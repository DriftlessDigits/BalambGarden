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
        // Pots DO wilt (08-16, Papa's Krakka twins), but they also BROADCAST it - the
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

        if (bed.LastTended is not { } tended)
            return WaterState.Unknown;

        var hours = (now - tended).TotalHours;
        var dangerAt = crop.WiltHours + (crop.WitherHours - crop.WiltHours) / 2.0;

        return hours switch
        {
            _ when hours < crop.WiltHours * DueFraction => WaterState.Watered,
            _ when hours < crop.WiltHours => WaterState.Due,
            _ when hours < dangerAt => WaterState.Overdue,
            _ => WaterState.Danger,
        };
    }

    /// <summary>When this bed's plant becomes unrecoverable if nobody waters: tend +
    /// WitherHours (2026-08-18, the Allagan Melons - wilt is a countdown, not an end
    /// state, and the deadline deserves saying out loud). Null wherever the clock makes
    /// no claim: pots (observed, not clocked), ripe beds (cannot die), no tend receipt
    /// (no clock to run).</summary>
    public static DateTimeOffset? DiesAt(ClaimedBed bed, Crop crop)
        => bed.IsPot || bed.Latest is { Stage: >= 4 } || bed.LastTended is not { } tended
            ? null
            : tended.AddHours(crop.WitherHours);
}
