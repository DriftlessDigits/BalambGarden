using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>The garden board's one seam: a garden goes in (sowings, waterings, sightings), the
/// visit cards come out. Nothing here reaches inside the board.</summary>
public class GardenBoardTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T16:30:00Z");

    // Species indexes from Data/SpeciesIndex.json. Krakka: grow 72h, wilt 24h. Fig: grow 120h, wilt 48h.
    private const ushort Krakka = 49;
    private const ushort Fig = 65;

    private static readonly EstateKey Yard = new(340, 11, 32);
    private static readonly EstateKey Cottage = new(341, 4, 7);

    private static EstateRecord Estate(EstateKey key, string name) => new() { Key = key, Nickname = name };

    private static AccessRoster Roster(params EstateKey[] keys)
        => new(keys.Select(k => new RosterEstate(k, "test")).ToList());

    private static ClaimedBed Bed(EstateKey estate, int patch, int slot) => new()
    {
        Estate = estate, MapKey = 100 + patch, PatchOrdinal = patch, BedSlot = slot,
    };

    private static IReadOnlyList<VisitCard> Board(
        IReadOnlyList<EstateRecord> estates, IReadOnlyList<ClaimedBed> beds, AccessRoster? roster = null)
        => GardenBoard.Cards(estates, beds, roster ?? Roster(estates.Select(e => e.Key).ToArray()), T, Now);

    [Fact]
    public void CardsComeOutSoonestWaterDeadlineFirst()
    {
        // The cottage's figs were watered 10h ago: water by Now + 38h. The yard's Krakka were
        // sown 20h ago and never watered: water by Now + 4h.
        var beds = new List<ClaimedBed>
        {
            Seed.Tend(Seed.Plant(Bed(Cottage, 0, 0), Now.AddHours(-30), Fig, stage: 1), Now.AddHours(-10), Fig),
            Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-20), Krakka, stage: 1),
        };

        var cards = Board([Estate(Cottage, "Cottage"), Estate(Yard, "Yard")], beds);

        Assert.Equal(["Yard", "Cottage"], cards.Select(c => c.Estate.DisplayName));
        Assert.Equal(Now.AddHours(4), cards[0].WaterBy);
        Assert.Equal(Now.AddHours(38), cards[1].WaterBy);
    }

    [Fact]
    public void EachBedCountsFromItsWateringElseItsSowingElseNothing()
    {
        var watered = Seed.Tend(Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-30), Fig, stage: 1), Now.AddHours(-2), Fig);
        var sown = Seed.Plant(Bed(Yard, 0, 1), Now.AddHours(-10), Krakka, stage: 1);
        var sightedOnly = Seed.Sight(Bed(Yard, 0, 2), Now.AddHours(-1), Krakka, stage: 2);

        var card = Assert.Single(Board([Estate(Yard, "Yard")], [watered, sown, sightedOnly]));
        var cells = Assert.Single(card.Patches).Cells;

        Assert.Equal(Now.AddHours(46), cells[0].WaterBy);   // 2h ago + 48h
        Assert.False(cells[0].TendUnheard);
        Assert.Equal(Now.AddHours(14), cells[1].WaterBy);   // sown 10h ago + 24h
        Assert.True(cells[1].TendUnheard);
        Assert.Null(cells[2].WaterBy);                      // no watering, no sowing: no claim
        Assert.True(cells[2].TendUnheard);

        Assert.Equal(Now.AddHours(14), card.WaterBy);       // the earliest bed's deadline wins
        Assert.Equal(2, card.TendUnheard);
    }

    [Fact] // ruling 5: nothing planted, no card - emptied containers and no containers alike
    public void AnEstateWithNoKnownPlantingHasNoCard()
    {
        var harvested = Seed.Plant(Bed(Cottage, 0, 0), Now.AddHours(-100), Krakka, stage: 1);
        harvested.Rebase(RebaseReason.HarvestWitnessed, cut: Now.AddHours(-5));
        var apartment = new EstateKey(342, 1, 1);
        var elsewhere = new EstateKey(343, 2, 2);

        var cards = Board(
            [Estate(Yard, "Yard"), Estate(Cottage, "Cottage"), Estate(apartment, "Apartment"), Estate(elsewhere, "Elsewhere")],
            [Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-1), Krakka, stage: 1), harvested,
             Seed.Plant(Bed(elsewhere, 0, 0), Now.AddHours(-1), Krakka, stage: 1)],
            Roster(Yard, Cottage, apartment));   // "Elsewhere" is planted but no longer granted

        Assert.Equal(["Yard"], cards.Select(c => c.Estate.DisplayName));
    }

    [Fact] // an estate whose only planting is in pots still needs a visit
    public void APotsOnlyEstateHasACardWithItsPotsPicture()
    {
        var pot = new ClaimedBed { Estate = Cottage, MapKey = 3, PatchOrdinal = 3, BedSlot = 0, IsPot = true };
        Seed.Plant(pot, Now.AddHours(-30), Krakka, stage: 2);

        var card = Assert.Single(Board([Estate(Cottage, "Cottage")], [pot]));

        var pots = Assert.Single(card.Patches);
        Assert.True(pots.IsPots);
        Assert.Equal(3, Assert.Single(pots.Cells).Position);
        Assert.Null(card.WaterBy);          // pots are observed, never clocked
        Assert.Equal(0, card.TendUnheard);
    }

    [Fact]
    public void ChipsCountWhatTheVisitHolds()
    {
        var harvested = Seed.Plant(Bed(Yard, 0, 6), Now.AddHours(-100), Krakka, stage: 1);
        harvested.Rebase(RebaseReason.HarvestWitnessed, cut: Now.AddHours(-5));
        var beds = new List<ClaimedBed>
        {
            Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-20), Krakka, stage: 1),                         // water by +4h
            Seed.Tend(Seed.Plant(Bed(Yard, 0, 1), Now.AddHours(-30), Fig, stage: 1), Now.AddHours(-2), Fig), // +46h: past the day
            Seed.Tend(Seed.Plant(Bed(Yard, 0, 2), Now.AddHours(-20), Krakka, stage: 1), Now.AddHours(-10), Krakka), // +14h
            Seed.Ripe(Seed.Plant(Bed(Yard, 0, 3), Now.AddHours(-80), Krakka, stage: 1), Now.AddHours(-1), Krakka), // ripe now
            Seed.Tend(Seed.Plant(Bed(Yard, 0, 4), Now.AddHours(-60), Krakka, stage: 2), Now.AddHours(-1), Krakka), // +23h, ripe at +12h
            Seed.Plant(Bed(Yard, 0, 5), Now.AddHours(-10), Fig, stage: 1),                             // +38h, ripe at +110h
            harvested,                                                                                 // known empty
        };

        var card = Assert.Single(Board([Estate(Yard, "Yard")], beds));

        Assert.Equal(Now.AddHours(4), card.WaterBy);
        Assert.Equal(3, card.Water);     // slots 0, 2, 4: due within a day of the card's deadline
        Assert.Equal(2, card.Harvest);   // slot 3 ripe now, slot 4 ripening within a day
        Assert.Equal(1, card.RipeNow);
        Assert.Equal(1, card.Replant);   // slot 6
        Assert.Equal(ContainerLook.Empty, card.Patches[0].Cells[6].Look);
    }

    [Fact] // a deadline already past is no reason to drop the beds due in the next hours
    public void ThePastDeadlineChipWindowStartsFromNow()
    {
        var beds = new List<ClaimedBed>
        {
            Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-30), Krakka, stage: 1),   // water by -6h
            Seed.Plant(Bed(Yard, 0, 1), Now.AddHours(-4), Krakka, stage: 1),    // water by +20h
        };

        var card = Assert.Single(Board([Estate(Yard, "Yard")], beds));

        Assert.Equal(Now.AddHours(-6), card.WaterBy);
        Assert.Equal(2, card.Water);
        Assert.Equal(ContainerLook.PastDeadline, card.Patches[0].Cells[0].Look);
    }

    [Fact] // user story 11: crops that ripen together share one ripe time; pots are their own group
    public void RipeTimesGroupByCropAndCropsRipeningTogetherShareAGroup()
    {
        const ushort mirror = 17;   // grow 120h
        var pot = new ClaimedBed { Estate = Yard, MapKey = 3, PatchOrdinal = 3, BedSlot = 0, IsPot = true };
        var sightedMirror = Seed.Sight(Bed(Yard, 1, 0), Now.AddHours(-10), mirror, stage: 1);
        Seed.Sight(sightedMirror, Now.AddHours(-1), mirror, stage: 2);
        var beds = new List<ClaimedBed>
        {
            Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-60), Krakka, stage: 2),    // ripe +12h, anchored
            Seed.Plant(Bed(Yard, 0, 1), Now.AddHours(-58), Krakka, stage: 2),    // +14h: the same crop, its earliest wins
            Seed.Plant(Bed(Yard, 0, 2), Now.AddHours(-106), Fig, stage: 3),      // +14h: within the group's span
            sightedMirror,                                                       // +70h..+79h, bracketed
            Seed.Plant(pot, Now.AddHours(-58), Krakka, stage: 2),                // +14h, but a pot
        };

        var groups = Assert.Single(Board([Estate(Yard, "Yard")], beds)).Ripe;

        Assert.Equal(3, groups.Count);
        Assert.False(groups[0].IsPots);
        Assert.Equal([Krakka, Fig], groups[0].Crops.Select(c => c.Species));
        Assert.Equal(new EtaWindow(Now.AddHours(12), Now.AddHours(12), Provenance.Anchored), groups[0].Window);
        Assert.True(groups[1].IsPots);
        Assert.Equal(Now.AddHours(14), groups[1].Window.Earliest);
        Assert.Equal([mirror], groups[2].Crops.Select(c => c.Species));
        Assert.Equal(new EtaWindow(Now.AddHours(70), Now.AddHours(79), Provenance.Bracketed), groups[2].Window);
    }

    private static ClaimedBed Pot(EstateKey estate, int mapKey)
        => new() { Estate = estate, MapKey = mapKey, PatchOrdinal = mapKey, BedSlot = 0, IsPot = true };

    [Fact] // ruling 4: water deadlines first; estates with no water clock after, by next harvest, ripe now first
    public void EstatesWithNoWaterClockFollowByNextHarvestAndRipeNowLifts()
    {
        var room = new EstateKey(342, 1, 1);
        var plot = new EstateKey(343, 2, 2);
        var beds = new List<ClaimedBed>
        {
            Seed.Plant(Pot(Cottage, 1), Now.AddHours(-67), Krakka, stage: 3),            // pots ripe +5h
            Seed.Ripe(Seed.Plant(Pot(room, 1), Now.AddHours(-80), Krakka, stage: 3), Now.AddHours(-1), Krakka), // ripe now
            Seed.Sight(Pot(plot, 1), Now.AddHours(-1), 0, stage: 0),                     // contents unidentified: no window
            Seed.Tend(Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-30), Fig, stage: 1), Now.AddHours(-18), Fig), // water by +30h
        };

        var cards = Board(
            [Estate(plot, "Plot"), Estate(Cottage, "Cottage"), Estate(room, "Room"), Estate(Yard, "Yard")], beds);

        Assert.Equal(["Yard", "Room", "Cottage", "Plot"], cards.Select(c => c.Estate.DisplayName));
    }

    [Fact] // ruling 4 as narrowed at slice 3: pot wilt speaks only where the player stands, from the live read
    public void APotWiltsOnlyOnTheCardOfTheRoomThePlayerStandsIn()
    {
        var room = new EstateKey(342, 1, 1);
        var beds = new List<ClaimedBed>
        {
            Seed.Plant(Pot(room, 1), Now.AddHours(-10), Krakka, stage: 1),
            Seed.Plant(Pot(room, 2), Now.AddHours(-10), Krakka, stage: 1),
            Seed.Plant(Pot(Cottage, 1), Now.AddHours(-10), Krakka, stage: 1),   // same map key, another estate
        };
        var live = new Dictionary<int, PotReading>
        {
            [1] = new(Krakka, Stage: 1, Color: 0, Extra: 0, Occupied: true, Recognized: true, Wilt: 1),
            [2] = new(Krakka, Stage: 1, Color: 0, Extra: 0, Occupied: true, Recognized: true, Wilt: 0),
        };
        var estates = new List<EstateRecord> { Estate(room, "Room"), Estate(Cottage, "Cottage") };

        var standing = GardenBoard.Cards(estates, beds, Roster(room, Cottage), T, Now, standing: new Standing(room, Inside: true, live));
        var roomCard = standing.Single(c => c.Estate.Key == room);
        Assert.Equal(1, roomCard.PotsWilting);
        Assert.Equal([ContainerLook.Wilting, ContainerLook.Growing], roomCard.Patches.Single().Cells.Select(c => c.Look));
        Assert.Equal(0, standing.Single(c => c.Estate.Key == Cottage).PotsWilting);

        var away = GardenBoard.Cards(estates, beds, Roster(room, Cottage), T, Now, standing: null);
        Assert.All(away, c => Assert.Equal(0, c.PotsWilting));
    }

    [Fact] // a ripe pot broadcasts a wilt flag the game offers no verb for (the pot wilt rule)
    public void ARipePotMakesNoWiltClaim()
    {
        var room = new EstateKey(342, 1, 1);
        var pot = Seed.Ripe(Seed.Plant(Pot(room, 1), Now.AddHours(-80), Krakka, stage: 3), Now.AddHours(-1), Krakka);
        var live = new Dictionary<int, PotReading>
        {
            [1] = new(Krakka, Stage: 4, Color: 0, Extra: 0, Occupied: true, Recognized: true, Wilt: 1),
        };

        var card = Assert.Single(GardenBoard.Cards(
            [Estate(room, "Room")], [pot], Roster(room), T, Now, standing: new Standing(room, Inside: true, live)));

        Assert.Equal(0, card.PotsWilting);
        Assert.Equal(ContainerLook.Ripe, card.Patches.Single().Cells.Single().Look);
    }

    [Fact] // a record made by a tend heard before any read was never emptied: not a replant
    public void ARecordNeverReadIsNotKnownEmpty()
    {
        var tendedFirst = Seed.Tend(Bed(Yard, 0, 1), Now.AddHours(-2), Krakka);
        var beds = new List<ClaimedBed> { Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-1), Krakka, stage: 1), tendedFirst };

        var card = Assert.Single(Board([Estate(Yard, "Yard")], beds));

        Assert.Equal(ContainerLook.Unread, card.Patches[0].Cells[1].Look);
        Assert.Equal(0, card.Replant);
    }

    [Fact] // the pots are one group of their own, whatever crops they hold
    public void ThePotsAreOneRipeGroup()
    {
        var beds = new List<ClaimedBed>
        {
            Seed.Plant(Pot(Cottage, 1), Now.AddHours(-60), Krakka, stage: 2),   // ripe +12h
            Seed.Plant(Pot(Cottage, 2), Now.AddHours(-10), Fig, stage: 1),      // ripe +110h
        };

        var group = Assert.Single(Assert.Single(Board([Estate(Cottage, "Cottage")], beds)).Ripe);

        Assert.True(group.IsPots);
        Assert.Equal(Now.AddHours(12), group.Window.Earliest);
        Assert.Equal([Krakka, Fig], group.Crops.Select(c => c.Species));
    }

    [Fact] // among estates with no water clock, a plant seen ripe lifts the card above a window merely open
    public void SeenRipeLiftsAboveAnOpenWindow()
    {
        var room = new EstateKey(342, 1, 1);
        var opened = Seed.Sight(Pot(Cottage, 1), Now.AddHours(-30), Krakka, stage: 3);   // estimated window, already open
        var beds = new List<ClaimedBed>
        {
            opened,
            Seed.Ripe(Seed.Plant(Pot(room, 1), Now.AddHours(-80), Krakka, stage: 3), Now.AddHours(-1), Krakka),
        };

        var cards = Board([Estate(Cottage, "Cottage"), Estate(room, "Room")], beds);

        Assert.True(cards.Single(c => c.Estate.Key == Cottage).Ripe[0].Window.Earliest < Now);
        Assert.Equal(["Room", "Cottage"], cards.Select(c => c.Estate.DisplayName));
    }

    [Fact] // the last indoor read outlives the room: outside, it says nothing about the pots
    public void TheLiveReadSpeaksOnlyWhileInside()
    {
        var pot = Seed.Plant(Pot(Yard, 1), Now.AddHours(-10), Krakka, stage: 1);   // the house's pots share the yard's key
        var live = new Dictionary<int, PotReading>
        {
            [1] = new(Krakka, Stage: 1, Color: 0, Extra: 0, Occupied: true, Recognized: true, Wilt: 1),
        };

        var card = Assert.Single(GardenBoard.Cards(
            [Estate(Yard, "Yard")], [pot], Roster(Yard), T, Now, standing: new Standing(Yard, Inside: false, live)));

        Assert.Equal(0, card.PotsWilting);
    }

    [Fact] // the card's thirst is its worst bed's: the window colours the headline from it
    public void ThirstIsTheWorstBed()
    {
        var soon = Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-20), Krakka, stage: 1);   // 20h of 24: water soon
        var past = Seed.Plant(Bed(Cottage, 0, 0), Now.AddHours(-30), Krakka, stage: 1); // past its deadline
        var fine = Seed.Plant(Bed(Cottage, 0, 1), Now.AddHours(-1), Krakka, stage: 1);

        var cards = Board([Estate(Yard, "Yard"), Estate(Cottage, "Cottage")], [soon, past, fine]);

        Assert.Equal(ContainerLook.PastDeadline, cards.Single(c => c.Estate.Key == Cottage).Thirst);
        Assert.Equal(ContainerLook.WaterSoon, cards.Single(c => c.Estate.Key == Yard).Thirst);
    }

    [Fact] // GLOSSARY "Bed": 1 2 3 across the top, 4 and 5 down the right, 6 and 7 back along the bottom, 8 on the left
    public void BedsRingThePatchClockwiseFromTheTopLeft()
    {
        Assert.Equal((0, 0), GardenBoard.RingPosition(0));
        Assert.Equal((2, 0), GardenBoard.RingPosition(2));
        Assert.Equal((2, 1), GardenBoard.RingPosition(3));
        Assert.Equal((2, 2), GardenBoard.RingPosition(4));
        Assert.Equal((0, 2), GardenBoard.RingPosition(6));
        Assert.Equal((0, 1), GardenBoard.RingPosition(7));
    }

    [Fact] // a patch picture is always eight slots, so an empty slot reads as a hole
    public void APatchPictureHasEightSlotsInSlotOrder()
    {
        var bed = Seed.Plant(Bed(Yard, 1, 5), Now.AddHours(-1), Krakka, stage: 1);

        var patch = Assert.Single(Assert.Single(Board([Estate(Yard, "Yard")], [bed])).Patches);

        Assert.Equal(1, patch.Ordinal);
        Assert.Equal(Enumerable.Range(0, 8), patch.Cells.Select(c => c.Position));
        Assert.Equal(ContainerLook.Unclaimed, patch.Cells[0].Look);
        Assert.Same(bed, patch.Cells[5].Bed);
    }

    // ---- the timeline lanes (slice 4): one lane per card, under the cards ----

    [Fact] // user story 18: the water bar fills from the watering (or the sowing) to the deadline
    public void TheWaterBarStartsWhereTheDeadlineBedsClockStarts()
    {
        var watered = Seed.Tend(Seed.Plant(Bed(Cottage, 0, 0), Now.AddHours(-30), Krakka, stage: 1), Now.AddHours(-10), Krakka);
        var sown = Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-20), Krakka, stage: 1);
        var pots = Seed.Plant(Pot(Cottage, 1), Now.AddHours(-10), Krakka, stage: 1);
        var room = new EstateKey(342, 1, 1);
        var potsOnly = Seed.Plant(Pot(room, 1), Now.AddHours(-10), Krakka, stage: 1);

        var cards = Board([Estate(Cottage, "Cottage"), Estate(Yard, "Yard"), Estate(room, "Room")], [watered, sown, pots, potsOnly]);

        Assert.Equal(Now.AddHours(-20), cards.Single(c => c.Estate.Key == Yard).Lane.WaterFrom);    // never watered: the sowing
        Assert.Equal(Now.AddHours(-10), cards.Single(c => c.Estate.Key == Cottage).Lane.WaterFrom); // the watering
        Assert.Null(cards.Single(c => c.Estate.Key == room).Lane.WaterFrom);                        // pots are never clocked
    }

    [Fact] // orient scope call 2026-11-08: on a mixed card the bar is the deadline bed's own clock, not the oldest watering's
    public void OnAMixedCardTheWaterBarIsTheDeadlineBedsClock()
    {
        var fig = Seed.Tend(Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-50), Fig, stage: 1), Now.AddHours(-40), Fig);         // water by +8h
        var krakka = Seed.Tend(Seed.Plant(Bed(Yard, 0, 1), Now.AddHours(-30), Krakka, stage: 1), Now.AddHours(-20), Krakka); // water by +4h

        var card = Assert.Single(Board([Estate(Yard, "Yard")], [fig, krakka]));

        Assert.Equal(Now.AddHours(4), card.WaterBy);
        Assert.Equal(Now.AddHours(-20), card.Lane.WaterFrom);   // the Krakka's watering, not the fig's older one
    }

    [Fact] // user story 17: an anchored ripe window is a tick; a bracketed one a band its own width, an estimated one striped
    public void EachRipeGroupIsATickOrABandOnItsRow()
    {
        const ushort mirror = 17;   // grow 120h
        var bracketed = Seed.Sight(Bed(Yard, 1, 0), Now.AddHours(-10), mirror, stage: 1);
        Seed.Sight(bracketed, Now.AddHours(-1), mirror, stage: 2);
        var beds = new List<ClaimedBed>
        {
            Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-60), Krakka, stage: 2),   // sown: anchored at +12h
            bracketed,                                                          // two stages seen: +70h..+79h
            Seed.Sight(Bed(Yard, 1, 1), Now.AddHours(-1), Fig, stage: 1),        // one stage seen: +79h..+119h
            Seed.Plant(Pot(Yard, 3), Now.AddHours(-58), Krakka, stage: 2),       // a pot, sown: +14h
        };

        var card = Assert.Single(Board([Estate(Yard, "Yard")], beds));

        Assert.Equal(
            [
                (LaneRow.Beds, LaneMarkKind.Tick, Now.AddHours(12), Now.AddHours(12)),
                (LaneRow.Pots, LaneMarkKind.Tick, Now.AddHours(14), Now.AddHours(14)),
                (LaneRow.Beds, LaneMarkKind.Band, Now.AddHours(70), Now.AddHours(79)),
                (LaneRow.Beds, LaneMarkKind.EstimatedBand, Now.AddHours(79), Now.AddHours(119)),
            ],
            card.Lane.Marks.Select(m => (m.Row, m.Kind, m.From, m.To)));
        Assert.Equal([Krakka], card.Lane.Marks[0].Crops);
        Assert.Equal([Fig], card.Lane.Marks[3].Crops);

        // The lane and the card cannot disagree: each mark is the card's own ripe group.
        Assert.Equal(card.Ripe.Count, card.Lane.Marks.Count);
        Assert.All(card.Lane.Marks.Zip(card.Ripe), pair => Assert.Same(pair.Second, pair.First.Group));
    }

    [Fact] // orient scope call 2026-11-08: a plant seen ripe has no window, so the lane marks it at the now line
    public void WhatIsSeenRipeIsMarkedRipeNowOnItsRow()
    {
        var beds = new List<ClaimedBed>
        {
            Seed.Ripe(Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-80), Krakka, stage: 3), Now.AddHours(-1), Krakka),
            Seed.Ripe(Seed.Plant(Bed(Yard, 0, 1), Now.AddHours(-80), Krakka, stage: 3), Now.AddHours(-1), Krakka),
            Seed.Ripe(Seed.Plant(Pot(Yard, 3), Now.AddHours(-130), Fig, stage: 3), Now.AddHours(-2), Fig),
            Seed.Plant(Bed(Yard, 0, 2), Now.AddHours(-60), Krakka, stage: 2),   // still growing: its tick, not ripe now
        };

        var marks = Assert.Single(Board([Estate(Yard, "Yard")], beds)).Lane.Marks;

        var inBeds = Assert.Single(marks, m => m.Kind == LaneMarkKind.RipeNow && m.Row == LaneRow.Beds);
        Assert.Equal((Now, Now), (inBeds.From, inBeds.To));
        Assert.Equal([Krakka], inBeds.Crops);   // one crop, however many beds
        var pots = Assert.Single(marks, m => m.Kind == LaneMarkKind.RipeNow && m.Row == LaneRow.Pots);
        Assert.Equal([Fig], pots.Crops);
        Assert.Single(marks, m => m.Kind == LaneMarkKind.Tick);
    }

    [Fact] // user story 16: the lanes span from a few hours ago to six days ahead; past its end nothing is drawn
    public void TheLanesSpanAFewHoursAgoToSixDaysAhead()
    {
        Assert.Equal(new TimelineSpan(Now.AddHours(-6), Now.AddDays(6)), GardenBoard.Span(Now));

        const ushort mirror = 17;   // grow 120h
        var beds = new List<ClaimedBed>
        {
            Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-82), Krakka, stage: 3),   // anchored at -10h, not yet seen ripe
            Seed.Plant(Bed(Yard, 0, 1), Now.AddHours(-30), Fig, stage: 1),      // +90h
            Seed.Plant(Bed(Yard, 1, 0), Now.AddHours(30), mirror, stage: 0),    // a clock-skewed sowing: +150h, past the end
        };

        var card = Assert.Single(Board([Estate(Yard, "Yard")], beds));

        Assert.Equal(3, card.Ripe.Count);   // the card says all three
        // A window that closed before the span is the likeliest harvest on the estate: it stays.
        Assert.Equal([Now.AddHours(-10), Now.AddHours(90)], card.Lane.Marks.Select(m => m.From));
    }

    [Fact] // a day line sits at the reader's own midnight, each day with its own offset across a clock change
    public void MidnightsFollowTheReadersClockAcrossAClockChange()
    {
        var central = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");   // daylight time ends Sun 2026-11-01 2am
        var span = new TimelineSpan(DateTimeOffset.Parse("2026-10-31T17:00:00Z"), DateTimeOffset.Parse("2026-11-03T12:00:00Z"));

        Assert.Equal(
            [
                DateTimeOffset.Parse("2026-11-01T00:00:00-05:00"),
                DateTimeOffset.Parse("2026-11-02T00:00:00-06:00"),
                DateTimeOffset.Parse("2026-11-03T00:00:00-06:00"),
            ],
            span.Midnights(central));
        Assert.Equal(TimeSpan.FromHours(-6), span.Midnights(central)[1].Offset);
    }
}
