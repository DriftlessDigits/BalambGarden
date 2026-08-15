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
        // Flowerpots cannot wilt (08-15 finding: Sam's third-party gardening table lists
        // every flowerpot seed at 1-day grow with NO wilt time, corroborated by our own
        // sunflower receipt - unwatered seed to ripe in about a day). Indoor watering is
        // the pigment mechanic, cosmetic only. Marching a pot to Danger would be a false
        // alarm about a plant that cannot die, so the clock never runs on one.
        if (bed.IsPot)
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
}
