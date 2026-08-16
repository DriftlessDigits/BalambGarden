using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

public class PotCyclePlannerTests
{
    private static readonly DomainTables T = DomainTables.Load();

    // The real 08-16 bench: species 93 "Cosmos" carries seedId 30365 in SpeciesIndex.json,
    // exactly like a crop - color is pot state (b2 nibble), never part of the species.
    private const ushort Cosmos = 93;
    private const uint CosmosSeeds = 30365;
    private const uint PottingSoil = 7717;

    private sealed class FakeBags : Dictionary<uint, int>, IInventorySource
    {
        public int CountOf(uint itemId) => TryGetValue(itemId, out var n) ? n : 0;
    }

    private static PotCycleCandidate Pot(int key, ushort? species = Cosmos, bool reachable = true)
        => new(key, species, reachable);

    private static List<BagSoil> OneSoil(int count = 10)
        => [new BagSoil(PottingSoil, "Potting Soil", count)];

    [Fact] // the screenshot case: four ripe Cosmos pots, one soil, seeds to cover - one press
    public void FourPotsOneSoilPlansAll()
    {
        var plan = PotCyclePlanner.Plan(
            [Pot(393), Pot(208), Pot(227), Pot(392)],
            OneSoil(), new FakeBags { [CosmosSeeds] = 4 }, freeBagSlots: 10, T);

        Assert.Null(plan.Refusal);
        Assert.Empty(plan.Skips);
        Assert.Equal(PottingSoil, plan.SoilItemId);
        Assert.Equal([208, 227, 392, 393], plan.Jobs.Select(j => j.Key));
        Assert.All(plan.Jobs, j => Assert.Equal(CosmosSeeds, j.SeedId));
    }

    [Fact] // no soil in bags: nothing to plant with, the whole press refuses
    public void NoSoilRefuses()
    {
        var plan = PotCyclePlanner.Plan(
            [Pot(208)], [], new FakeBags { [CosmosSeeds] = 4 }, freeBagSlots: 10, T);

        Assert.NotNull(plan.Refusal);
        Assert.Contains("no soil", plan.Refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(plan.Jobs);
    }

    [Fact] // two different soils: the bag is no longer a plan, the picker is (ruling 08-16)
    public void TwoSoilsRefuses()
    {
        var plan = PotCyclePlanner.Plan(
            [Pot(208)],
            [new BagSoil(PottingSoil, "Potting Soil", 5), new BagSoil(7715, "Shroud Topsoil", 3)],
            new FakeBags { [CosmosSeeds] = 4 }, freeBagSlots: 10, T);

        Assert.NotNull(plan.Refusal);
        Assert.Contains("Potting Soil", plan.Refusal);
        Assert.Contains("Shroud Topsoil", plan.Refusal);
        Assert.Empty(plan.Jobs);
    }

    [Fact] // two seeds for four pots: the covered pots run, the rest are named skips
    public void SeedShortageSkipsTheRest()
    {
        var plan = PotCyclePlanner.Plan(
            [Pot(208), Pot(227), Pot(392), Pot(393)],
            OneSoil(), new FakeBags { [CosmosSeeds] = 2 }, freeBagSlots: 10, T);

        Assert.Null(plan.Refusal);
        Assert.Equal([208, 227], plan.Jobs.Select(j => j.Key));
        Assert.Equal(2, plan.Skips.Count);
        Assert.Contains(plan.Skips, s => s.Contains("pot 392") && s.Contains("out of"));
        Assert.Contains(plan.Skips, s => s.Contains("pot 393"));
    }

    [Fact] // soil runs out before the pots do: same skip shape as a seed shortage
    public void SoilShortageSkipsTheRest()
    {
        var plan = PotCyclePlanner.Plan(
            [Pot(208), Pot(227), Pot(392)],
            OneSoil(count: 1), new FakeBags { [CosmosSeeds] = 4 }, freeBagSlots: 10, T);

        Assert.Null(plan.Refusal);
        Assert.Equal([208], plan.Jobs.Select(j => j.Key));
        Assert.Equal(2, plan.Skips.Count);
        Assert.Contains(plan.Skips, s => s.Contains("pot 227") && s.Contains("Potting Soil"));
    }

    [Fact] // a species the tables cannot join to a seed is skipped, never guessed at
    public void UnknownSpeciesSkips()
    {
        var plan = PotCyclePlanner.Plan(
            [Pot(208), Pot(227, species: 999), Pot(392, species: null)],
            OneSoil(), new FakeBags { [CosmosSeeds] = 4 }, freeBagSlots: 10, T);

        Assert.Null(plan.Refusal);
        Assert.Equal([208], plan.Jobs.Select(j => j.Key));
        Assert.Equal(2, plan.Skips.Count);
        Assert.Contains(plan.Skips, s => s.Contains("pot 227"));
        Assert.Contains(plan.Skips, s => s.Contains("pot 392"));
    }

    [Fact] // a pot the sweep cannot walk to is reported, not silently dropped
    public void UnreachablePotSkips()
    {
        var plan = PotCyclePlanner.Plan(
            [Pot(208), Pot(227, reachable: false)],
            OneSoil(), new FakeBags { [CosmosSeeds] = 4 }, freeBagSlots: 10, T);

        Assert.Null(plan.Refusal);
        Assert.Equal([208], plan.Jobs.Select(j => j.Key));
        Assert.Contains(plan.Skips, s => s.Contains("pot 227") && s.Contains("reach"));
    }

    [Fact] // every harvest needs somewhere to land - short on bag room refuses whole (as patches do)
    public void FreeSlotShortageRefuses()
    {
        var plan = PotCyclePlanner.Plan(
            [Pot(208), Pot(227), Pot(392), Pot(393)],
            OneSoil(), new FakeBags { [CosmosSeeds] = 4 }, freeBagSlots: 2, T);

        Assert.NotNull(plan.Refusal);
        Assert.Contains("bag slots", plan.Refusal);
        Assert.Empty(plan.Jobs);
    }
}
