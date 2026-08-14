using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

public enum WaterState { Unknown, Watered, Due, Overdue, Danger }

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
