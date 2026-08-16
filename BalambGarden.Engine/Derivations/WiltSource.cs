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
        // No pot has ever been SEEN to wilt, but the evidence base is flower seeds only
        // (Sam's table + our unwatered sunflower, both flowers) - whether that is a pot
        // mechanic or a flower oddity is what the dry-vs-watered twins labs are running
        // to decide (08-15). Until they report, the clock does not run on pots: a Danger
        // march would assert a mechanic nothing has receipted.
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
