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
        IReadOnlyList<ClaimedBed> beds, DomainTables tables, DateTimeOffset now,
        Func<EstateKey, string>? nameOf = null)
    {
        var name = nameOf ?? (k => k.DisplayLabel());
        var tips = new List<Tip>();
        var intents = RecognizeIntents(beds, tables);

        // Stock: three patches running the same cross are ONE fact about the garden -
        // one line per (estate, pair), patches listed together (08-16 Sam: 13 lines of
        // the tab restating the same census).
        foreach (var pair in intents.GroupBy(i => (i.Estate, i.SpeciesA, i.SpeciesB)))
        {
            var products = string.Join(" or ", pair
                .Select(i => i.ResultSeedId)
                .Distinct()
                .OrderBy(seed => seed)
                .Select(seed => tables.CropBySeedId(seed)?.Name ?? $"seed {seed}"));
            var patches = PatchList(pair.Select(i => i.PatchOrdinal));
            tips.Add(new Tip(TipKind.Stock,
                $"{name(pair.Key.Estate)} {patches}: " +
                $"{tables.SpeciesName(pair.Key.SpeciesA)} x {tables.SpeciesName(pair.Key.SpeciesB)} " +
                $"-> {products}"));
        }

        // Chain: one intent's result seed is a parent in another patch's pair -> pipeline.
        // One line per REAL relationship (feeder patch + feeder seed -> consumer patch);
        // a multi-result patch on either side must not multiply the prose (spec: silence
        // over filler - tips are advisory sentences a human reads).
        var relationships = intents
            .SelectMany(feeder => intents
                .Where(consumer => (consumer.Estate, consumer.PatchOrdinal)
                                   != (feeder.Estate, feeder.PatchOrdinal))
                .Where(consumer => tables.SeedIdBySpeciesIndex(consumer.SpeciesA) == feeder.ResultSeedId
                                   || tables.SeedIdBySpeciesIndex(consumer.SpeciesB) == feeder.ResultSeedId)
                .Select(consumer => (feeder, consumer)))
            .GroupBy(r => (
                FeederEstate: r.feeder.Estate,
                FeederSeed: r.feeder.ResultSeedId,
                ConsumerEstate: r.consumer.Estate));

        // One line per real relationship: every feeder PATCH of the same (estate, seed,
        // consumer) is the same supply edge - the patches list together.
        foreach (var relationship in relationships)
        {
            var products = string.Join(" or ", relationship
                .Select(r => r.consumer.ResultSeedId)
                .Distinct()
                .OrderBy(seed => seed)
                .Select(seed => tables.CropBySeedId(seed)?.Name ?? "?"));
            var feederName = tables.CropBySeedId(relationship.Key.FeederSeed)?.Name ?? "?";
            var feederPatches = PatchList(relationship.Select(r => r.feeder.PatchOrdinal));
            tips.Add(new Tip(TipKind.Bottleneck,
                $"{feederName} seeds feed the {products} patch " +
                $"({name(relationship.Key.ConsumerEstate)}) - feeder is " +
                $"{name(relationship.Key.FeederEstate)} {feederPatches}"));
        }

        // "patch 2" / "patches 1-3" / "patches 1, 3": contiguous ordinals compress to a
        // range, gaps stay honest as a list. Display is 1-based like everywhere else.
        static string PatchList(IEnumerable<int> ordinals)
        {
            var sorted = ordinals.Distinct().OrderBy(o => o).Select(o => o + 1).ToList();
            if (sorted.Count == 1)
                return $"patch {sorted[0]}";
            var contiguous = sorted.Last() - sorted.First() == sorted.Count - 1;
            return contiguous
                ? $"patches {sorted.First()}-{sorted.Last()}"
                : $"patches {string.Join(", ", sorted)}";
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
                $"{name(patch.Key.Estate)} patch {patch.Key.PatchOrdinal + 1} " +
                $"bed {offSlot + 1} breaks the alternation - intentional?"));
        }

        return tips;
    }
}
