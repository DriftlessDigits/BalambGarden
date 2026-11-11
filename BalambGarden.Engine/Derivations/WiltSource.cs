using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>Water status of a claimed bed or pot. <see cref="NotApplicable"/> is appended last so
/// the existing members keep their numeric values.</summary>
public enum WaterState { Unknown, Watered, Due, Overdue, Danger, NotApplicable }

/// <summary>The wilt seam (spec): v1 derives from the tend clock; a future memory
/// sensor is just another implementation writing better-provenance data.</summary>
public interface IWiltSource
{
    WaterState StateFor(ClaimedBed bed, Crop crop, DateTimeOffset now);
}

public sealed class ClockWiltSource : IWiltSource
{
    public const double DueFraction = 0.75;

    public WaterState StateFor(ClaimedBed bed, Crop crop, DateTimeOffset now)
    {
        // Pots run the same clock as beds (dashboard rethink ruling 23, 2026-11-10): a crop
        // wilts wherever it grows (08-16, Drift's Krakka twins), and a pot nobody stands
        // beside shows its wilt to no one - four onion pots died that way across a
        // maintenance. Where the game does speak (a pot's live wilt flag in its room, a kept
        // sighting), the garden board lets it win the look. Flowerpot flowers never wilt:
        // they have no crop row, so the caller never reaches here for them.

        // A fully grown crop cannot wilt or die (the same community table the wilt hours
        // come from), so a bed last SEEN ripe makes no water claim however stale its tend
        // clock - the live stage read outranks the derived clock (2026-08-18, the
        // "DANGER · ripe now" screenshot: Gardener's watering was invisible to the ledger).
        if (bed.Latest is { Stage: >= 4 })
            return WaterState.NotApplicable;

        if (ClockStart(bed) is not { } start)
            return WaterState.Unknown;

        var hours = (now - start).TotalHours;
        var dangerAt = crop.WiltHours + (crop.WitherHours - crop.WiltHours) / 2.0;

        return hours switch
        {
            _ when hours < crop.WiltHours * DueFraction => WaterState.Watered,
            _ when hours < crop.WiltHours => WaterState.Due,
            _ when hours < dangerAt => WaterState.Overdue,
            _ => WaterState.Danger,
        };
    }

    /// <summary>Where the wilt clock starts: the last heard watering, else the witnessed
    /// sowing (dashboard rethink ruling 3, 2026-11-08: a sowing starts the clock, so a fresh
    /// bed gets a deadline instead of a "?"). A planting always rebases at its own instant,
    /// so a surviving watering is newer than the sowing and "watering, else sowing" is the
    /// later of the two. Beds and pots alike (ruling 23). Null where the clock makes no claim:
    /// a ripe plant (cannot wilt), a dead one (stage 5, past wilting - the same test covers
    /// both), and a record with neither receipt.</summary>
    public static DateTimeOffset? ClockStart(ClaimedBed bed)
        => bed.Latest is { Stage: >= GardenStage.Ripe } ? null : bed.LastTended ?? bed.PlantedAt;

    /// <summary>The water deadline: when this bed or pot reaches its crop's wilt hours if nobody
    /// waters it. Null wherever <see cref="ClockStart"/> is.</summary>
    public static DateTimeOffset? WaterBy(ClaimedBed bed, Crop crop)
        => ClockStart(bed)?.AddHours(crop.WiltHours);

    /// <summary>A bed or pot with no watering recorded: its deadline, if it has one, rests on
    /// the sowing.</summary>
    public static bool TendUnheard(ClaimedBed bed) => bed.LastTended is null;

    /// <summary>When this bed's or pot's plant becomes unrecoverable if nobody waters: the clock's start +
    /// WitherHours (2026-08-18, the Allagan Melons - wilt is a countdown, not an end
    /// state, and the deadline deserves saying out loud). Null wherever the clock makes
    /// no claim (<see cref="ClockStart"/>).</summary>
    public static DateTimeOffset? DiesAt(ClaimedBed bed, Crop crop)
        => ClockStart(bed)?.AddHours(crop.WitherHours);
}
