using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

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

    public static EtaWindow? RipeWindow(IReadOnlyList<Observation> ring, int growHours)
    {
        if (ring.Count == 0)
            return null;

        var anchor = ring.FirstOrDefault(o => o.Source == ObservationSource.PlantReceipt);
        if (anchor is not null)
        {
            var ripe = anchor.At.AddHours(growHours);
            return new EtaWindow(ripe, ripe, Provenance.Anchored);
        }

        var staged = ring.Where(o => o.Stage is >= 1 and <= 4).OrderBy(o => o.At).ToList();
        if (staged.Count == 0)
            return null;

        var provenance = staged.Count >= 2 ? Provenance.Bracketed : Provenance.Estimated;

        var ripeSeen = staged.FirstOrDefault(o => o.Stage == 4);
        if (ripeSeen is not null)
        {
            // Ripe was observed: it is ripe now; earliest possible ripe bounded by prior sightings.
            return new EtaWindow(ripeSeen.At, ripeSeen.At, provenance);
        }

        // Each observation (t, stage s) constrains plant time to [t - Hi(s)*G, t - Lo(s)*G].
        var earliestPlant = DateTimeOffset.MinValue;
        var latestPlant = DateTimeOffset.MaxValue;
        foreach (var o in staged)
        {
            var (lo, hi) = StageFraction(o.Stage);
            var min = o.At.AddHours(-hi * growHours);
            var max = o.At.AddHours(-lo * growHours);
            if (min > earliestPlant) earliestPlant = min;
            if (max < latestPlant) latestPlant = max;
        }

        if (earliestPlant > latestPlant)
            return null;   // contradictory sightings: report nothing rather than a lie

        return new EtaWindow(
            earliestPlant.AddHours(growHours),
            latestPlant.AddHours(growHours),
            provenance);
    }
}
