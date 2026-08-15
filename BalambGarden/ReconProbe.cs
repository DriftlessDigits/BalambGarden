#if DEBUG
using System;
using System.Text;
using BalambGarden.Engine.Sensing;
using BalambGarden.Game;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BalambGarden;

/// <summary>
/// One-shot recon instruments, log-only, DEBUG builds only - a Release plugin carries
/// none of this. Two questions it was built to answer:
/// (1) What does estate identity look like from HousingManager (ward/plot/room,
///     inside/outside) - the rebuild's estate-roster key.
/// (2) Do bed objects carry plant/growth state passively in native memory? The
///     dump is a diff instrument: capture beds in different states (young plant,
///     ripe, empty, neighbor's) and diff the hex - the bytes that differ per
///     state ARE the answer.
///
/// <para>Both were answered (08-12/08-13 captures) and the answers became MapSensor +
/// MapFormat + ObjectSensor. It is kept because the next time the game moves something,
/// an instrument that no longer exists cannot be switched on.</para>
///
/// <para>IMPORTANT: the probe reads through the SAME sensors the app uses
/// (<see cref="MapSensor.ReadRawEntries"/>, <see cref="ObjectSensor"/>). An instrument
/// with its own route could disagree with the app about what was on screen, and then
/// neither reading is evidence. The raw hex is dumped verbatim beside the decode, so a
/// decoder bug is visible rather than hidden.</para>
/// </summary>
internal static unsafe class ReconProbe
{
    internal static void LogHousingLocation()
    {
        try
        {
            var housing = HousingManager.Instance();
            if (housing == null)
            {
                Plugin.Log.Information("[Probe] HousingManager: null (not in a housing area?)");
                return;
            }

            Plugin.Log.Information(
                $"[Probe] Housing: territory={Plugin.ClientState.TerritoryType} "
                + $"ward={housing->GetCurrentWard()} plot={housing->GetCurrentPlot()} "
                + $"room={housing->GetCurrentRoom()} inside={housing->IsInside()}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Probe] housing location read failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Walks the housing territory's own records: the furniture vector (item ids +
    /// positions, read directly - no sensor covers it) and the gardening DataMap, read
    /// through MapSensor and decoded through MapFormat exactly as the census does.
    /// </summary>
    internal static void DumpHousingRecords()
    {
        try
        {
            DumpFurnitureVector();
            DumpDataMap();
            DumpBedGimmicks();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Probe] housing records dump failed: {ex}");
        }
    }

    /// <summary>Furniture positions/item ids. No sensor reads this - it is how the probe
    /// answers "what furniture is even here", so it walks the vector itself.</summary>
    private static void DumpFurnitureVector()
    {
        var housing = HousingManager.Instance();
        if (housing == null || housing->CurrentTerritory == null)
        {
            Plugin.Log.Information("[Probe] no housing territory");
            return;
        }

        var outdoor = housing->OutdoorTerritory;
        var indoor = housing->IndoorTerritory;
        HousingFurnitureManager* furniture =
            outdoor != null ? &outdoor->FurnitureManager
            : indoor != null ? &indoor->FurnitureManager
            : null;
        if (furniture == null)
        {
            Plugin.Log.Information("[Probe] no furniture manager (workshop territory?)");
            return;
        }

        var me = ECommons.GameHelpers.Player.Object;
        var myPos = me?.Position ?? default;

        Plugin.Log.Information(
            $"[Probe] furniture vector: {furniture->FurnitureVector.Count} entries, "
            + $"object array count: {furniture->ObjectManager.ObjectArray.ObjectCount}");

        var logged = 0;
        foreach (var pointer in furniture->FurnitureVector)
        {
            var item = pointer.Value;
            if (item == null || item->Id == 0)
                continue;
            var distance = System.Numerics.Vector3.Distance(myPos, item->Position);
            if (distance > 45f)
                continue;

            Plugin.Log.Information(
                $"[Probe] furniture idx={item->Index} id={item->Id} stain={item->Stain} "
                + $"pos={item->Position:F1} dist={distance:F1}y");
            logged++;
        }
        Plugin.Log.Information($"[Probe] furniture within 45y: {logged}");
    }

    /// <summary>
    /// The gardening DataMap: raw hex per entry (kept verbatim - the hex is the receipt),
    /// then the same bytes decoded by MapFormat and named by the Engine's species table.
    /// Both outdoor (8 beds) and indoor (pot, sub-entry 0) decodes are printed, because
    /// the probe is often the thing establishing WHICH one an entry is.
    /// </summary>
    private static void DumpDataMap()
    {
        var entries = MapSensor.ReadRawEntries();
        Plugin.Log.Information($"[Probe] datamap: {entries.Count} entries (via MapSensor)");

        var logged = 0;
        foreach (var (key, bytes) in entries)
        {
            var raw = new StringBuilder();
            foreach (var b in bytes)
                raw.Append($"{b:X2} ");
            Plugin.Log.Information($"[Probe] datamap key={key} bytes: {raw}");

            DecodeAsOutdoor(key, bytes);
            DecodeAsIndoor(key, bytes);

            logged++;
            if (logged >= 80)
            {
                Plugin.Log.Information("[Probe] datamap cap 80 reached");
                break;
            }
        }
        Plugin.Log.Information($"[Probe] datamap entries logged: {logged}");
    }

    private static void DecodeAsOutdoor(int key, byte[] bytes)
    {
        try
        {
            var beds = MapFormat.DecodeOutdoorEntry(bytes);
            var occupied = 0;
            var decoded = new StringBuilder();
            foreach (var bed in beds)
            {
                if (!bed.Occupied)
                    continue;
                occupied++;
                decoded.Append(
                    $"\n[Probe]   slot {bed.Slot}: {Plugin.Tables.SpeciesName(bed.SpeciesIndex)} "
                    + $"(0x{bed.SpeciesIndex:X2}) stage={bed.Stage:X2} extra={bed.Extra:X2}");
            }
            if (occupied > 0)
                Plugin.Log.Information($"[Probe] key={key} outdoor decode ({occupied}/8 occupied):{decoded}");
        }
        catch (Exception ex)
        {
            // Same failure the census would report as "unreadable" - said out loud here.
            Plugin.Log.Information($"[Probe] key={key} outdoor decode failed: {ex.Message}");
        }
    }

    private static void DecodeAsIndoor(int key, byte[] bytes)
    {
        try
        {
            if (MapFormat.DecodeIndoorEntry(bytes, s => Plugin.Tables.SeedIdBySpeciesIndex(s) is not null)
                is not { Occupied: true } pot)
                return;   // multi-slot furniture or an empty pot - not a pot reading

            Plugin.Log.Information(
                $"[Probe] key={key} indoor decode: {Plugin.Tables.SpeciesName(pot.SpeciesIndex)} "
                + $"(0x{pot.SpeciesIndex:X2}) stage={pot.Stage:X2} extra={pot.Extra:X2} "
                + $"recognized={pot.Recognized}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Information($"[Probe] key={key} indoor decode failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Key&lt;-&gt;patch binding (2026-08-12, two-estate verified): GimmickId =
    /// [bed 0-7][patch ordinal][patch-id u16]. Map keys preserve the patch-ids'
    /// PAIRWISE DIFFS per estate (Chelsea +6,+1 -&gt; keys 110/116/117; FC +20,+6
    /// -&gt; 1293/1313/1319) with a per-plot offset. The earlier low-byte-equality
    /// rule was an FC offset coincidence - killed at Chelsea's. Census join:
    /// diff-pattern shortlist, confirm with one tend receipt, cache (keys stable).
    /// <para>Recon keeps the wide 40y sweep on purpose; the working sensor caps at
    /// <see cref="ObjectSensor.PatchSweepRange"/>.</para>
    /// </summary>
    private static void DumpBedGimmicks()
    {
        foreach (var bed in ObjectSensor.NearbyBeds(40f))
        {
            Plugin.Log.Information(
                $"[Probe] bed patch=0x{bed.Gimmick.PatchId:X4} ordinal={bed.Gimmick.PatchOrdinal} "
                + $"slot={bed.Gimmick.BedIndex} dist={bed.Distance:F1}y "
                + $"targetable={bed.Targetable} pos={bed.Object.Position:F1}");
        }
    }

    /// <summary>Hex-dumps the first bytes of each nearby bed's native GameObject.</summary>
    internal static void DumpBedStructs()
    {
        const int windowBytes = 0x220;
        const int maxObjects = 32;

        var dumped = 0;
        // Outdoor beds by the sensor's own DataId filter; indoors, any close housing
        // object (pots have per-model DataIds, so proximity is the honest filter and the
        // probe deliberately keeps a wider net than the app's name-filtered pot sensor).
        foreach (var (obj, distance) in ObjectSensor.ReconObjects(bedRange: 40f, housingRange: 10f))
        {
            if (dumped >= maxObjects)
            {
                Plugin.Log.Information($"[Probe] dump cap ({maxObjects}) reached - move closer to the beds you care about");
                break;
            }

            try
            {
                var address = obj.Address;
                Plugin.Log.Information(
                    $"[Probe] object entity={obj.EntityId:X8} index={obj.ObjectIndex} "
                    + $"name='{obj.Name.TextValue}' pos={obj.Position:F1} dist={distance:F2}y addr={address:X}");

                var bytes = new byte[windowBytes];
                System.Runtime.InteropServices.Marshal.Copy(address, bytes, 0, windowBytes);
                for (var offset = 0; offset < windowBytes; offset += 16)
                {
                    var line = new StringBuilder($"[Probe]   +{offset:X3}: ");
                    for (var i = 0; i < 16 && offset + i < windowBytes; i++)
                        line.Append($"{bytes[offset + i]:X2} ");
                    Plugin.Log.Information(line.ToString());
                }

                dumped++;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[Probe] object dump failed at {distance:F1}y: {ex.Message}");
            }
        }

        Plugin.Log.Information($"[Probe] dumped {dumped} objects ({windowBytes:X} bytes each)");
    }
}
#endif
