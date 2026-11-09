using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>Rethink slice 19, at the garden board's seam: a pot the game showed wilting keeps its
/// wilt on its card after the player leaves the room (spec ruling 4, "pot wilting (seen 2h ago)"),
/// until the plugin knows the pot is no longer thirsty. The garden goes in the way the plugin
/// learns it - the census's map reads and receipts, a relayed watering - and the cards come out.</summary>
public class PotWiltSightingTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T19:00:00Z");
    private static readonly DateTimeOffset Seen = Now.AddMinutes(-12);

    private const ushort Krakka = 49;   // Data/SpeciesIndex.json
    private const ushort Fig = 65;

    private static readonly EstateKey Room = new(342, 1, 1);

    private readonly LedgerStore store = new();
    private readonly CensusEngine census;

    public PotWiltSightingTests() => census = new CensusEngine(store);

    /// <summary>One live read of the room's pot at map key <paramref name="key"/>.</summary>
    private void Read(int key, DateTimeOffset at, byte stage, byte wilt, ushort species = Krakka, bool occupied = true)
        => census.OnMapSighting(Room, key, [new BedReading(0, species, stage, 0, occupied, wilt)], at,
            isPot: true, mayRecord: true);

    private ClaimedBed PotAt(int key) => store.Beds.Single(b => b.IsPot && b.MapKey == key);

    /// <summary>An own act on the pot at <paramref name="key"/> (a pot's ordinal is its map key).</summary>
    private void Act(int key, ReceiptVerb verb, DateTimeOffset at, ushort species = Krakka, byte stage = 1)
        => census.OnReceipt(new ReceiptEvent(Room, key, 0, verb, species, stage, at, IsPot: true));

    private VisitCard Card(Standing? standing = null) => Assert.Single(GardenBoard.Cards(
        [new EstateRecord { Key = Room, Nickname = "Room" }], store.Beds,
        new AccessRoster([new RosterEstate(Room, "test")]), T, Now, standing));

    private static ContainerCell Cell(VisitCard card, int key) => card.Patches.Single(p => p.IsPots).Cells.Single(c => c.Position == key);

    [Fact]
    public void APotSeenWiltingAndLeftReadsWiltingWithItsSightingTime()
    {
        Read(1, Seen.AddHours(-1), stage: 1, wilt: 0);
        Read(1, Seen, stage: 1, wilt: 1);

        var card = Card();   // walked out: no live read

        Assert.Equal(1, card.PotsWilting);
        Assert.Equal(Seen, card.PotsWiltingSeenAt);
        Assert.Equal(ContainerLook.Wilting, Cell(card, 1).Look);
        Assert.Equal(Seen, Cell(card, 1).WiltSeenAt);
    }

    [Fact] // the Water All at FC Estate (2026-11-08 18:50): our own watering clears it
    public void AnOwnWateringAfterTheSightingClearsIt()
    {
        Read(1, Seen, stage: 1, wilt: 1);
        Act(1, ReceiptVerb.PotWater, Seen.AddMinutes(2));

        var card = Card();

        Assert.Equal(0, card.PotsWilting);
        Assert.Null(card.PotsWiltingSeenAt);
        Assert.Equal(ContainerLook.Growing, Cell(card, 1).Look);
    }

    [Fact] // the other install watered it: the relayed watering lands through the same door
    public void ARelayedWateringAfterTheSightingClearsIt()
    {
        Read(1, Seen, stage: 1, wilt: 1);
        Seed.Tend(PotAt(1), Seen.AddMinutes(5), Krakka, by: Seed.Household);

        Assert.Equal(0, Card().PotsWilting);
    }

    [Fact] // a watering the relay delivers late, from before the game showed the wilt, is not news
    public void AWateringOlderThanTheSightingDoesNotClearIt()
    {
        Read(1, Seen, stage: 1, wilt: 1);
        Seed.Tend(PotAt(1), Seen.AddHours(-3), Krakka, by: Seed.Household);

        var card = Card();

        Assert.Equal(1, card.PotsWilting);
        Assert.Equal(Seen, card.PotsWiltingSeenAt);
    }

    [Fact] // a later look at the room that shows the pot fine (watered by a hand we never heard)
    public void ALaterReadShowingItNotWiltingClearsIt()
    {
        Read(1, Seen, stage: 1, wilt: 1);
        Read(1, Seen.AddMinutes(5), stage: 1, wilt: 0);

        Assert.Equal(0, Card().PotsWilting);
    }

    [Fact] // the pot wilt rule: a ripe pot's flag is no water claim, so a ripe read answers the sighting too
    public void ARipeReadClearsItAndNeverStampsOne()
    {
        Read(1, Seen, stage: 3, wilt: 1);
        Read(1, Seen.AddMinutes(5), stage: 4, wilt: 1);

        var card = Card();

        Assert.Equal(0, card.PotsWilting);
        Assert.Equal(ContainerLook.Ripe, Cell(card, 1).Look);
        Assert.Null(PotAt(1).WiltSeenAt);
    }

    [Fact] // the sighting was about the plant that left
    public void AHarvestClearsIt()
    {
        Read(1, Seen, stage: 3, wilt: 1);
        Act(1, ReceiptVerb.Harvest, Seen.AddMinutes(1));

        Assert.Null(PotAt(1).WiltSeenAt);
    }

    [Fact] // a sowing starts a new planting: the old plant's wilt does not carry over
    public void ANewPlantingClearsIt()
    {
        Read(1, Seen, stage: 1, wilt: 1);
        Act(1, ReceiptVerb.Plant, Seen.AddMinutes(1), Fig, stage: 0);

        var card = Card();

        Assert.Equal(0, card.PotsWilting);
        Assert.Null(PotAt(1).WiltSeenAt);
    }

    [Fact] // in the room the live read wins: the map in front of the player is now, the sighting is then
    public void InTheRoomTheLiveReadWinsOverTheSighting()
    {
        Read(1, Seen, stage: 1, wilt: 1);
        Read(2, Seen, stage: 1, wilt: 0);
        var live = new Dictionary<int, PotReading>
        {
            [1] = new(Krakka, Stage: 1, Color: 0, Extra: 0, Occupied: true, Recognized: true, Wilt: 0),
            [2] = new(Krakka, Stage: 1, Color: 0, Extra: 0, Occupied: true, Recognized: true, Wilt: 1),
        };

        var card = Card(new Standing(Room, Inside: true, live));

        Assert.Equal(1, card.PotsWilting);
        Assert.Null(card.PotsWiltingSeenAt);   // the live read: the note reads as today
        Assert.Equal(ContainerLook.Growing, Cell(card, 1).Look);
        Assert.Equal(ContainerLook.Wilting, Cell(card, 2).Look);
        Assert.Null(Cell(card, 2).WiltSeenAt);
    }

    [Fact] // the house's pots share the yard's key: standing outside, the card speaks from the sighting
    public void OutsideTheHouseTheSightingSpeaks()
    {
        Read(1, Seen, stage: 1, wilt: 1);
        var staleLive = new Dictionary<int, PotReading>
        {
            [1] = new(Krakka, Stage: 1, Color: 0, Extra: 0, Occupied: true, Recognized: true, Wilt: 0),
        };

        var card = Card(new Standing(Room, Inside: false, staleLive));

        Assert.Equal(1, card.PotsWilting);
        Assert.Equal(Seen, card.PotsWiltingSeenAt);
    }

    [Fact] // several pots seen on different visits: the note's age is the last time the game showed one wilting
    public void TheNoteCarriesTheNewestSighting()
    {
        Read(1, Seen.AddHours(-3), stage: 1, wilt: 1);
        Read(2, Seen, stage: 2, wilt: 1);

        var card = Card();

        Assert.Equal(2, card.PotsWilting);
        Assert.Equal(Seen, card.PotsWiltingSeenAt);
        Assert.Equal(Seen.AddHours(-3), Cell(card, 1).WiltSeenAt);
    }

    [Fact] // harvested while nobody watched: the pot's entry is gone from two settled reads
    public void APotReadAbsentClearsIt()
    {
        Read(1, Seen, stage: 2, wilt: 1);
        census.ReconcileAbsentPots(Room, presentKeys: [], Seen.AddMinutes(5));

        Assert.Null(PotAt(1).WiltSeenAt);
    }

    [Fact] // harvested while nobody watched: the room reads the pot empty
    public void AnEmptyReadClearsIt()
    {
        Read(1, Seen, stage: 2, wilt: 1);
        Read(1, Seen.AddMinutes(5), stage: 0, wilt: 0, species: 0, occupied: false);

        Assert.Null(PotAt(1).WiltSeenAt);
    }
}
