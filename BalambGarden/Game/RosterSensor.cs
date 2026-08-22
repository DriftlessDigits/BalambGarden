using System;
using System.Collections.Generic;
using System.Linq;
using BalambGarden.Engine.Census;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BalambGarden.Game;

/// <summary>
/// The access roster, read from the game (spec: Permission Architecture, 2026-08-15;
/// receipts: captures/2026-08-15-roster-recon.log). Two corroborating sources, both
/// HouseId-shaped: the teleport list (raw Telepo - the SAME route the recon probe proved,
/// one instrument, one app, one truth) and HousingManager's owned-estate answers (which
/// also carry the chambers no teleport row names). The union is the set of estates the
/// game says Drift can reach; per Drift's v1 ruling that set is assumed actionable, and the
/// composed menu handles any per-verb weirdness gracefully at act time.
///
/// <para>Fail-open on staleness, fail-closed on shape: a refused refresh keeps the last
/// good roster (an estate does not stop being Drift's because a read misfired), but a row
/// that fits no receipted shape is dropped with its raw HouseId in the log.</para>
/// </summary>
internal static unsafe class RosterSensor
{
    private static AccessRoster cached = AccessRoster.Empty;
    private static DateTime nextRefreshUtc = DateTime.MinValue;
    private static bool warnedNoRead;

    internal static AccessRoster Current()
    {
        if (DateTime.UtcNow < nextRefreshUtc)
            return cached;
        nextRefreshUtc = DateTime.UtcNow.AddSeconds(60);

        try
        {
            if (Read() is { } fresh)
            {
                cached = fresh;
                warnedNoRead = false;
            }
            else if (!warnedNoRead)
            {
                warnedNoRead = true;
                Plugin.Log.Warning("[Roster] refresh refused (no player or game said no) - "
                    + $"holding the last good roster ({cached.Estates.Count} estate(s))");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Roster] read failed: {ex.Message} - holding the last good roster");
        }

        return cached;
    }

    private static AccessRoster? Read()
    {
        if (!Player.Available)
            return null;

        var telepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance();
        if (telepo == null || telepo->UpdateAetheryteList() == null)
            return null;

        var entries = new List<RosterEstate>();
        foreach (var row in telepo->TeleportList)
        {
            // 255 = a plain aetheryte; all-Fs = the game's own "none" (recon receipt).
            if ((int)row.EstateType == 255 || row.HouseId.Id == ulong.MaxValue)
                continue;
            Add(entries, row.HouseId, row.EstateType.ToString());
        }

        // The owned-estate reads: corroboration, plus the chambers the list has no row for.
        foreach (var (type, index) in new[]
        {
            (EstateType.FreeCompanyEstate, 0), (EstateType.PersonalChambers, 0),
            (EstateType.PersonalEstate, 0), (EstateType.SharedEstate, 0),
            (EstateType.SharedEstate, 1), (EstateType.ApartmentRoom, 0),
        })
        {
            var id = HousingManager.GetOwnedHouseId(type, index);
            if (id.Id == ulong.MaxValue)
                continue;
            Add(entries, id, type.ToString());
        }

        return new AccessRoster(entries);
    }

    private static void Add(List<RosterEstate> entries, HouseId houseId, string kind)
    {
        var key = AccessRoster.FromHouseParts(
            (ushort)houseId.TerritoryTypeId, houseId.WardIndex, houseId.PlotIndex,
            houseId.RoomNumber, houseId.IsApartment, houseId.ApartmentDivision);
        if (key is null)
        {
            Plugin.Log.Warning(
                $"[Roster] {kind} row fits no receipted shape - houseId=0x{houseId.Id:X16} - dropped");
            return;
        }
        if (entries.All(e => e.Key != key))
            entries.Add(new RosterEstate(key, kind));
    }
}
