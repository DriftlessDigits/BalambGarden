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

    /// <summary>Hex-dumps the first bytes of each nearby bed's native GameObject.</summary>
    internal static void DumpBedStructs()
    {
        const int windowBytes = 0x220;
        const int maxObjects = 20;

        var dumped = 0;
        foreach (var sighting in GardenScanner.NearbyEventObjects())
        {
            if (sighting.DataId != GardenScanner.GardenBedDataId)
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
