using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>Rethink slice 23, at the garden board's seam: the game publishes death - a dead plant's
/// stage reads 5 on the map (2026-11-10, four dead onion pots; the inspect line "This crop is
/// beyond hope."). A container whose latest read is stage 5 is dead: never ripe, never growing,
/// no water claim, and one more container to clear and sow. The game wins: a later read replaces it.</summary>
public class DeadPlantTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-11T00:50:00Z");

    // Data/SpeciesIndex.json. Thavnairian Onion: wilt 24h, wither 48h. Krakka: grow 72h, wilt 24h.
    private const ushort Onion = 50;
    private const ushort Krakka = 49;

    private static readonly EstateKey Yard = new(340, 11, 32);

    private static ClaimedBed Pot(int mapKey)
        => new() { Estate = Yard, MapKey = mapKey, PatchOrdinal = mapKey, BedSlot = 0, IsPot = true };

    private static ClaimedBed Bed(int patch, int slot)
        => new() { Estate = Yard, MapKey = 100 + patch, PatchOrdinal = patch, BedSlot = slot };

    private static VisitCard OneCard(IReadOnlyList<ClaimedBed> beds, Standing? standing = null)
        => Assert.Single(GardenBoard.Cards([new EstateRecord { Key = Yard, Nickname = "Onion Yard" }], beds,
            new AccessRoster([new RosterEstate(Yard, "test")]), T, Now, standing));

    private static ContainerCell PotCell(VisitCard card, int key)
        => card.Patches.Single(p => p.IsPots).Cells.Single(c => c.Position == key);

    /// <summary>A pot sown Sat 18:14, never watered, read dead at 18:43 two days later.</summary>
    private static ClaimedBed DeadPot(int key)
    {
        var pot = Seed.Plant(Pot(key), DateTimeOffset.Parse("2026-11-08T00:14:00Z"), Onion, stage: 0, by: Seed.Household);
        return Seed.Sight(pot, DateTimeOffset.Parse("2026-11-11T00:43:41Z"), Onion, stage: 5);
    }

    [Fact] // the 2026-11-10 capture: four dead onion pots turned blue and "ripe now"
    public void APotReadAtStageFiveIsDeadNeverRipe()
    {
        var card = OneCard([DeadPot(126), DeadPot(127), DeadPot(128), DeadPot(129)]);

        var cell = PotCell(card, 126);
        Assert.Equal(ContainerLook.Dead, cell.Look);
        Assert.Equal(DateTimeOffset.Parse("2026-11-11T00:43:41Z"), cell.SeenDeadAt);
        Assert.Null(cell.Ripe);                        // no ripe window
        Assert.Null(cell.WaterBy);                     // no water claim
        Assert.Null(cell.DiesAt);
        Assert.False(cell.TendUnheard);                // no "no watering recorded" for a dead plant
        Assert.False(cell.WiltShown);

        Assert.Equal(0, card.RipeNow);
        Assert.Equal(0, card.Harvest);
        Assert.Empty(card.Ripe);
        Assert.DoesNotContain(card.Lane.Marks, m => m.Kind == LaneMarkKind.RipeNow);
        Assert.Empty(card.Lane.Marks);
        Assert.Null(card.WaterBy);
        Assert.Null(card.Lane.WaterFrom);
        Assert.Equal(ContainerCount.None, card.Water);
        Assert.Equal(ContainerCount.None, card.TendUnheard);
        Assert.Equal(ContainerLook.Growing, card.Thirst);

        Assert.Equal(new ContainerCount(Beds: 0, Pots: 4, Patch: null), card.Dead);
        Assert.Equal(4, card.Replant);                 // dead counts with the empty: clear and sow
    }

    [Fact] // slice 20's pot clock: a dead pot never claims water, however old its sowing
    public void ADeadPotMakesNoWaterClaimBesideALivingOne()
    {
        var living = Seed.Plant(Pot(130), Now.AddHours(-10), Onion, stage: 1);
        var card = OneCard([DeadPot(126), living]);

        Assert.Equal(Now.AddHours(14), card.WaterBy);  // the living pot's deadline alone
        Assert.Equal(new ContainerCount(Beds: 0, Pots: 1, Patch: null), card.FirstToWilt);
        Assert.Equal(new ContainerCount(Beds: 0, Pots: 1, Patch: null), card.Water);
        Assert.Equal(ContainerLook.Growing, card.Thirst);
    }

    [Fact] // a dead plant still holds a planting: the estate keeps its card, the visit clears it
    public void AnEstateHoldingOnlyDeadPlantsKeepsItsCard()
    {
        var card = OneCard([DeadPot(126)]);

        Assert.Equal(1, card.Replant);
        Assert.Equal(new ContainerCount(Beds: 0, Pots: 1, Patch: null), card.Dead);
    }

    [Fact] // in the room the live read decides wilt; a dead pot is not wilting and not growing
    public void InItsRoomADeadPotStaysDead()
    {
        var live = new Dictionary<int, PotReading>
        {
            [126] = new(Onion, Stage: 5, Color: 0, Extra: 0, Occupied: true, Recognized: true, Wilt: 2),
        };
        var card = OneCard([DeadPot(126)], new Standing(Yard, Inside: true, live));

        Assert.Equal(ContainerLook.Dead, PotCell(card, 126).Look);
        Assert.Equal(0, card.PotsWilting);
    }

    [Fact] // the game wins: a later read showing a living plant replaces the dead one
    public void ALaterReadOfAGrowingPlantReplacesIt()
    {
        var pot = DeadPot(126);
        Seed.Plant(pot, Now.AddMinutes(-2), Onion, stage: 0);   // cleared and sown again
        Seed.Sight(pot, Now.AddMinutes(-1), Onion, stage: 0);

        var cell = PotCell(OneCard([pot]), 126);

        Assert.Equal(ContainerLook.Growing, cell.Look);
        Assert.Null(cell.SeenDeadAt);
        Assert.Equal(Now.AddMinutes(-2).AddHours(24), cell.WaterBy);
    }

    [Fact] // the game wins: a later empty read replaces it (the 18:52 re-entry after the clearing)
    public void ALaterEmptyReadReplacesIt()
    {
        var dead = DeadPot(126);
        dead.Rebase(RebaseReason.ReadsEmpty, cut: Now.AddMinutes(-1));
        var living = Seed.Plant(Pot(127), Now.AddHours(-1), Onion, stage: 1);

        var card = OneCard([dead, living]);

        Assert.Equal(ContainerLook.Empty, PotCell(card, 126).Look);
        Assert.Equal(ContainerCount.None, card.Dead);
        Assert.Equal(1, card.Replant);
    }

    [Fact] // stage 4 is still ripe
    public void StageFourIsStillRipe()
    {
        var ripe = Seed.Sight(Seed.Plant(Pot(126), Now.AddDays(-2), Onion, stage: 0), Now.AddMinutes(-5), Onion, stage: 4);

        var card = OneCard([ripe]);

        Assert.Equal(ContainerLook.Ripe, PotCell(card, 126).Look);
        Assert.Equal(1, card.RipeNow);
        Assert.Equal(ContainerCount.None, card.Dead);
        Assert.Equal(0, card.Replant);
    }

    [Fact] // beds share the rule (a dead bed's stage is unreceipted; the first one seen confirms or reopens it)
    public void ABedReadAtStageFiveIsDeadToo()
    {
        var bed = Seed.Sight(Seed.Plant(Bed(0, 2), Now.AddDays(-4), Krakka, stage: 1), Now.AddMinutes(-3), Krakka, stage: 5);

        var card = OneCard([bed]);

        Assert.Equal(ContainerLook.Dead, card.Patches.Single().Cells[2].Look);
        Assert.Equal(new ContainerCount(Beds: 1, Pots: 0, Patch: 0), card.Dead);
        Assert.Equal(0, card.Harvest);
        Assert.Null(card.WaterBy);
    }
}
