using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>Water status of a claimed bed. <see cref="NotApplicable"/> is appended last so
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
        // Pots DO wilt (08-16, Drift's Krakka twins), but they also BROADCAST it - the
        // live map's b4=1 is the game's own claim, so pot wilt is observed, never
        // clocked. The prediction machinery below is for beds, where the byte hunt
        // closed refuted and a clock is all there is.
        if (bed.IsPot)
            return WaterState.NotApplicable;

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

    /// <summary>Where the thirst clock starts: the last heard watering, else the witnessed
    /// sowing (dashboard rethink ruling 3, 2026-11-08: a sowing starts the clock, so a fresh
    /// bed gets a deadline instead of a "?"). A planting always rebases at its own instant,
    /// so a surviving watering is newer than the sowing and "watering, else sowing" is the
    /// later of the two. Null where the clock makes no claim: pots (observed, never
    /// clocked), ripe beds (cannot wilt), and a bed with neither receipt.</summary>
    public static DateTimeOffset? ClockStart(ClaimedBed bed)
        => bed.IsPot || bed.Latest is { Stage: >= 4 } ? null : bed.LastTended ?? bed.PlantedAt;

    /// <summary>The water deadline: when this bed reaches its crop's wilt hours if nobody
    /// waters it. Null wherever <see cref="ClockStart"/> is.</summary>
    public static DateTimeOffset? WaterBy(ClaimedBed bed, Crop crop)
        => ClockStart(bed)?.AddHours(crop.WiltHours);

    /// <summary>A bed whose watering was never heard: its deadline, if it has one, rests on the
    /// sowing. Beds only - a pot makes no water claim to rest on anything.</summary>
    public static bool WateringUnheard(ClaimedBed bed) => !bed.IsPot && bed.LastTended is null;

    /// <summary>When this bed's plant becomes unrecoverable if nobody waters: the clock's start +
    /// WitherHours (2026-08-18, the Allagan Melons - wilt is a countdown, not an end
    /// state, and the deadline deserves saying out loud). Null wherever the clock makes
    /// no claim (<see cref="ClockStart"/>).</summary>
    public static DateTimeOffset? DiesAt(ClaimedBed bed, Crop crop)
        => ClockStart(bed)?.AddHours(crop.WitherHours);
}
