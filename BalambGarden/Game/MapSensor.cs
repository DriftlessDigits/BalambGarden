using System;
using System.Collections.Generic;
using System.Linq;
using BalambGarden.Engine.Sensing;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BalambGarden.Game;

/// <summary>Stateless reader of the housing DataMap - the plant sensor for both
/// outdoor patches (48-byte entries, 8 beds) and indoor pots (same block, sub-entry 0).
/// Reading path receipt-verified in ReconProbe (probe branch) across 08-12/08-13.</summary>
internal static unsafe class MapSensor
{
    /// <summary>Entries whose bytes failed to decode on the last read - the honest
    /// "N unreadable" surface for the dashboard.</summary>
    internal static int UnreadableCount { get; private set; }

    /// <summary>The housing territory whose records we are standing in - the one place
    /// that choice is made. The DataMap and the furniture vector both hang off it, which
    /// is not incidental: they are two views of the same territory's contents, and reading
    /// them through two different routes could have them describing two different houses.
    ///
    /// <para>Internal for the same reason <see cref="ReadRawEntries"/> is: the debug probe
    /// prints fields identity has no use for (item id, stain), so it does its own walk of
    /// the vector - but it must walk the SAME territory the app is reading, or the two
    /// disagree and neither is evidence.</para></summary>
    internal static HousingFurnitureManager* CurrentFurniture()
    {
        var housing = HousingManager.Instance();
        if (housing == null || housing->CurrentTerritory == null)
            return null;

        var outdoor = housing->OutdoorTerritory;
        var indoor = housing->IndoorTerritory;
        return outdoor != null ? &outdoor->FurnitureManager
            : indoor != null ? &indoor->FurnitureManager
            : null;
    }

    /// <summary>
    /// Every placed piece of furniture: where it stands and which vector slot it is.
    ///
    /// <para>RECEIPT (2026-08-15, two estates, four pots): a flowerpot's furniture vector
    /// INDEX is its DataMap key. Papa's Place 15:47 - idx=180 and idx=181 (id 65979) among
    /// 182 entries, against DataMap keys 180 and 181, the Krakka twins whose keys were
    /// established independently by decode. Apartment 16:25 through 16:37 - idx=0 and idx=1
    /// (id 65981) against keys 0 and 1, established by appearance diff at planting. Both
    /// sites also show the furniture entry's position matching the game object's exactly as
    /// printed (object 'Oasis Flowerpot' &lt;-1.5,-0.0,-1.3&gt; == furniture idx=0).</para>
    ///
    /// <para>POTS ONLY. There are no receipts for beds, and beds do not need any: they bind
    /// through the receipt-confirmed diff-pattern join and have done since 08-12. Nothing
    /// here may be extended to them on the strength of an indoor coincidence.</para>
    /// </summary>
    internal static List<FurniturePlacement> ReadFurniture()
    {
        var placements = new List<FurniturePlacement>();
        var furniture = CurrentFurniture();
        if (furniture == null)
            return placements;

        foreach (var pointer in furniture->FurnitureVector)
        {
            var item = pointer.Value;
            if (item == null || item->Id == 0)
                continue;
            placements.Add(new FurniturePlacement(item->Index, item->Position));
        }
        return placements;
    }

    /// <summary>The one map read in the plugin. Internal rather than private so the debug
    /// probe reads through it too: an instrument that walked the DataMap by its own route
    /// could disagree with the app about what was there, and then neither is evidence.</summary>
    internal static Dictionary<int, byte[]> ReadRawEntries()
    {
        var result = new Dictionary<int, byte[]>();
        var furniture = CurrentFurniture();
        if (furniture == null)
            return result;

        foreach (var pair in furniture->ObjectManager.DataMap)
        {
            var data = pair.Item2;
            var bytes = new byte[MapFormat.OutdoorEntrySize];
            var p = (byte*)&data;
            var n = Math.Min(sizeof(HousingObjectManager.HousingObjectData), bytes.Length);
            for (var i = 0; i < n; i++)
                bytes[i] = p[i];
            result[(int)pair.Item1] = bytes;
        }
        return result;
    }

    /// <summary>Outdoor: every non-empty 8-bed entry decoded. Unclaimed data stays
    /// ephemeral - callers hold this for the session, never persist it.</summary>
    internal static Dictionary<int, IReadOnlyList<BedReading>> ReadOutdoor()
    {
        var result = new Dictionary<int, IReadOnlyList<BedReading>>();
        UnreadableCount = 0;
        if (EstateSensor.IsInside())
            return result;

        foreach (var (key, bytes) in ReadRawEntries())
        {
            try
            {
                var beds = MapFormat.DecodeOutdoorEntry(bytes);
                if (beds.Any(b => b.Occupied))
                    result[key] = beds;
            }
            catch (Exception ex)
            {
                UnreadableCount++;
                Plugin.Log.Warning($"[MapSensor] key={key} unreadable: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>Indoor: single-plant pot entries only; multi-slot furniture rejected
    /// by the decoder (aquariums etc., 08-13).</summary>
    internal static Dictionary<int, PotReading> ReadIndoor()
    {
        var result = new Dictionary<int, PotReading>();
        UnreadableCount = 0;
        if (!EstateSensor.IsInside())
            return result;

        foreach (var (key, bytes) in ReadRawEntries())
        {
            try
            {
                if (MapFormat.DecodeIndoorEntry(
                        bytes, s => Plugin.Tables.SeedIdBySpeciesIndex(s) is not null)
                    is { Occupied: true } pot)
                    result[key] = pot;
            }
            catch (Exception ex)
            {
                UnreadableCount++;
                Plugin.Log.Warning($"[MapSensor] indoor key={key} unreadable: {ex.Message}");
            }
        }
        return result;
    }
}
