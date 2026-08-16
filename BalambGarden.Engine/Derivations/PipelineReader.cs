using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

public enum TipKind { Stock, Bottleneck, Anomaly }

/// <summary>Attention = this line should be READ (a shortage, an anomaly) - it drives the
/// tab's "(!)" flag. Stock furniture and covered chains never claim it.</summary>
public sealed record Tip(TipKind Kind, string Text, bool Attention = false);

/// <summary>What the plugin can see in bags RIGHT NOW. The Engine asks, never caches -
/// a supply claim is only ever as fresh as the read behind it (ruling 2026-08-16: bags
/// only, live, honest about scope).</summary>
public interface IInventorySource
{
    int CountOf(uint itemId);
}

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
        Func<EstateKey, string>? nameOf = null, IInventorySource? inventory = null,
        Func<DateTimeOffset, DateTimeOffset, string>? formatWindow = null)
    {
        var name = nameOf ?? (k => k.DisplayLabel());
        var fmt = formatWindow ?? WindowFormat.Coarse;
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
        // consumer) is the same supply edge - the patches list together. Sentence shape
        // (08-16 Sam: the dash-chain read as clause soup): lead with who needs what,
        // clauses on "·" like the rest of the UI, the time range LAST so its internal
        // dash never collides with a clause break. The consumer is named by PLACE - the
        // stock line above already says what it makes.
        foreach (var relationship in relationships)
        {
            var feederName = tables.CropBySeedId(relationship.Key.FeederSeed)?.Name ?? "?";
            var feederPatches = PatchList(relationship.Select(r => r.feeder.PatchOrdinal));
            var feederLabel = $"{name(relationship.Key.FeederEstate)} {feederPatches}";
            var consumerPatches = relationship
                .Select(r => (r.consumer.Estate, r.consumer.PatchOrdinal)).Distinct().ToList();
            var consumerLabel = $"{name(relationship.Key.ConsumerEstate)} "
                                + PatchList(consumerPatches.Select(p => p.PatchOrdinal));

            var fallback = $"{consumerLabel} replants with {feederName} seeds · feeder is {feederLabel}";

            // The state join (rulings 2026-08-16): demand from the consumer's actual
            // layout, supply from the live bag read, feeder ripeness as a DATE only -
            // crossbreed yield is chance-based, so a future harvest is never a quantity.
            var sample = relationship.First().consumer;
            var species = tables.SeedIdBySpeciesIndex(sample.SpeciesA) == relationship.Key.FeederSeed
                ? sample.SpeciesA : sample.SpeciesB;
            var consumerBeds = beds.Where(b => !b.IsPot
                && consumerPatches.Contains((b.Estate, b.PatchOrdinal))).ToList();
            var demand = consumerBeds.Count(b => b.Latest?.SpeciesIndex == species);

            if (inventory is null || demand == 0)
            {
                tips.Add(new Tip(TipKind.Bottleneck, fallback));
                continue;
            }

            var supply = inventory.CountOf(relationship.Key.FeederSeed);
            var text = $"{consumerLabel} replant needs {demand} {feederName} seeds · {supply} in bags";
            var attention = false;

            if (supply >= demand)
            {
                text += " · covered";
            }
            else
            {
                // Short: the feeder's ripe window against the consumer's replant moment
                // says whether the chain is late or merely lean.
                attention = true;
                var feederKeys = relationship
                    .Select(r => (r.feeder.Estate, r.feeder.PatchOrdinal)).Distinct().ToList();
                var feederWindow = CombinedWindow(beds.Where(b => !b.IsPot
                    && feederKeys.Contains((b.Estate, b.PatchOrdinal))), tables);
                var consumerWindow = CombinedWindow(consumerBeds, tables);
                text += feederWindow is { } f && consumerWindow is { } c
                    ? $" · feeder {feederLabel} ripens "
                      + (f.Earliest <= c.Earliest ? "before" : "after")
                      + $" the replant: {fmt(f.Earliest, f.Latest)}"
                    : $" · feeder is {feederLabel}";
            }

            tips.Add(new Tip(TipKind.Bottleneck, text, attention));
        }

        // A patch's window as one span: earliest any bed could ripen to latest any bed
        // might - the whole patch is the unit a Cycle press works, so its replant clock
        // is the union of its beds'.
        static EtaWindow? CombinedWindow(IEnumerable<ClaimedBed> patchBeds, DomainTables tables)
        {
            EtaWindow? combined = null;
            foreach (var bed in patchBeds)
            {
                if (bed.Latest is not { } latest)
                    continue;
                if (tables.GrowHours(latest.SpeciesIndex) is not { } growHours)
                    continue;
                if (StageModel.RipeWindow(bed.Ring, growHours) is not { } window)
                    continue;
                combined = combined is { } c
                    ? new EtaWindow(
                        window.Earliest < c.Earliest ? window.Earliest : c.Earliest,
                        window.Latest > c.Latest ? window.Latest : c.Latest,
                        c.Provenance)
                    : window;
            }
            return combined;
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
                $"bed {offSlot + 1} breaks the alternation - intentional?",
                Attention: true));
        }

        return tips;
    }
}
