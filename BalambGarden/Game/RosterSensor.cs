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
/// composed menu handles any per-verb weirdness gracefully at act time. A reader, never a
/// writer: in debug builds, <c>LastRead</c> is what the census pump saves for readers outside
/// the game.
///
/// <para>Read on ARRIVALS, never on a clock (ruled 2026-11-08): plugin load, login, and every
/// zone line. Access changes when the game grants or drops an estate, which a player meets on
/// arrival, so between arrivals the roster is held, not re-read. An arrival does not trust one
/// read: the teleport list is settle-async state, so the sensor reads every
/// <see cref="ReadSpacing"/> from the first answer until <see cref="SettleWindow"/> has passed
/// and keeps the last (each read logged at DBG, so the field shows whether the window earns
/// its keep). Named cost: an access change while standing still shows at the next zone line.</para>
///
/// <para>Fail-open on staleness, fail-closed on shape: a refused read keeps the last
/// good roster (an estate does not stop being Drift's because a read misfired) and retries,
/// but a row that fits no receipted shape is dropped with its raw HouseId in the log.</para>
/// </summary>
internal static unsafe class RosterSensor
{
    private static readonly TimeSpan ReadSpacing = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(10);

    private static AccessRoster cached = AccessRoster.Empty;
#if DEBUG
    private static DateTimeOffset? cachedReadAt;
#endif
    // No read before this instant. Bench pins it to DateTime.MaxValue to hold a roster it set,
    // so the name and meaning stay.
    private static DateTime nextRefreshUtc = DateTime.MinValue;
    // True from an arrival until the settle window closes; plugin load counts as an arrival.
    private static bool settling = true;
    private static DateTime? firstAnswerUtc;
    private static int settleReads;
    // A refusal is expected mid-zone (no player yet: field 2026-11-08, every zone into a house),
    // so the first refusal of an arrival logs at DBG; only a refusal still standing
    // RefusalWarnAfter later warns. Both reset on an answer and on an arrival.
    private static readonly TimeSpan RefusalWarnAfter = TimeSpan.FromSeconds(30);
    private static DateTime? firstRefusalUtc;
    private static bool warnedNoRead;

    /// <summary>An arrival (login or zone line): read the roster again until it settles.</summary>
    internal static void NoteArrival()
    {
        settling = true;
        firstAnswerUtc = null;
        settleReads = 0;
        firstRefusalUtc = null;
        warnedNoRead = false;
        nextRefreshUtc = DateTime.MinValue;
    }

    internal static AccessRoster Current()
    {
        var now = DateTime.UtcNow;
        if (!settling || now < nextRefreshUtc)
            return cached;
        nextRefreshUtc = now + ReadSpacing;

        try
        {
            if (Read() is { } fresh)
            {
                firstRefusalUtc = null;
                warnedNoRead = false;
                firstAnswerUtc ??= now;
                settleReads++;
                var changed = !fresh.Estates.SequenceEqual(cached.Estates);
                cached = fresh;
                Plugin.Log.Debug($"[Roster] settle read {settleReads}: {fresh.Estates.Count} estate(s)"
                    + (changed ? " (changed)" : ""));
                if (now - firstAnswerUtc >= SettleWindow)
                {
                    settling = false;
#if DEBUG
                    cachedReadAt = DateTimeOffset.UtcNow;
#endif
                    Plugin.Log.Debug($"[Roster] settled: {cached.Estates.Count} estate(s) after {settleReads} read(s)");
                }
            }
            else if (firstRefusalUtc is null)
            {
                firstRefusalUtc = now;
                Plugin.Log.Debug("[Roster] read refused (no player yet, or game said no) - "
                    + $"holding the last good roster ({cached.Estates.Count} estate(s)), retrying");
            }
            else if (!warnedNoRead && now - firstRefusalUtc >= RefusalWarnAfter)
            {
                warnedNoRead = true;
                Plugin.Log.Warning($"[Roster] read still refused {RefusalWarnAfter.TotalSeconds:0}s on - "
                    + $"holding the last good roster ({cached.Estates.Count} estate(s)), retrying");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Roster] read failed: {ex.Message} - holding the last good roster, retrying");
        }

        return cached;
    }

#if DEBUG
    /// <summary>The cached roster and when the game answered it, without reading again; null
    /// until the game has answered once this session (an empty cache is not an answer), and
    /// null while an arrival is still settling (a half-settled list is not saved).
    /// Debug builds only: its one reader saves the roster for tooling outside the game.</summary>
    internal static (AccessRoster Roster, DateTimeOffset ReadAt)? LastRead
        => !settling && cachedReadAt is { } at ? (cached, at) : null;
#endif

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
