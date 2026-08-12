using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace BalambGarden;

/// <summary>A nearby interactable world object, as seen by the recon scan.</summary>
internal readonly record struct GardenSighting(
    IGameObject Object,
    string Name,
    ObjectKind Kind,
    uint DataId,
    float Distance,
    bool Targetable)
{
    /// <summary>Whether a tend would be honoured from where the player stands.</summary>
    internal bool InReach
        => Targetable && Distance <= GardenScanner.ValidRangeFor(Kind);
}

/// <summary>
/// Read-only scan of nearby event objects. Recon-first: lists everything of the two
/// kinds housing interactables come in, so the live session at the garden can tell us
/// what beds actually look like (kind, name, DataId) before we filter to them.
/// </summary>
internal static class GardenScanner
{
    // Field-proven interact ranges (Scrooge/AutoRetainer lineage): RAW centre-to-centre
    // distance, no hitbox subtraction. Whether beds behave like housing objects is a
    // live-recon question; both constants ride along until it is answered.
    internal const float EventObjRange = 4.6f;
    internal const float HousingEventObjRange = 6.5f;

    internal static float ValidRangeFor(ObjectKind kind)
        => kind == ObjectKind.HousingEventObject ? HousingEventObjRange : EventObjRange;

    internal static List<GardenSighting> NearbyEventObjects(float maxDistance = 40f)
    {
        var sightings = new List<GardenSighting>();
        if (!Player.Available || Player.Object is not { } me)
            return sightings;

        foreach (var obj in Svc.Objects)
        {
            if (obj is null || !obj.IsValid())
                continue;
            if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject))
                continue;

            var distance = Vector3.Distance(me.Position, obj.Position);
            if (distance > maxDistance)
                continue;

            var name = obj.Name.TextValue;
            sightings.Add(new GardenSighting(
                obj,
                string.IsNullOrEmpty(name) ? "(unnamed)" : name,
                obj.ObjectKind,
                obj.BaseId,
                distance,
                obj.IsTargetable));
        }

        sightings.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return sightings;
    }
}
