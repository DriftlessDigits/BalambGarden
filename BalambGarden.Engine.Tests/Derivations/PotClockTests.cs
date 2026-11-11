using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>Rethink slice 20, at the garden board's seam (spec ruling 23): a pot runs the same water
/// clock a bed does, from its last recorded watering (own or relayed), else its sowing, plus its
/// crop's wilt and wither hours, and the pots join the card's deadline, sort, water chip and water
/// bar. The game still wins where it speaks: the live read in the room, a kept wilt sighting away.</summary>
public class PotClockTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T16:30:00Z");

    // Data/SpeciesIndex.json. Krakka: wilt 24h, wither 48h. Fig: wilt 48h. Thavnairian Onion: wilt
    // 24h, wither 48h. Sunflowers: a flowerpot flower, no crop row (no wilt).
    private const ushort Krakka = 49;
    private const ushort Fig = 65;
    private const ushort Onion = 50;
    private const ushort Sunflowers = 103;

    private static readonly EstateKey Room = new(342, 1, 1);
    private static readonly EstateKey Yard = new(340, 11, 32);

    private static EstateRecord Estate(EstateKey key, string name) => new() { Key = key, Nickname = name };

    private static ClaimedBed Pot(EstateKey estate, int mapKey)
        => new() { Estate = estate, MapKey = mapKey, PatchOrdinal = mapKey, BedSlot = 0, IsPot = true };

    private static ClaimedBed Bed(EstateKey estate, int patch, int slot)
        => new() { Estate = estate, MapKey = 100 + patch, PatchOrdinal = patch, BedSlot = slot };

    private static IReadOnlyList<VisitCard> Board(IReadOnlyList<ClaimedBed> beds, Standing? standing = null,
        params EstateRecord[] estates)
        => GardenBoard.Cards(estates, beds,
            new AccessRoster(estates.Select(e => new RosterEstate(e.Key, "test")).ToList()), T, Now, standing);

    private static VisitCard OneCard(IReadOnlyList<ClaimedBed> beds, Standing? standing = null)
        => Assert.Single(Board(beds, standing, Estate(Room, "Room")));

    private static ContainerCell PotCell(VisitCard card, int key)
        => card.Patches.Single(p => p.IsPots).Cells.Single(c => c.Position == key);

    private static PotReading Live(ushort species, byte stage, byte wilt)
        => new(species, stage, Color: 0, Extra: 0, Occupied: true, Recognized: true, Wilt: wilt);

    [Fact] // 2026-11-08: a household estate's pots sown and watered Sat 18:14, the beds done from outside Sun 19:44
    public void TheSundayNightScenePutsTheUnwateredPotsAtTheTop()
    {
        var pots = new EstateKey(339, 12, 33);
        var home = new EstateKey(339, 4, 52);
        var now = DateTimeOffset.Parse("2026-11-08T19:44:00-06:00");
        var sowing = DateTimeOffset.Parse("2026-11-07T18:14:00-06:00");
        var beds = new List<ClaimedBed>
        {
            Seed.Plant(Pot(pots, 126), sowing, Onion, stage: 1, by: Seed.Household),
            Seed.Plant(Pot(pots, 127), sowing, Onion, stage: 1, by: Seed.Household),
            Seed.Plant(Pot(pots, 128), sowing, Onion, stage: 1, by: Seed.Household),
            Seed.Tend(Seed.Plant(Pot(pots, 129), sowing.AddMinutes(-3), Onion, stage: 1, by: Seed.Household), sowing, Onion, by: Seed.Household),
            Seed.Tend(Seed.Plant(Bed(pots, 0, 0), now.AddDays(-2), Fig, stage: 1), now, Fig),         // the beds, just done: +48h
            Seed.Tend(Seed.Plant(Bed(home, 0, 0), now.AddDays(-2), Krakka, stage: 1), now.AddHours(-14), Krakka), // due in 10h
        };

        var cards = GardenBoard.Cards([Estate(home, "Home"), Estate(pots, "Onion Yard")], beds,
            new AccessRoster([new RosterEstate(home, "test"), new RosterEstate(pots, "test")]), T, now);

        Assert.Equal(["Onion Yard", "Home"], cards.Select(c => c.Estate.DisplayName));
        var card = cards[0];
        Assert.Equal(sowing.AddHours(24), card.WaterBy);                 // Sun 18:14: the onion's wilt hours
        Assert.All(card.Patches.Single(p => p.IsPots).Cells, c => Assert.Equal(ContainerLook.Wilting, c.Look));
        Assert.Equal(ContainerLook.Wilting, card.Thirst);
        Assert.Equal(new ContainerCount(Beds: 0, Pots: 4, Patch: null), card.FirstToWilt);
        Assert.Equal(new ContainerCount(Beds: 0, Pots: 4, Patch: null), card.Worst);
        Assert.Equal(new ContainerCount(Beds: 0, Pots: 4, Patch: null), card.Water);
        Assert.Equal(new ContainerCount(Beds: 0, Pots: 3, Patch: null), card.TendUnheard);
        Assert.Equal(0, card.PotsWilting);                               // the game never showed them: no note
        Assert.Equal(sowing.AddHours(48), PotCell(card, 126).DiesAt);
        Assert.True(PotCell(card, 126).TendUnheard);
        Assert.False(PotCell(card, 129).TendUnheard);
    }

    [Fact]
    public void AWateringAfterTheSowingMovesThePotsClock()
    {
        var pot = Seed.Tend(Seed.Plant(Pot(Room, 1), Now.AddHours(-30), Krakka, stage: 1), Now.AddHours(-10), Krakka);

        var card = OneCard([pot]);

        Assert.Equal(Now.AddHours(14), card.WaterBy);
        Assert.Equal(ContainerLook.Growing, PotCell(card, 1).Look);
    }

    [Fact] // the other install's watering, relayed, is the same receipt
    public void ARelayedWateringMovesThePotsClock()
    {
        var pot = Seed.Tend(Seed.Plant(Pot(Room, 1), Now.AddHours(-30), Krakka, stage: 1), Now.AddHours(-10), Krakka, by: Seed.Household);

        Assert.Equal(Now.AddHours(14), OneCard([pot]).WaterBy);
    }

    [Fact] // ruling 3 for pots (unchecked in game: slice 22)
    public void ASowingWithNoWateringStartsThePotsClock()
    {
        var card = OneCard([Seed.Plant(Pot(Room, 1), Now.AddHours(-20), Krakka, stage: 1)]);

        Assert.Equal(Now.AddHours(4), card.WaterBy);
        Assert.Equal(ContainerLook.WaterSoon, PotCell(card, 1).Look);
        Assert.True(PotCell(card, 1).TendUnheard);
    }

    [Fact]
    public void ARipePotMakesNoClaim()
    {
        var pot = Seed.Ripe(Seed.Plant(Pot(Room, 1), Now.AddHours(-80), Krakka, stage: 3), Now.AddHours(-1), Krakka);

        var card = OneCard([pot]);

        Assert.Null(card.WaterBy);
        Assert.Equal(ContainerLook.Ripe, PotCell(card, 1).Look);
        Assert.Null(PotCell(card, 1).DiesAt);
        Assert.False(PotCell(card, 1).TendUnheard);
    }

    [Fact] // flowerpot flowers never wilt: no crop row, no hours (ruled 2026-11-10: "flowers in pots don't wilt, but seeds do")
    public void AFlowerInAPotMakesNoClaim()
    {
        var card = OneCard([Seed.Plant(Pot(Room, 1), Now.AddHours(-40), Sunflowers, stage: 2)]);

        Assert.Null(card.WaterBy);
        Assert.Null(card.Lane.WaterFrom);
        var cell = PotCell(card, 1);
        Assert.Equal(ContainerLook.Growing, cell.Look);
        Assert.True(cell.NeverWilts);
        Assert.False(cell.TendUnheard);
        Assert.Equal(ContainerCount.None, card.TendUnheard);
    }

    [Fact] // in the room the live read decides the look; the clock never overrides it
    public void InTheRoomTheLiveReadWinsOverTheClock()
    {
        var beds = new List<ClaimedBed>
        {
            Seed.Plant(Pot(Room, 1), Now.AddHours(-30), Krakka, stage: 1),   // clock: wilting; game: fine
            Seed.Plant(Pot(Room, 2), Now.AddHours(-2), Krakka, stage: 1),    // clock: fine; game: wilting
            Seed.Plant(Pot(Room, 3), Now.AddHours(-40), Krakka, stage: 1),   // clock: dying; game: wilting
            Seed.Plant(Pot(Room, 4), Now.AddHours(-20), Krakka, stage: 1),   // clock: water soon; game: fine
        };
        var live = new Dictionary<int, PotReading>
        {
            [1] = Live(Krakka, 1, wilt: 0), [2] = Live(Krakka, 1, wilt: 1),
            [3] = Live(Krakka, 1, wilt: 1), [4] = Live(Krakka, 1, wilt: 0),
        };

        var card = OneCard(beds, new Standing(Room, Inside: true, live));

        Assert.Equal(ContainerLook.Growing, PotCell(card, 1).Look);
        Assert.Equal(ContainerLook.Wilting, PotCell(card, 2).Look);
        Assert.Equal(ContainerLook.Dying, PotCell(card, 3).Look);      // the game agrees it wilts; the clock says how far
        Assert.Equal(ContainerLook.WaterSoon, PotCell(card, 4).Look);  // no wilt yet is what water soon means
        Assert.True(PotCell(card, 2).WiltShown);
        Assert.True(PotCell(card, 3).WiltShown);
        Assert.False(PotCell(card, 1).WiltShown);
        Assert.Equal(2, card.PotsWilting);                               // the note counts what the game shows
        Assert.Null(card.PotsWiltingSeenAt);
    }

    [Fact] // the room reads the pot ripe before its record has caught up: a ripe pot never claims
    public void InTheRoomALiveRipeReadMakesNoWiltClaim()
    {
        var pot = Seed.Plant(Pot(Room, 1), Now.AddHours(-30), Krakka, stage: 3);   // clock: wilting
        var live = new Dictionary<int, PotReading> { [1] = Live(Krakka, 4, wilt: 1) };

        var card = OneCard([pot], new Standing(Room, Inside: true, live));

        Assert.Equal(ContainerLook.Growing, PotCell(card, 1).Look);
        Assert.Equal(0, card.PotsWilting);
    }

    [Fact] // slice 19's kept sighting still speaks away from the room, over a clock that is fine
    public void AKeptSightingStillMakesAPotWilting()
    {
        var seen = Now.AddMinutes(-12);
        var fine = Seed.Sight(Seed.Plant(Pot(Room, 1), Now.AddHours(-2), Krakka, stage: 1), seen, Krakka, stage: 1);
        fine.SightWilt(seen, wilting: true, stage: 2);
        var dying = Seed.Sight(Seed.Plant(Pot(Room, 2), Now.AddHours(-40), Krakka, stage: 1), seen, Krakka, stage: 1);
        dying.SightWilt(seen, wilting: true, stage: 2);

        var card = OneCard([fine, dying]);

        Assert.Equal(ContainerLook.Wilting, PotCell(card, 1).Look);
        Assert.Equal(seen, PotCell(card, 1).WiltSeenAt);
        Assert.Equal(ContainerLook.Dying, PotCell(card, 2).Look);
        Assert.Equal(seen, PotCell(card, 2).WiltSeenAt);
        Assert.Equal(2, card.PotsWilting);
        Assert.Equal(seen, card.PotsWiltingSeenAt);
    }

    [Fact] // a card with only pots gets a deadline, a water chip and a water bar
    public void APotsOnlyCardHasADeadlineAChipAndABar()
    {
        var card = OneCard([Seed.Plant(Pot(Room, 1), Now.AddHours(-20), Krakka, stage: 1)]);

        Assert.Equal(Now.AddHours(4), card.WaterBy);
        Assert.Equal(new ContainerCount(Beds: 0, Pots: 1, Patch: null), card.Water);
        Assert.Equal(Now.AddHours(-20), card.Lane.WaterFrom);
        Assert.True(card.Lane.FromSowing);
        Assert.Equal(ContainerLook.WaterSoon, card.Thirst);
    }

    [Fact] // the card names the kind that sets its deadline and its worst look, and a lone bed's patch
    public void TheCardNamesTheKindThatNeedsWater()
    {
        var beds = new List<ClaimedBed>
        {
            Seed.Tend(Seed.Plant(Bed(Room, 1, 0), Now.AddHours(-50), Krakka, stage: 1), Now.AddHours(-40), Krakka), // dying, water by -16h
            Seed.Plant(Bed(Room, 0, 1), Now.AddHours(-2), Fig, stage: 1),                                            // +46h
            Seed.Plant(Pot(Room, 1), Now.AddHours(-20), Krakka, stage: 1),                                           // +4h
            Seed.Plant(Pot(Room, 2), Now.AddHours(-10), Krakka, stage: 1),                                           // +14h
        };

        var card = OneCard(beds);

        Assert.Equal(Now.AddHours(-16), card.WaterBy);
        Assert.Equal(new ContainerCount(Beds: 1, Pots: 0, Patch: 1), card.FirstToWilt);
        Assert.Equal(new ContainerCount(Beds: 1, Pots: 0, Patch: 1), card.Worst);
        Assert.Equal(ContainerLook.Dying, card.Thirst);
        Assert.Equal(new ContainerCount(Beds: 1, Pots: 2, Patch: 1), card.Water);   // the cut is now + 24h: the fig is out
        Assert.Equal(new ContainerCount(Beds: 1, Pots: 2, Patch: 0), card.TendUnheard);
        Assert.False(card.Lane.FromSowing);                                           // the dying bed was watered
        Assert.Equal(Now.AddHours(-40), card.Lane.WaterFrom);
    }

    [Fact] // a pot due first sorts its estate above one whose beds are due later
    public void APotDueFirstSortsItsEstateFirst()
    {
        var beds = new List<ClaimedBed>
        {
            Seed.Tend(Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-30), Krakka, stage: 1), Now.AddHours(-10), Krakka), // +14h
            Seed.Plant(Pot(Room, 1), Now.AddHours(-20), Krakka, stage: 1),                                           // +4h
        };

        var cards = Board(beds, null, Estate(Yard, "Yard"), Estate(Room, "Room"));

        Assert.Equal(["Room", "Yard"], cards.Select(c => c.Estate.DisplayName));
    }
}
