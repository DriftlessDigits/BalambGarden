using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>Rethink slice 5: the card of the estate the player stands at (the here card) comes
/// first; where the player stands, a container the record holds planted that the live map shows
/// empty reads "empty, not confirmed" (the game wins on a content mismatch) under today's guards;
/// and each container's menu offers only the presses its state allows. A garden goes in, the
/// board comes out.</summary>
public class HereCardTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T16:30:00Z");

    private const ushort Krakka = 49;   // grow 72h, wilt 24h
    private const ushort Fig = 65;      // grow 120h, wilt 48h

    private static readonly EstateKey Yard = new(340, 11, 32);
    private static readonly EstateKey Cottage = new(341, 4, 7);
    private static readonly EstateKey Room = new(342, 1, 1);

    private static EstateRecord Estate(EstateKey key, string name) => new() { Key = key, Nickname = name };

    private static AccessRoster Roster(params EstateKey[] keys)
        => new(keys.Select(k => new RosterEstate(k, "test")).ToList());

    private static ClaimedBed Bed(EstateKey estate, int patch, int slot) => new()
    {
        Estate = estate, MapKey = 100 + patch, PatchOrdinal = patch, BedSlot = slot,
    };

    private static ClaimedBed Pot(EstateKey estate, int mapKey)
        => new() { Estate = estate, MapKey = mapKey, PatchOrdinal = mapKey, BedSlot = 0, IsPot = true };

    /// <summary>Growing Krakka at stage 2, sown 30 h ago, watered an hour ago.</summary>
    private static ClaimedBed Growing(ClaimedBed bed)
        => Seed.Tend(Seed.Plant(bed, Now.AddHours(-30), Krakka, stage: 2), Now.AddHours(-1), Krakka);

    /// <summary>Krakka the game showed ripe an hour ago.</summary>
    private static ClaimedBed SeenRipe(ClaimedBed bed)
        => Seed.Ripe(Seed.Tend(Seed.Plant(bed, Now.AddHours(-80), Krakka, stage: 3), Now.AddHours(-2), Krakka),
            Now.AddHours(-1), Krakka);

    /// <summary>The live outdoor read of one patch key: the given slots empty, every other slot
    /// growing Krakka.</summary>
    private static Dictionary<int, IReadOnlyList<BedReading>> Outdoor(int key, params int[] emptySlots)
        => new()
        {
            [key] = Enumerable.Range(0, GardenBoard.PatchSlots)
                .Select(s => emptySlots.Contains(s)
                    ? new BedReading(s, 0, 0, 0, Occupied: false)
                    : new BedReading(s, Krakka, 2, 0, Occupied: true))
                .ToList(),
        };

    private static Standing Outside(EstateKey at, IReadOnlyDictionary<int, IReadOnlyList<BedReading>> live,
        Func<ClaimedBed, bool>? awaitingSow = null)
        => new(at, Inside: false, new Dictionary<int, PotReading>())
            { LiveOutdoor = live, AwaitingSow = awaitingSow };

    private static Standing Inside(EstateKey at, IReadOnlyDictionary<int, PotReading> live, bool mapAnswered)
        => new(at, Inside: true, live) { MapAnswered = mapAnswered };

    private static PotReading LivePot(bool occupied)
        => new(occupied ? Krakka : (ushort)0, Stage: occupied ? (byte)2 : (byte)0, Color: 0, Extra: 0,
            Occupied: occupied, Recognized: occupied, Wilt: 0);

    private static VisitCard CardOf(IReadOnlyList<ClaimedBed> beds, EstateKey estate, Standing? standing,
        Func<ClaimedBed, bool>? held = null)
        => GardenBoard.Cards([Estate(estate, "Here")], beds, Roster(estate), T, Now, standing, held).Single();

    // ------------------------------------------------------------------ the here card's place

    [Fact] // ruling 18: the estate you stand at rises to the top, whatever its deadline
    public void TheCardOfTheEstateYouStandAtComesFirst()
    {
        var beds = new List<ClaimedBed>
        {
            Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-20), Krakka, stage: 1),   // water by Now + 4h
            Growing(Bed(Cottage, 0, 0)),                                          // water by Now + 23h
        };
        var estates = new[] { Estate(Yard, "Yard"), Estate(Cottage, "Cottage") };

        var away = GardenBoard.Cards(estates, beds, Roster(Yard, Cottage), T, Now);
        var atCottage = GardenBoard.Cards(estates, beds, Roster(Yard, Cottage), T, Now,
            Outside(Cottage, new Dictionary<int, IReadOnlyList<BedReading>>()));

        Assert.Equal(["Yard", "Cottage"], away.Select(c => c.Estate.DisplayName));
        Assert.Equal(["Cottage", "Yard"], atCottage.Select(c => c.Estate.DisplayName));
    }

    // ------------------------------------------------------------------ reads empty now

    [Fact] // the folded-in overlay: planted on the record, empty on the live map where you stand
    public void AGrowingBedTheLiveMapShowsEmptyReadsEmptyNotConfirmed()
    {
        var bed = Growing(Bed(Yard, 0, 2));
        var card = CardOf([bed], Yard, Outside(Yard, Outdoor(100, emptySlots: 2)));

        var cell = card.Patches[0].Cells[2];
        Assert.Equal(ContainerLook.ReadsEmpty, cell.Look);
        Assert.Null(cell.WaterBy);
        Assert.Null(cell.Ripe);
        Assert.False(cell.TendUnheard);
        Assert.Null(card.WaterBy);
        Assert.Equal(0, card.Water.Total);
        Assert.Equal(0, card.Harvest);
        Assert.Equal(1, card.Replant);
    }

    [Fact] // no harvest claim either: a ripe record the map shows empty was harvested (or cleared)
    public void ASeenRipeBedTheLiveMapShowsEmptyMakesNoHarvestClaim()
    {
        var bed = SeenRipe(Bed(Yard, 0, 0));
        var card = CardOf([bed], Yard, Outside(Yard, Outdoor(100, emptySlots: 0)));

        Assert.Equal(ContainerLook.ReadsEmpty, card.Patches[0].Cells[0].Look);
        Assert.Equal(0, card.Harvest);
        Assert.Equal(0, card.RipeNow);
        Assert.Equal(1, card.Replant);
        Assert.DoesNotContain(card.Lane.Marks, m => m.Kind is LaneMarkKind.RipeNow or LaneMarkKind.RipeByClock);
    }

    [Fact] // only where you stand: another estate's live read says nothing about this one
    public void AwayFromTheEstateTheBedReadsAsTheRecordSays()
    {
        var bed = Growing(Bed(Yard, 0, 2));
        var card = CardOf([bed], Yard, Outside(Cottage, Outdoor(100, emptySlots: 2)));

        Assert.Equal(ContainerLook.Growing, card.Patches[0].Cells[2].Look);
        Assert.Equal(0, card.Replant);
    }

    [Fact] // the estate tabs ask the rule directly, with wherever the player stands
    public void TheRuleAskedDirectlyAnswersOnlyForTheEstateYouStandAt()
    {
        var bed = Growing(Bed(Yard, 0, 2));
        var live = Outdoor(100, emptySlots: 2);

        Assert.True(GardenBoard.MapShowsEmpty(bed, Outside(Yard, live)));
        Assert.False(GardenBoard.MapShowsEmpty(bed, Outside(Cottage, live)));
        Assert.False(GardenBoard.ReadsEmptyNow(bed, Outside(Cottage, live)));
    }

    [Fact]
    public void StandingNowhereTheBedReadsAsTheRecordSays()
        => Assert.Equal(ContainerLook.Growing, CardOf([Growing(Bed(Yard, 0, 2))], Yard, standing: null).Patches[0].Cells[2].Look);

    [Fact] // inside the house the yard's last outdoor read is not in front of you
    public void InsideTheBedsReadAsTheRecordSays()
    {
        var bed = Growing(Bed(Yard, 0, 2));
        var standing = new Standing(Yard, Inside: true, new Dictionary<int, PotReading>())
            { LiveOutdoor = Outdoor(100, emptySlots: 2), MapAnswered = true };

        Assert.Equal(ContainerLook.Growing, CardOf([bed], Yard, standing).Patches[0].Cells[2].Look);
    }

    [Fact] // silence is never evidence: no read for the patch, or none for the slot, keeps the record
    public void APatchOrSlotTheLiveReadDoesNotCoverReadsAsTheRecordSays()
    {
        var bed = Growing(Bed(Yard, 0, 2));
        var noKey = Outside(Yard, Outdoor(999, emptySlots: 2));
        var noSlot = Outside(Yard, new Dictionary<int, IReadOnlyList<BedReading>>
            { [100] = [new BedReading(0, Krakka, 2, 0, Occupied: true)] });

        Assert.Equal(ContainerLook.Growing, CardOf([bed], Yard, noKey).Patches[0].Cells[2].Look);
        Assert.Equal(ContainerLook.Growing, CardOf([bed], Yard, noSlot).Patches[0].Cells[2].Look);
    }

    [Fact] // never a bed our own chain just sowed: the map is a beat behind the seed (2026-11-07)
    public void ABedAwaitingOurOwnSowReadsAsTheRecordSays()
    {
        var bed = Seed.Plant(Bed(Yard, 0, 2), Now.AddSeconds(-1), Krakka, stage: 0);
        var standing = Outside(Yard, Outdoor(100, emptySlots: 2), awaitingSow: b => b == bed);

        Assert.NotEqual(ContainerLook.ReadsEmpty, CardOf([bed], Yard, standing).Patches[0].Cells[2].Look);
    }

    [Fact] // a record already empty is plainly empty: nothing for the map to contradict
    public void AnEmptiedRecordStaysPlainEmpty()
    {
        var bed = Growing(Bed(Yard, 0, 2));
        bed.Rebase(RebaseReason.ReadsEmpty, cut: Now.AddMinutes(-1));
        var neighbour = Growing(Bed(Yard, 0, 0));   // an estate with nothing planted has no card

        var card = CardOf([bed, neighbour], Yard, Outside(Yard, Outdoor(100, emptySlots: 2)));
        Assert.Equal(ContainerLook.Empty, card.Patches[0].Cells[2].Look);
        Assert.Equal(1, card.Replant);
    }

    [Fact] // the conductor's call: a container the look rule still holds keeps the clock's word
    public void AContainerTheLookRuleStillHoldsKeepsTheClocksWord()
    {
        var bed = Growing(Bed(Yard, 0, 2));
        var card = CardOf([bed], Yard, Outside(Yard, Outdoor(100, emptySlots: 2)), held: b => b == bed);

        var cell = card.Patches[0].Cells[2];
        Assert.Equal(ContainerLook.Growing, cell.Look);
        Assert.NotNull(cell.WaterBy);
        Assert.Equal(0, card.Replant);
    }

    [Fact] // pots: the room's live read, once the map has answered this visit; a zeroed entry is "empty"
    public void APotTheRoomShowsEmptyReadsEmptyNotConfirmedOnceTheMapAnswered()
    {
        var gone = Growing(Pot(Room, 1));
        var zeroed = Growing(Pot(Room, 2));
        var here = Growing(Pot(Room, 3));
        var live = new Dictionary<int, PotReading> { [2] = LivePot(occupied: false), [3] = LivePot(occupied: true) };

        var card = CardOf([gone, zeroed, here], Room, Inside(Room, live, mapAnswered: true));

        var looks = card.Patches.Single(p => p.IsPots).Cells.Select(c => c.Look).ToList();
        Assert.Equal([ContainerLook.ReadsEmpty, ContainerLook.ReadsEmpty, ContainerLook.Growing], looks);
        Assert.Equal(2, card.Replant);
    }

    [Fact] // before the map answered this visit, an empty room is a map not talking yet (2026-08-28)
    public void BeforeTheMapAnsweredAPotReadsAsTheRecordSays()
    {
        var pot = Growing(Pot(Room, 1));
        var card = CardOf([pot], Room, Inside(Room, new Dictionary<int, PotReading>(), mapAnswered: false));

        Assert.Equal(ContainerLook.Growing, card.Patches.Single(p => p.IsPots).Cells[0].Look);
    }

    [Fact] // standing outside, the pots' room is not in front of you
    public void OutsideAPotReadsAsTheRecordSays()
    {
        var pot = Growing(Pot(Yard, 1));
        var standing = new Standing(Yard, Inside: false, new Dictionary<int, PotReading>())
            { LiveOutdoor = Outdoor(100), MapAnswered = true };

        Assert.Equal(ContainerLook.Growing, CardOf([pot], Yard, standing).Patches.Single(p => p.IsPots).Cells[0].Look);
    }

    // ------------------------------------------------------------------ the menus

    [Fact] // criterion: an empty bed's menu offers nothing it cannot do; a ripe bed's offers Harvest and Replant
    public void EachContainerOffersOnlyThePressesItsStateAllows()
    {
        var growing = Growing(Bed(Yard, 0, 0));
        var thirsty = Seed.Plant(Bed(Yard, 0, 1), Now.AddHours(-23), Krakka, stage: 1);      // water soon
        var ripe = SeenRipe(Bed(Yard, 0, 2));
        var ripeByClock = Seed.Tend(Seed.Plant(Bed(Yard, 0, 3), Now.AddHours(-74), Krakka, stage: 3),
            Now.AddHours(-2), Krakka);                                                        // ripe time 2 h ago
        var emptied = Growing(Bed(Yard, 0, 4));
        emptied.Rebase(RebaseReason.HarvestWitnessed, cut: Now.AddMinutes(-5));
        var dead = Seed.Sight(Seed.Plant(Bed(Yard, 0, 5), Now.AddHours(-30), Krakka, stage: 2),
            Now.AddMinutes(-5), Krakka, stage: GardenStage.Dead);
        var vanished = Growing(Bed(Yard, 0, 6));
        var card = CardOf([growing, thirsty, ripe, ripeByClock, emptied, dead, vanished], Yard,
            Outside(Yard, Outdoor(100, emptySlots: 6)));

        IReadOnlyList<CellVerb> Verbs(int slot) => GardenBoard.VerbsFor(card.Patches[0].Cells[slot]);

        Assert.Equal([CellVerb.Tend], Verbs(0));
        Assert.Equal(ContainerLook.WaterSoon, card.Patches[0].Cells[1].Look);
        Assert.Equal([CellVerb.Tend], Verbs(1));
        Assert.Equal([CellVerb.Harvest, CellVerb.Replant], Verbs(2));
        Assert.True(card.Patches[0].Cells[3] is { Look: ContainerLook.Ripe, ByClock: true });
        Assert.Equal([CellVerb.Harvest, CellVerb.Replant], Verbs(3));
        Assert.Empty(Verbs(4));   // emptied
        Assert.Empty(Verbs(5));   // dead
        Assert.Empty(Verbs(6));   // reads empty now
        Assert.Empty(Verbs(7));   // nothing recorded
    }

    [Fact] // a wilting or dying plant still drinks
    public void AWiltingOrDyingPlantStillOffersTend()
    {
        var wilting = Seed.Plant(Bed(Yard, 0, 0), Now.AddHours(-30), Krakka, stage: 1);   // wilt +24h, dying +36h
        var dying = Seed.Plant(Bed(Yard, 0, 1), Now.AddHours(-40), Krakka, stage: 1);
        var card = CardOf([wilting, dying], Yard, standing: null);

        Assert.Equal(ContainerLook.Wilting, card.Patches[0].Cells[0].Look);
        Assert.Equal(ContainerLook.Dying, card.Patches[0].Cells[1].Look);
        Assert.Equal([CellVerb.Tend], GardenBoard.VerbsFor(card.Patches[0].Cells[0]));
        Assert.Equal([CellVerb.Tend], GardenBoard.VerbsFor(card.Patches[0].Cells[1]));
    }
}
