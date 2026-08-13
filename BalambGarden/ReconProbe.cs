using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BalambGarden;

/// <summary>
/// One-shot recon instruments, log-only. Two questions:
/// (1) What does estate identity look like from HousingManager (ward/plot/room,
///     inside/outside) - the rebuild's estate-roster key.
/// (2) Do bed objects carry plant/growth state passively in native memory? The
///     dump is a diff instrument: capture beds in different states (young plant,
///     ripe, empty, neighbor's) and diff the hex - the bytes that differ per
///     state ARE the answer.
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
    /// Probe v2: walks the housing territory's own records - the furniture vector
    /// (item ids + positions) and the HousingObjectManager DataMap whose entries
    /// hold 8 value-sets each (8 = beds per patch; plant id + state suspects).
    /// </summary>
    internal static void DumpHousingRecords()
    {
        try
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

            var mapCount = 0;
            foreach (var pair in furniture->ObjectManager.DataMap)
            {
                var data = pair.Item2;
                var raw = new StringBuilder();
                var p = (byte*)&data;
                for (var i = 0; i < sizeof(HousingObjectManager.HousingObjectData); i++)
                    raw.Append($"{p[i]:X2} ");
                Plugin.Log.Information($"[Probe] datamap key={pair.Item1} bytes: {raw}");

                // Decoded view: Value1 (u16) = species index (Lotlab join, desk-verified
                // 0x11=Mirror Apple / 0x41=Old World Fig); V2-V4 = stage/water suspects.
                var occupied = 0;
                var decoded = new StringBuilder();
                for (var slot = 0; slot < 8; slot++)
                {
                    var vs = data.ValueSets[slot];
                    if (vs.Value1 == 0 && vs.Value2 == 0 && vs.Value3 == 0 && vs.Value4 == 0)
                        continue;
                    occupied++;
                    decoded.Append(
                        $"\n[Probe]   slot {slot}: {SpeciesTable.Name(vs.Value1)} "
                        + $"(0x{vs.Value1:X2}) v2={vs.Value2:X2} v3={vs.Value3:X2} v4={vs.Value4:X2} v5={vs.Value5:X2}");
                }
                if (occupied > 0)
                    Plugin.Log.Information($"[Probe] key={pair.Item1} decoded ({occupied}/8 occupied):{decoded}");

                mapCount++;
                if (mapCount >= 80)
                {
                    Plugin.Log.Information("[Probe] datamap cap 80 reached");
                    break;
                }
            }
            Plugin.Log.Information($"[Probe] datamap entries logged: {mapCount}");

            // Key<->patch binding hypothesis (2026-08-12): map key low byte == patch
            // GimmickId low byte (FC receipts matched 3/3: keys 1293/1313/1319 ->
            // 13/33/39 vs founding GimmickId lows 13/39/33). Correlate in-reach beds.
            foreach (var sighting in GardenScanner.NearbyEventObjects())
            {
                if (sighting.DataId != GardenScanner.GardenBedDataId)
                    continue;
                var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)sighting.Object.Address;
                var gimmick = native->GimmickId;
                Plugin.Log.Information(
                    $"[Probe] bed gimmick=0x{gimmick:X8} low=0x{gimmick & 0xFF:X2} ({gimmick & 0xFF}) "
                    + $"dist={sighting.Distance:F1}y pos={sighting.Object.Position:F1}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Probe] housing records dump failed: {ex}");
        }
    }

    /// <summary>Hex-dumps the first bytes of each nearby bed's native GameObject.</summary>
    internal static void DumpBedStructs()
    {
        const int windowBytes = 0x220;
        const int maxObjects = 20;

        var dumped = 0;
        foreach (var sighting in GardenScanner.NearbyEventObjects())
        {
            // Outdoor beds by DataId; indoors, any close housing object (pots have
            // per-model DataIds, so proximity is the honest filter).
            var isBed = sighting.DataId == GardenScanner.GardenBedDataId;
            var isCloseHousingObject =
                sighting.Kind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.HousingEventObject
                && sighting.Distance <= 10f;
            if (!isBed && !isCloseHousingObject)
                continue;
            if (dumped >= maxObjects)
            {
                Plugin.Log.Information($"[Probe] dump cap ({maxObjects}) reached - move closer to the beds you care about");
                break;
            }

            try
            {
                var address = sighting.Object.Address;
                Plugin.Log.Information(
                    $"[Probe] bed entity={sighting.Object.EntityId:X8} index={sighting.Object.ObjectIndex} "
                    + $"pos={sighting.Object.Position:F1} dist={sighting.Distance:F2}y addr={address:X}");

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
                Plugin.Log.Warning($"[Probe] bed dump failed at {sighting.Distance:F1}y: {ex.Message}");
            }
        }

        Plugin.Log.Information($"[Probe] dumped {dumped} bed objects ({windowBytes:X} bytes each)");
    }
}
