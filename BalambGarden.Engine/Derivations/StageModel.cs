using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>A ripe-time estimate off the planting record. <see cref="Contradiction"/> flags a
/// record whose stage brackets cannot all be true of one plant (a missed replant, or skew
/// between installs): the window is then an honest estimate off the newest stage, never null
/// and never a lie dressed as a bracket. The pump logs a contradiction once per row per visit.</summary>
public sealed record RipeEstimate(EtaWindow Window, bool Contradiction);

/// <summary>Stage-fraction timing model. Stage bands are equal thirds of growHours -
/// a tunable model constant, calibration pending (spec: brackets).</summary>
public static class StageModel
{
    public static (double Lo, double Hi) StageFraction(byte stage) => stage switch
    {
        1 => (0.0, 1.0 / 3.0),
        2 => (1.0 / 3.0, 2.0 / 3.0),
        3 => (2.0 / 3.0, 1.0),
        4 => (1.0, 1.0),
        _ => (0.0, 1.0),
    };

    /// <summary>When this bed ripens (or ripened), from what the record holds about the current
    /// planting. A witnessed sowing (<see cref="ClaimedBed.PlantedAt"/>) is the ONE legitimate
    /// anchor: exact ripe time, zero-width window. Otherwise each stage bracket constrains the
    /// sowing to [LastSeen(s) - Hi(s)G, FirstSeen(s) - Lo(s)G]; two or more stages seen =
    /// bracketed, one = estimated. The fence (<see cref="ClaimedBed.PlantingResetAt"/>) is a
    /// floor on the sowing: every reset cuts at or before the moment the old contents were last
    /// true, so this planting went in after it. A fence that tightens the earliest end closes a
    /// real two-sided bracket (emptied at X, seen growing at Y); a fence that would invert the
    /// stage bracket is ignored - a new input never manufactures a contradiction (field receipt
    /// 2026-10-18: a hand replant read "Tue-Wed evening" when the sowing was pinned to 8 minutes).
    /// Null only when the record holds no stage evidence at all.</summary>
    public static RipeEstimate? RipeWindow(ClaimedBed bed, int growHours)
    {
        if (bed.PlantedAt is { } planted)
        {
            var ripe = planted.AddHours(growHours);
            return new RipeEstimate(new EtaWindow(ripe, ripe, Provenance.Anchored), false);
        }

        if (bed.StageSeen.Count == 0)
            return null;

        var provenance = bed.StageSeen.Count >= 2 ? Provenance.Bracketed : Provenance.Estimated;

        if (bed.StageSeen.TryGetValue(GardenStage.Ripe, out var ripeSeen))
        {
            // Became ripe between the last stage-3 sighting and the first stage-4 one. With no
            // stage 3 on record - or an inverted pair, which is skew or a missed replant - the
            // honest word is an estimate the width of one stage band, unless the fence closes it.
            var hi = ripeSeen.FirstSeen;
            var ripeFloor = bed.PlantingResetAt?.AddHours(growHours);
            if (bed.StageSeen.TryGetValue(3, out var third) && third.LastSeen < hi)
                return new RipeEstimate(new EtaWindow(Floor(third.LastSeen, ripeFloor, hi), hi, provenance), false);
            var lo = hi.AddHours(-growHours / 3.0);
            var floored = Floor(lo, ripeFloor, hi);
            return new RipeEstimate(
                new EtaWindow(floored, hi, floored > lo ? Provenance.Bracketed : Provenance.Estimated), false);
        }

        var earliestPlant = DateTimeOffset.MinValue;
        var latestPlant = DateTimeOffset.MaxValue;
        foreach (var (stage, seen) in bed.StageSeen)
        {
            var (lo, hi) = StageFraction(stage);
            var min = seen.LastSeen.AddHours(-hi * growHours);
            var max = seen.FirstSeen.AddHours(-lo * growHours);
            if (min > earliestPlant) earliestPlant = min;
            if (max < latestPlant) latestPlant = max;
        }

        if (earliestPlant <= latestPlant)
        {
            var floored = Floor(earliestPlant, bed.PlantingResetAt, latestPlant);
            if (floored > earliestPlant)
                (earliestPlant, provenance) = (floored, Provenance.Bracketed);
            return new RipeEstimate(
                new EtaWindow(earliestPlant.AddHours(growHours), latestPlant.AddHours(growHours), provenance),
                false);
        }

        // Contradictory brackets: estimate off the newest stage sighting and say so.
        var newest = bed.StageSeen.MaxBy(kv => kv.Value.LastSeen);
        var (nlo, nhi) = StageFraction(newest.Key);
        return new RipeEstimate(
            new EtaWindow(
                newest.Value.LastSeen.AddHours((1 - nhi) * growHours),
                newest.Value.LastSeen.AddHours((1 - nlo) * growHours),
                Provenance.Estimated),
            true);
    }

    /// <summary>Raises <paramref name="lo"/> to the fence-derived floor when the floor sits inside
    /// the window; a floor past <paramref name="hi"/> would invert it and is ignored.</summary>
    private static DateTimeOffset Floor(DateTimeOffset lo, DateTimeOffset? floor, DateTimeOffset hi)
        => floor is { } f && f > lo && f <= hi ? f : lo;
}
