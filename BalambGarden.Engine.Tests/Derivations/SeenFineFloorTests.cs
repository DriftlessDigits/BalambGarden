using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>Rethink slice 21, at the garden board's seam: a pot the room shows growing and not
/// wilting at T was watered after T minus its wilt hours, or it would be wilting (spec ruling 23,
/// "a pot seen fine floors its clock"). The floor puts the deadline at T itself, so a pot whose
/// clock rests on the floor alone reads "water soon" with a "?" through its wilting band, never
/// wilting, and dying from the floor's dying point (ruled 2026-11-10 at the plan call: "B, or
/// maybe overlay a ? over the square"). The garden goes in the way the plugin learns it - the
/// census's map reads and receipts - and the cards come out.</summary>
public class SeenFineFloorTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-11T01:17:00Z");
    private static readonly DateTimeOffset Seen = Now.AddMinutes(-10);

    // Krakka Root: wilt 24h, wither 48h (Data/Crops.json); dying from 36h (halfway).
    private const ushort Krakka = 49;
    private const ushort Fig = 65;
    private const ushort Sunflowers = 103;   // a flowerpot flower: no crop row, no wilt

    private static readonly EstateKey Room = new(342, 1, 1);

    private readonly LedgerStore store = new();
    private readonly CensusEngine census;

    public SeenFineFloorTests() => census = new CensusEngine(store);

    /// <summary>One live read of the room's pot at map key <paramref name="key"/>.</summary>
    private void Read(int key, DateTimeOffset at, byte stage, byte wilt, ushort species = Krakka, bool occupied = true)
        => census.OnMapSighting(Room, key, [new BedReading(0, species, stage, 0, occupied) { Wilt = wilt }], at,
            isPot: true, mayRecord: true);

    private ClaimedBed PotAt(int key) => store.Beds.Single(b => b.IsPot && b.MapKey == key);

    private void Act(int key, ReceiptVerb verb, DateTimeOffset at, ushort species = Krakka, byte stage = 1)
        => census.OnReceipt(new ReceiptEvent(Room, key, 0, verb, species, stage, at, IsPot: true));

    private VisitCard Card(DateTimeOffset? now = null, Standing? standing = null) => Assert.Single(GardenBoard.Cards(
        [new EstateRecord { Key = Room, Nickname = "Room" }], store.Beds,
        new AccessRoster([new RosterEstate(Room, "test")]), T, now ?? Now, standing));

    private static ContainerCell Cell(VisitCard card, int key) => card.Patches.Single(p => p.IsPots).Cells.Single(c => c.Position == key);

    /// <summary>The 2026-11-10 morning: sown three days ago, the last recorded watering two days
    /// ago (dying by its clock); the real watering this morning went unrecorded.</summary>
    private void SownAndWateredLongAgo(int key = 1) => Sown(key, Now.AddDays(-2));

    /// <summary>A pot seen in its room three days ago and sown there, then watered at
    /// <paramref name="watered"/> (a pot's record is born from a room read; the sowing's reset
    /// takes that read's floor with it).</summary>
    private void Sown(int key, DateTimeOffset watered)
    {
        Read(key, Now.AddDays(-3).AddMinutes(-1), stage: 0, wilt: 0);
        Act(key, ReceiptVerb.Plant, Now.AddDays(-3), stage: 0);
        Act(key, ReceiptVerb.PotWater, watered);
    }

    [Fact] // the case the slice exists for: walk in, see it fine, walk out
    public void APotSeenFineAwayFromTheRoomReadsWaterSoonNotWilting()
    {
        SownAndWateredLongAgo();
        Assert.Equal(ContainerLook.Dying, Cell(Card(), 1).Look);   // before: the recorded clock alone

        Read(1, Seen, stage: 2, wilt: 0);
        var card = Card();   // walked out: no live read
        var cell = Cell(card, 1);

        Assert.Equal(ContainerLook.WaterSoon, cell.Look);
        Assert.Equal(Seen, cell.SeenFineAt);        // the "?": the clock rests on the floor alone
        Assert.Equal(Seen, cell.WaterBy);           // the floor plus the wilt hours: no earlier than T
        Assert.Equal(Seen.AddHours(24), cell.DiesAt);
        Assert.Equal(Seen, card.WaterBy);
        Assert.Equal(ContainerLook.WaterSoon, card.Thirst);
        Assert.Equal(new SeenFineDeadline(Seen, MayBeDying: false), card.DeadlineSeenFine);
        Assert.Equal(0, card.PotsWilting);
    }

    [Fact] // the floor says when it could first be dying: halfway from its wilt hours to withering
    public void APotOnTheFloorTurnsDyingAtTheFloorsDyingPoint()
    {
        // Sown two days ago and never watered on the record, so it is still a day from ripe when
        // the floor reaches its dying point (ripe by the clock would stand its water down, ruling 24).
        Read(1, Now.AddDays(-2).AddMinutes(-1), stage: 0, wilt: 0);
        Act(1, ReceiptVerb.Plant, Now.AddDays(-2), stage: 0);
        Read(1, Seen, stage: 2, wilt: 0);

        Assert.Equal(ContainerLook.WaterSoon, Cell(Card(Seen.AddHours(12).AddMinutes(-1)), 1).Look);
        var dying = Card(Seen.AddHours(12));
        Assert.Equal(ContainerLook.Dying, Cell(dying, 1).Look);
        Assert.Equal(new SeenFineDeadline(Seen, MayBeDying: true), dying.DeadlineSeenFine);   // the headline follows the cell
    }

    [Fact] // no watering or sowing recorded at all: the floor alone is a clock
    public void AFloorWithNothingRecordedUnderItIsAClock()
    {
        Read(1, Seen, stage: 2, wilt: 0);

        var cell = Cell(Card(), 1);

        Assert.Equal(ContainerLook.WaterSoon, cell.Look);
        Assert.Equal(Seen, cell.WaterBy);
        Assert.Equal(Seen, cell.SeenFineAt);
    }

    [Fact] // a recorded watering after the fine read is a receipt: it wins, no "?"
    public void ARecordedWateringNewerThanTheFloorWins()
    {
        SownAndWateredLongAgo();
        Read(1, Seen, stage: 2, wilt: 0);
        Seed.Tend(PotAt(1), Seen.AddMinutes(5), Krakka, by: Seed.Household);

        var card = Card();
        var cell = Cell(card, 1);

        Assert.Equal(ContainerLook.Growing, cell.Look);
        Assert.Null(cell.SeenFineAt);
        Assert.Equal(Seen.AddMinutes(5).AddHours(24), cell.WaterBy);
        Assert.Null(card.DeadlineSeenFine);
    }

    [Fact] // watered two hours before the look: the floor (a day before it) is older and changes nothing
    public void AFloorOlderThanTheRecordedStartChangesNothing()
    {
        Sown(1, Seen.AddHours(-2));
        var before = Cell(Card(), 1);

        Read(1, Seen, stage: 2, wilt: 0);
        var after = Cell(Card(), 1);

        Assert.Equal(before.WaterBy, after.WaterBy);
        Assert.Equal(before.DiesAt, after.DiesAt);
        Assert.Equal(before.Look, after.Look);
        Assert.Null(after.SeenFineAt);
    }

    [Fact] // a wilting read is the kept sighting as before, and sets no floor
    public void AWiltingReadSetsTheSightingAndNoFloor()
    {
        SownAndWateredLongAgo();
        Read(1, Seen, stage: 2, wilt: 1);

        var cell = Cell(Card(), 1);

        Assert.Null(PotAt(1).SeenFineAt);
        Assert.Equal(Seen, PotAt(1).WiltSeenAt);
        Assert.True(cell.WiltShown);
        Assert.Null(cell.SeenFineAt);
    }

    [Fact] // seen fine, then seen wilting: the game's own wilt wins purple
    public void TheGamesOwnWiltBeatsTheFloor()
    {
        Read(1, Seen.AddHours(-3), stage: 2, wilt: 0);
        Read(1, Seen, stage: 2, wilt: 1);

        var cell = Cell(Card(), 1);

        Assert.Equal(ContainerLook.Wilting, cell.Look);
        Assert.True(cell.WiltShown);
    }

    [Fact] // the pot wilt rule: a ripe pot makes no water claim either way
    public void ARipeReadSetsNoFloor()
    {
        Read(1, Seen, stage: 4, wilt: 0);

        var cell = Cell(Card(), 1);

        Assert.Null(PotAt(1).SeenFineAt);
        Assert.Equal(ContainerLook.Ripe, cell.Look);
        Assert.Null(cell.WaterBy);
    }

    [Fact] // a dead plant is past wilting: no floor
    public void ADeadReadSetsNoFloor()
    {
        Read(1, Seen, stage: 5, wilt: 2);

        Assert.Null(PotAt(1).SeenFineAt);
        Assert.Equal(ContainerLook.Dead, Cell(Card(), 1).Look);
    }

    [Fact] // a flowerpot flower never wilts, so not wilting proves nothing: no clock, no "?"
    public void AFlowerMakesNoClaim()
    {
        Read(1, Seen, stage: 2, wilt: 0, species: Sunflowers);

        var cell = Cell(Card(), 1);

        Assert.Null(cell.WaterBy);
        Assert.Null(cell.SeenFineAt);
        Assert.Equal(ContainerLook.Growing, cell.Look);
    }

    [Fact] // the floor was about the plant that left
    public void AHarvestClearsTheFloor()
    {
        Read(1, Seen.AddMinutes(-5), stage: 3, wilt: 0);
        Act(1, ReceiptVerb.Harvest, Seen);

        Assert.Null(PotAt(1).SeenFineAt);
    }

    [Fact] // a sowing starts a new planting: the old plant's floor does not carry over
    public void ASowingClearsTheFloor()
    {
        Read(1, Seen.AddMinutes(-5), stage: 2, wilt: 0);
        Act(1, ReceiptVerb.Plant, Seen, Fig, stage: 0);

        Assert.Null(PotAt(1).SeenFineAt);
    }

    [Fact] // harvested while nobody watched: the room reads the pot empty
    public void AnEmptyReadClearsTheFloor()
    {
        Read(1, Seen.AddMinutes(-5), stage: 2, wilt: 0);
        Read(1, Seen, stage: 0, wilt: 0, species: 0, occupied: false);

        Assert.Null(PotAt(1).SeenFineAt);
    }

    [Fact] // in the room the live read already shows it fine; the floor reads the same as away, so walking out changes nothing
    public void InTheRoomAPotOnTheFloorReadsAsItDoesAway()
    {
        SownAndWateredLongAgo();
        Read(1, Seen, stage: 2, wilt: 0);
        var live = new Dictionary<int, PotReading>
        {
            [1] = new(Krakka, Stage: 2, Color: 0, Extra: 0, Occupied: true, Recognized: true, Wilt: 0),
        };

        var card = Card(standing: new Standing(Room, Inside: true, live));

        Assert.Equal(ContainerLook.WaterSoon, Cell(card, 1).Look);
        Assert.Equal(Seen, Cell(card, 1).SeenFineAt);
        Assert.Equal(new SeenFineDeadline(Seen, MayBeDying: false), card.DeadlineSeenFine);
    }

    [Fact] // the lane's water bar is one real clock: on the floor it starts at the look, not a day before it
    public void TheWaterBarOnTheFloorStartsAtTheLook()
    {
        SownAndWateredLongAgo();
        Read(1, Seen, stage: 2, wilt: 0);

        var lane = Card().Lane;

        Assert.Equal(Seen, lane.WaterFrom);
        Assert.True(lane.SeenFine);
        Assert.False(lane.FromSowing);
    }

    [Fact] // the headline speaks from the floor only when the floor sets the deadline
    public void TheHeadlineTakesTheFloorOnlyWhenTheFloorSetsTheDeadline()
    {
        Read(1, Seen, stage: 2, wilt: 0);
        Sown(2, Seen.AddMinutes(-30));   // recorded, fine for a day

        var floorFirst = Card();
        Assert.Equal(Seen, floorFirst.WaterBy);
        Assert.Equal(Seen, floorFirst.DeadlineSeenFine?.SeenAt);
        Assert.Equal(1, floorFirst.FirstToWilt.Pots);

        Sown(3, Now.AddDays(-2));        // recorded, never seen fine since: its deadline came first

        var recordedFirst = Card();
        Assert.Equal(Now.AddDays(-1), recordedFirst.WaterBy);
        Assert.Null(recordedFirst.DeadlineSeenFine);
        Assert.False(recordedFirst.Lane.SeenFine);                 // the bar and the headline agree
        Assert.Equal(Now.AddDays(-2), recordedFirst.Lane.WaterFrom);
    }
}
