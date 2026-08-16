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
    /// <para>DEMOTED from identity duty (08-16): the 08-15 "vector index == DataMap key"
    /// receipts (Papa's 180/181, apartment 0/1) were the special case of a house whose
    /// furniture slots were never recycled. The redecorated FC estate is the
    /// counterexample - pot data at keys 208/227/392/393 against pot vector entries at
    /// idx 378/379, two pots not in the vector at all. Identity now reads
    /// HousingObject.HousingFurnitureIndex off the pot object itself
    /// (<see cref="ObjectSensor.AllPots"/>); this walk remains for the debug probe.</para>
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
            placements.Add(new FurniturePlacement(item->Index, item->Position, item->Id));
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

    /// <summary>DataMap keys seen on the last indoor read that the pot-gate turned away -
    /// furniture holding plant-shaped bytes without being a flowerpot (vases, aquariums,
    /// canvases, partitions; 18 of them at the FC estate, 08-16). The census prunes any
    /// ledger row still wearing one of these keys.</summary>
    internal static IReadOnlyList<int> LastPhantomKeys { get; private set; } = [];

    /// <summary>Indoor: flowerpot entries only. The pot-gate decides membership off the pot
    /// OBJECTS (BaseId = Flowerpot sheet row, key = the object's own HousingFurnitureIndex -
    /// 08-16 receipts; decor vases and aquariums DECODE pot-shaped, so the decoder cannot be
    /// the gate, and the furniture VECTOR index diverges from the key at recycled-slot
    /// houses, so the vector cannot be either). When the object table has not settled yet
    /// this answers empty and flags no phantoms - never a verdict the world has not given.</summary>
    internal static Dictionary<int, PotReading> ReadIndoor()
    {
        var result = new Dictionary<int, PotReading>();
        UnreadableCount = 0;
        LastPhantomKeys = [];
        if (!EstateSensor.IsInside())
            return result;

        var raw = ReadRawEntries();
        var pots = ObjectSensor.AllPots();
        if (!ObjectSensor.SawHousingObjects)
            return result;

        var potKeys = pots.Where(p => p.MapKey is not null).Select(p => p.MapKey!.Value).ToHashSet();
        LastPhantomKeys = raw.Keys.Where(k => !potKeys.Contains(k)).ToList();

        foreach (var (key, bytes) in raw)
        {
            if (!potKeys.Contains(key))
                continue;
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
