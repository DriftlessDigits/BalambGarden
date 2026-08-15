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

    /// <summary>The one map read in the plugin. Internal rather than private so the debug
    /// probe reads through it too: an instrument that walked the DataMap by its own route
    /// could disagree with the app about what was there, and then neither is evidence.</summary>
    internal static Dictionary<int, byte[]> ReadRawEntries()
    {
        var result = new Dictionary<int, byte[]>();
        var housing = HousingManager.Instance();
        if (housing == null || housing->CurrentTerritory == null)
            return result;

        var outdoor = housing->OutdoorTerritory;
        var indoor = housing->IndoorTerritory;
        HousingFurnitureManager* furniture =
            outdoor != null ? &outdoor->FurnitureManager
            : indoor != null ? &indoor->FurnitureManager
            : null;
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
