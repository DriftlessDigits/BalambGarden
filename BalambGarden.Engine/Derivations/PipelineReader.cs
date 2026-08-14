using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

public enum TipKind { Stock, Bottleneck, Anomaly }

public sealed record Tip(TipKind Kind, string Text);

public sealed record CrossIntent(
    EstateKey Estate, int PatchOrdinal, ushort SpeciesA, ushort SpeciesB, uint ResultSeedId);

/// <summary>Reads the pattern the gardener already chose and reports its state.
/// Never a planner, never prescriptive (spec: tips). Anomalies ask, corrections never.</summary>
public static class PipelineReader
{
    /// <summary>One intent per (patch, possible result). Parent pairs are genuinely
    /// multi-result, so a single patch can carry more than one intent.</summary>
    public static IReadOnlyList<CrossIntent> RecognizeIntents(
        IReadOnlyList<ClaimedBed> beds, DomainTables tables)
    {
        var intents = new List<CrossIntent>();
        foreach (var patch in beds.Where(b => !b.IsPot).GroupBy(b => (b.Estate, b.PatchOrdinal)))
        {
            var latest = patch
                .Where(b => b.Latest is not null)
                .ToDictionary(b => b.BedSlot, b => b.Latest!.SpeciesIndex);
            if (latest.Count < 4)
                continue;

            var evens = latest.Where(kv => kv.Key % 2 == 0).Select(kv => kv.Value).Distinct().ToList();
            var odds = latest.Where(kv => kv.Key % 2 == 1).Select(kv => kv.Value).Distinct().ToList();
            if (evens.Count != 1 || odds.Count != 1 || evens[0] == odds[0])
                continue;

            var seedA = tables.SeedIdBySpeciesIndex(evens[0]);
            var seedB = tables.SeedIdBySpeciesIndex(odds[0]);
            if (seedA is null || seedB is null)
                continue;

            foreach (var result in tables.CrossResults(seedA.Value, seedB.Value))
            {
                intents.Add(new CrossIntent(patch.Key.Estate, patch.Key.PatchOrdinal,
                    evens[0], odds[0], result));
            }
        }
        return intents;
    }

    public static IReadOnlyList<Tip> Tips(
        IReadOnlyList<ClaimedBed> beds, DomainTables tables, DateTimeOffset now)
    {
        var tips = new List<Tip>();
        var intents = RecognizeIntents(beds, tables);

        // Stock: one line per PATCH, naming every product that pair can yield.
        foreach (var patch in intents.GroupBy(i => (i.Estate, i.PatchOrdinal, i.SpeciesA, i.SpeciesB)))
        {
            var products = string.Join(" or ", patch
                .OrderBy(i => i.ResultSeedId)
                .Select(i => tables.CropBySeedId(i.ResultSeedId)?.Name ?? $"seed {i.ResultSeedId}"));
            tips.Add(new Tip(TipKind.Stock,
                $"{patch.Key.Estate.DisplayWardPlot()} patch {patch.Key.PatchOrdinal + 1}: " +
                $"{tables.SpeciesName(patch.Key.SpeciesA)} x {tables.SpeciesName(patch.Key.SpeciesB)} " +
                $"-> {products}"));
        }

        // Chain: one intent's result seed is a parent in another intent -> pipeline.
        foreach (var feeder in intents)
        {
            foreach (var consumer in intents)
            {
                if (consumer == feeder) continue;
                var consumerParents = new[]
                {
                    tables.SeedIdBySpeciesIndex(consumer.SpeciesA),
                    tables.SeedIdBySpeciesIndex(consumer.SpeciesB),
                };
                if (!consumerParents.Contains(feeder.ResultSeedId))
                    continue;
                var product = tables.CropBySeedId(consumer.ResultSeedId)?.Name ?? "?";
                var feederName = tables.CropBySeedId(feeder.ResultSeedId)?.Name ?? "?";
                tips.Add(new Tip(TipKind.Bottleneck,
                    $"{feederName} seeds feed the {product} patch " +
                    $"({consumer.Estate.DisplayWardPlot()}) - feeder is " +
                    $"{feeder.Estate.DisplayWardPlot()} patch {feeder.PatchOrdinal + 1}"));
            }
        }

        // Anomaly: a patch that is one bed away from a clean A/B alternation.
        foreach (var patch in beds.Where(b => !b.IsPot).GroupBy(b => (b.Estate, b.PatchOrdinal)))
        {
            var latest = patch.Where(b => b.Latest is not null)
                .ToDictionary(b => b.BedSlot, b => b.Latest!.SpeciesIndex);
            if (latest.Count < 5)
                continue;
            var evenGroups = latest.Where(kv => kv.Key % 2 == 0)
                .GroupBy(kv => kv.Value).OrderByDescending(g => g.Count()).ToList();
            var oddGroups = latest.Where(kv => kv.Key % 2 == 1)
                .GroupBy(kv => kv.Value).OrderByDescending(g => g.Count()).ToList();
            var misfits = evenGroups.Skip(1).Sum(g => g.Count()) + oddGroups.Skip(1).Sum(g => g.Count());
            if (misfits != 1)
                continue;
            var offSlot = latest.First(kv =>
                (kv.Key % 2 == 0 && kv.Value != evenGroups[0].Key) ||
                (kv.Key % 2 == 1 && kv.Value != oddGroups[0].Key)).Key;
            tips.Add(new Tip(TipKind.Anomaly,
                $"{patch.Key.Estate.DisplayWardPlot()} patch {patch.Key.PatchOrdinal + 1} " +
                $"bed {offSlot + 1} breaks the alternation - intentional?"));
        }

        return tips;
    }
}
