using BalambGarden.Engine.Domain;

namespace BalambGarden.Engine.Derivations;

/// <summary>A ripe pot offered to the sweep: its map key, the ledger's species for it,
/// and whether the object is actually walkable-to right now.</summary>
public sealed record PotCycleCandidate(int Key, ushort? SpeciesIndex, bool Reachable);

/// <summary>One pot the sweep will cycle, with the seed the tables joined to its species.</summary>
public sealed record PotCycleJob(int Key, uint SeedId);

/// <summary>A soil item as the bags hold it right now - the pot side has no soil table
/// on purpose, so this is always a live read, never a row.</summary>
public sealed record BagSoil(uint ItemId, string Name, int Count);

/// <summary>What one press will do and everything it declined to do, each with a reason.
/// A non-null Refusal means the press does nothing at all - and then Jobs is empty,
/// never a half-plan.</summary>
public sealed record PotCyclePlan(
    uint SoilItemId, IReadOnlyList<PotCycleJob> Jobs, IReadOnlyList<string> Skips, string? Refusal);

/// <summary>
/// Turns "cycle everything ripe" into per-pot jobs the chain can run, fail-closed.
///
/// <para>Seed comes from the species table - flowers carry their seed join exactly like
/// crops (08-16: color is pot state, the species word is the colorless base). Soil comes
/// from the bag under the 08-16 ruling "the bag is the plan": exactly one soil in bags is
/// the soil; zero or several refuses the derivation and sends the player to the picker.</para>
///
/// <para>Shortages skip pots individually with a named reason (a sweep should do what it
/// can, like a player would); short bag ROOM refuses the whole press instead, matching the
/// patch cycle - yields with nowhere to land are not a per-pot problem.</para>
/// </summary>
public static class PotCyclePlanner
{
    public static PotCyclePlan Plan(
        IReadOnlyList<PotCycleCandidate> ripePots, IReadOnlyList<BagSoil> soils,
        IInventorySource bags, int freeBagSlots, DomainTables tables)
    {
        if (soils.Count == 0)
            return Refuse("no soil in bags - nothing to replant with");
        if (soils.Count > 1)
            return Refuse("more than one soil in bags ("
                + string.Join(", ", soils.Select(s => s.Name))
                + ") - use Cycle... to pick one");

        var soil = soils[0];
        var soilLeft = soil.Count;
        var seedLeft = new Dictionary<uint, int>();
        var jobs = new List<PotCycleJob>();
        var skips = new List<string>();

        foreach (var pot in ripePots.OrderBy(p => p.Key))
        {
            if (!pot.Reachable)
            {
                skips.Add($"pot {pot.Key}: not in reach");
                continue;
            }

            if (pot.SpeciesIndex is not { } species
                || tables.SeedIdBySpeciesIndex(species) is not { } seedId)
            {
                skips.Add($"pot {pot.Key}: no seed known for its plant - use Cycle...");
                continue;
            }

            if (!seedLeft.ContainsKey(seedId))
                seedLeft[seedId] = bags.CountOf(seedId);
            var seedName = tables.CropBySeedId(seedId)?.SeedName
                ?? $"{tables.SpeciesName(species)} seeds";
            if (seedLeft[seedId] == 0)
            {
                skips.Add($"pot {pot.Key}: out of {seedName}");
                continue;
            }

            if (soilLeft == 0)
            {
                skips.Add($"pot {pot.Key}: out of {soil.Name}");
                continue;
            }

            seedLeft[seedId]--;
            soilLeft--;
            jobs.Add(new PotCycleJob(pot.Key, seedId));
        }

        if (freeBagSlots < jobs.Count)
            return Refuse($"need {jobs.Count} free bag slots for yields, have {freeBagSlots}");

        return new PotCyclePlan(soil.ItemId, jobs, skips, Refusal: null);

        static PotCyclePlan Refuse(string why) => new(0, [], [], why);
    }
}
