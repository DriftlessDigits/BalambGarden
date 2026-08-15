using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BalambGarden.Engine.Sensing;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace BalambGarden.Game;

internal readonly record struct BedObject(
    IGameObject Object, BedGimmick Gimmick, float Distance, bool Targetable)
{
    internal bool InReach => Targetable && Distance <= ObjectSensor.EventObjRange;
}

internal sealed record PatchGroup(
    ushort PatchId, int Ordinal, Vector3 Center, List<BedObject> Beds, float Distance)
{
    internal bool InReach => Distance <= ObjectSensor.EventObjRange;
}

internal readonly record struct PotObject(IGameObject Object, string Name, float Distance)
{
    internal bool InReach => Distance <= ObjectSensor.HousingEventObjRange;
}

/// <summary>Nearby bed/pot objects. Patches group by GimmickId patch-id (the game's
/// own identity, receipt-verified 08-12/08-13) - never by position clustering.</summary>
internal static unsafe class ObjectSensor
{
    internal const uint GardenBedDataId = 2003757;
    internal const float EventObjRange = 4.6f;          // field-verified 08-11
    internal const float HousingEventObjRange = 6.5f;

    /// <summary>How far the working patch sweep looks, when nobody says otherwise. Sam's
    /// ruling 08-14 - the neighbour's twin ordinal sat at 37.9y, and players know where
    /// their own plots are, so a far own-patch simply reappears when they walk toward it.
    /// It is now a setting (<see cref="Configuration.PatchScanRadius"/>); this constant
    /// stays as the default that setting ships with. Recon keeps the wide 40y view on
    /// purpose.</summary>
    internal const float PatchSweepRange = 20f;

    /// <summary>The player's sweep radius, clamped to the range the slider offers - a
    /// config file edited by hand cannot talk the sensor into a nonsense distance.</summary>
    internal static float SweepRange =>
        Math.Clamp(Plugin.Configuration.PatchScanRadius, MinScanRadius, MaxScanRadius);

    internal const float MinScanRadius = 5f;
    internal const float MaxScanRadius = 40f;

    internal static List<BedObject> NearbyBeds(float maxDistance = 40f)
    {
        var beds = new List<BedObject>();
        if (!Player.Available || Player.Object is not { } me)
            return beds;

        foreach (var obj in Svc.Objects)
        {
            if (obj is null || !obj.IsValid())
                continue;
            if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject))
                continue;
            if (obj.BaseId != GardenBedDataId)
                continue;

            var distance = Vector3.Distance(me.Position, obj.Position);
            if (distance > maxDistance)
                continue;

            var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
            if (native == null)
                continue;

            beds.Add(new BedObject(obj, GimmickId.Decode(native->GimmickId), distance, obj.IsTargetable));
        }
        return beds;
    }

    internal static List<PatchGroup> Patches(float? maxDistance = null)
        => NearbyBeds(maxDistance ?? SweepRange)
            .GroupBy(b => b.Gimmick.PatchId)
            .Select(g =>
            {
                var beds = g.OrderBy(b => b.Gimmick.BedIndex).ToList();
                // All beds in a patch share the centre position (08-11): in range of
                // the centre IS in range of every bed.
                return new PatchGroup(
                    g.Key, beds[0].Gimmick.PatchOrdinal, beds[0].Object.Position,
                    beds, beds.Min(b => b.Distance));
            })
            .OrderBy(p => p.Ordinal)
            .ToList();

    /// <summary>Indoor pots by name ("Flowerpot" models). Pots are dumb props with
    /// per-model DataIds (08-13) - the name filter is the honest v1 identifier; a
    /// pot the filter misses simply shows no verbs, never a wrong one.</summary>
    internal static List<PotObject> NearbyPots(float maxDistance = 20f)
    {
        var pots = new List<PotObject>();
        if (!EstateSensor.IsInside() || !Player.Available || Player.Object is not { } me)
            return pots;

        foreach (var obj in Svc.Objects)
        {
            if (obj is null || !obj.IsValid() || !obj.IsTargetable)
                continue;
            if (obj.ObjectKind != ObjectKind.HousingEventObject)
                continue;
            var name = obj.Name.TextValue;
            if (!name.Contains("Flowerpot", StringComparison.OrdinalIgnoreCase))
                continue;

            var distance = Vector3.Distance(me.Position, obj.Position);
            if (distance > maxDistance)
                continue;
            pots.Add(new PotObject(obj, name, distance));
        }
        return pots.OrderBy(p => p.Distance).ToList();
    }

#if DEBUG
    /// <summary>Recon sweep (debug builds only): beds by DataId plus ANY close housing
    /// object. Deliberately wider than <see cref="NearbyPots"/> - the probe's job is
    /// partly to find the pot models the name filter does not know yet, so it must be
    /// able to see an object the app cannot name. Still routed through this sensor so
    /// the instrument and the app share one object route.</summary>
    internal static List<(IGameObject Object, float Distance)> ReconObjects(
        float bedRange, float housingRange)
    {
        var found = new List<(IGameObject, float)>();
        if (!Player.Available || Player.Object is not { } me)
            return found;

        foreach (var obj in Svc.Objects)
        {
            if (obj is null || !obj.IsValid())
                continue;
            if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject))
                continue;

            var distance = Vector3.Distance(me.Position, obj.Position);
            var isBed = obj.BaseId == GardenBedDataId && distance <= bedRange;
            var isCloseHousingObject =
                obj.ObjectKind == ObjectKind.HousingEventObject && distance <= housingRange;
            if (!isBed && !isCloseHousingObject)
                continue;

            found.Add((obj, distance));
        }
        return found.OrderBy(f => f.Item2).ToList();
    }
#endif
}
