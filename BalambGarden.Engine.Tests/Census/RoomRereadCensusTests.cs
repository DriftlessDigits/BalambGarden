using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

/// <summary>Rethink slice 25 at the census and the board: a re-read is an ordinary map read, so what
/// it finds lands the way the arrival read's would, and the Garden tab shows it. The bytes are the
/// game's own (Recon capture 2026-11-11 19:27:58, `Bench out/pot131-capture-1927.txt`); the stale
/// stage-3 entry is BUILT from the ledger's stage-3 sighting of pot 131 (no raw bytes exist for the
/// 19:24:59 read): the same entry with stage 3 in byte 2, and byte 4 as the ripe read had it.</summary>
public class RoomRereadCensusTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private const ushort Onion = 50;    // Data/SpeciesIndex.json, 0x32
    private const ushort Krakka = 49;   // 0x31

    private static readonly EstateKey House = new(340, 11, 57);   // the FC Estate's house (yard and rooms share it)
    private static readonly EstateKey FcRoom = new(340, 11, 57, 7);

    private static readonly DateTimeOffset Arrival = DateTimeOffset.Parse("2026-11-12T01:24:59Z");   // 19:24:59 local
    private static readonly DateTimeOffset Reread = Arrival.AddSeconds(15);

    /// <summary>Pot 131 on the game's map at 19:27:58, verbatim.</summary>
    private const string Pot131Ripe =
        "32 00 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 "
        + "00 00 00 00 00 7F 00 00 00 00 00 00 00 00 00 00 00 F4 00 00 00 00 00 00";

    /// <summary>The same entry one stage behind (built: byte 2 = 03).</summary>
    private static readonly string Pot131Stale = "32 00 03" + Pot131Ripe[8..];

    /// <summary>A dead Krakka pot: the dead onion pots' entry of 2026-11-10 18:50:42 (`32 00 05 00 02`) with
    /// Krakka's species (built), and a cleared pot's entry: every byte zero (the map zeroes in place).</summary>
    private static readonly string KrakkaDead = "31 00 05 00 02" + Pot131Ripe[14..];
    private static readonly string Zeroed = string.Join(" ", Enumerable.Repeat("00", 48));

    private static PotReading Decode(string hex)
        => MapFormat.DecodeIndoorEntry(Convert.FromHexString(hex.Replace(" ", "")), _ => true)!;

    private readonly LedgerStore store = new();
    private readonly CensusEngine census;

    public RoomRereadCensusTests() => census = new CensusEngine(store);

    /// <summary>One census read of a room, the way the pump's read passes each pot in.</summary>
    private int Read(EstateKey estate, DateTimeOffset at, IReadOnlyDictionary<int, PotReading> room,
        List<SightingChange>? changes = null, List<RebaseEvent>? rebases = null)
        => room.Sum(kv => census.OnMapSighting(estate, kv.Key,
            [new BedReading(0, kv.Value.SpeciesIndex, kv.Value.Stage, kv.Value.Extra, kv.Value.Occupied) { Wilt = kv.Value.Wilt }],
            at, isPot: true, mayRecord: true, changes: changes, rebases: rebases));

    private ClaimedBed Pot(EstateKey estate, int key) => store.Beds.Single(b => b.Estate == estate && b.IsPot && b.MapKey == key);

    private VisitCard Card(EstateKey estate, DateTimeOffset now, IReadOnlyDictionary<int, PotReading> live)
        => Assert.Single(GardenBoard.Cards([new EstateRecord { Key = estate, Nickname = "FC Estate" }], store.Beds,
            new AccessRoster([new RosterEstate(estate, "test")]), T, now, new Standing(estate, Inside: true, live)));

    private static ContainerCell Cell(VisitCard card, int key) => card.Patches.Single(p => p.IsPots).Cells.Single(c => c.Position == key);

    /// <summary>Pot 131 as the ledger held it walking in: sown 2026-11-01 23:57:05Z by a household hand
    /// (onion 240h: ripe 2026-11-11 23:57Z), watered 2026-11-11 01:43:20Z, stage 3 seen since 11-08.</summary>
    private void Pot131AsRecorded()
    {
        var pot = new ClaimedBed { Estate = House, MapKey = 131, PatchOrdinal = 131, BedSlot = 0, IsPot = true };
        store.Beds.Add(pot);
        census.Bind(House, 131, 131, isPot: true);
        pot.Plant(DateTimeOffset.Parse("2026-11-01T23:57:05Z"), Onion, 0, new Hand("hhbbaa"), out _);
        pot.Sight(DateTimeOffset.Parse("2026-11-08T00:19:18Z"), Onion, 3, ObservationSource.MapSighting);
        pot.Tend(DateTimeOffset.Parse("2026-11-11T01:43:20Z"), Hand.Own, Onion);
    }

    [Fact]
    public void TheRealBytesDecodeAsRipeAndTheBuiltStaleEntryAsStage3()
    {
        Assert.Equal((Onion, (byte)4, (byte)0), (Decode(Pot131Ripe).SpeciesIndex, Decode(Pot131Ripe).Stage, Decode(Pot131Ripe).Wilt));
        Assert.Equal((Onion, (byte)3), (Decode(Pot131Stale).SpeciesIndex, Decode(Pot131Stale).Stage));
    }

    [Fact]
    public void AReReadAfterAStaleArrivalReadRecordsPot131RipeAndTheCardShowsIt()
    {
        Pot131AsRecorded();
        var stale = new Dictionary<int, PotReading> { [131] = Decode(Pot131Stale) };
        Read(House, Arrival, stale);
        var before = Card(House, Arrival, stale);

        var ripe = new Dictionary<int, PotReading> { [131] = Decode(Pot131Ripe) };
        var changes = new List<SightingChange>();
        Read(House, Reread, ripe, changes);
        var after = Card(House, Reread, ripe);

        // Walking in: the stale read stands, the pot is not ripe (the bug the player saw: "the mod just shows 2").
        Assert.NotEqual(ContainerLook.Ripe, Cell(before, 131).Look);
        Assert.Equal(0, before.RipeNow);
        // The re-read: the record's newest read is ripe, the household hears it, the card shows it.
        Assert.Equal(new Observation(Reread, Onion, 4, ObservationSource.MapSighting), Pot(House, 131).Latest);
        Assert.Equal([new SightingChange(131, 0, SyncKind.Ripe, Onion, 4)], changes);
        Assert.Equal(ContainerLook.Ripe, Cell(after, 131).Look);
        Assert.Equal(1, after.RipeNow);
        Assert.Equal(1, after.Harvest);
    }

    private static readonly DateTimeOffset SeenDead = DateTimeOffset.Parse("2026-11-12T01:32:00Z");   // 19:32:00 local, the arrival read
    private static readonly DateTimeOffset Disposed = DateTimeOffset.Parse("2026-11-12T01:33:10Z");

    /// <summary>The FC Room's two Krakka pots as the 19:32:00 arrival read left them: sown 2026-11-07, dead.</summary>
    private Dictionary<int, PotReading> TwoDeadPots()
    {
        foreach (var key in new[] { 0, 1 })
        {
            var pot = new ClaimedBed { Estate = FcRoom, MapKey = key, PatchOrdinal = key, BedSlot = 0, IsPot = true };
            store.Beds.Add(pot);
            census.Bind(FcRoom, key, key, isPot: true);
            pot.Plant(DateTimeOffset.Parse("2026-11-07T16:46:40Z").AddSeconds(key * 39), Krakka, 0, Hand.Own, out _);
        }
        var dead = new Dictionary<int, PotReading> { [0] = Decode(KrakkaDead), [1] = Decode(KrakkaDead) };
        Read(FcRoom, SeenDead, dead);
        return dead;
    }

    [Fact]
    public void AReReadAfterAHandDisposalEmptiesTheDeadPotAndTheCardCountsItToReplant()
    {
        var dead = TwoDeadPots();
        Assert.Equal(2, Card(FcRoom, SeenDead, dead).Dead.Total);

        var oneCleared = new Dictionary<int, PotReading> { [0] = Decode(Zeroed), [1] = Decode(KrakkaDead) };
        var rebases = new List<RebaseEvent>();
        Read(FcRoom, Disposed.AddSeconds(2), oneCleared, rebases: rebases);
        var card = Card(FcRoom, Disposed.AddSeconds(2), oneCleared);

        var reset = Assert.Single(rebases);
        Assert.Equal(RebaseReason.ReadsEmpty, reset.Reason);
        Assert.Null(Pot(FcRoom, 0).Latest);
        Assert.Equal(ContainerLook.Empty, Cell(card, 0).Look);
        Assert.Equal(ContainerLook.Dead, Cell(card, 1).Look);
        Assert.Equal(2, card.Replant);            // the emptied and the dead
        Assert.Equal(1, card.Dead.Total);
    }

    [Fact] // the 19:32 scene: both dead pots disposed of by hand, the card shown until he left and came back
    public void AReReadAfterBothDisposalsEmptiesBothAndTheRoomLeavesTheGardenTab()
    {
        TwoDeadPots();

        var cleared = new Dictionary<int, PotReading> { [0] = Decode(Zeroed), [1] = Decode(Zeroed) };
        var rebases = new List<RebaseEvent>();
        Read(FcRoom, Disposed.AddSeconds(2), cleared, rebases: rebases);
        var cards = GardenBoard.Cards([new EstateRecord { Key = FcRoom, Nickname = "FC Room" }], store.Beds,
            new AccessRoster([new RosterEstate(FcRoom, "test")]), T, Disposed.AddSeconds(2),
            new Standing(FcRoom, Inside: true, cleared));

        Assert.Equal([RebaseReason.ReadsEmpty, RebaseReason.ReadsEmpty], rebases.Select(r => r.Reason));
        Assert.Empty(cards);                      // nothing planted there now: no card (spec ruling 5)
    }

    [Fact]
    public void AReReadSaysWhatChangedOnThePots()
    {
        // Keys go in highest first: the lines must come out by key, lowest first.
        var before = new Dictionary<int, PotReading>
        {
            [392] = Decode("32 00 04 00 01" + Pot131Ripe[14..]),
            [131] = Decode(Pot131Stale),
            [7] = Decode(Pot131Ripe),
            [0] = Decode(KrakkaDead),
        };
        var after = new Dictionary<int, PotReading>
        {
            [392] = Decode("32 00 04 00 00" + Pot131Ripe[14..]),
            [131] = Decode(Pot131Ripe),
            [5] = Decode("32 00 01 00 00" + Pot131Ripe[14..]),
            [0] = Decode(Zeroed),
        };

        Assert.Equal(
            ["pot (key 0): emptied", "pot (key 5): now species 50 at stage 1", "pot (key 7): gone from the map",
             "pot (key 131): stage 3 -> 4", "pot (key 392): wilt byte 1 -> 0"],
            RoomReread.Changes(before, after));
    }

    [Fact]
    public void AReReadThatFindsTheSameRoomSaysNothing()
    {
        var room = new Dictionary<int, PotReading> { [131] = Decode(Pot131Ripe), [0] = Decode(Zeroed) };
        var same = new Dictionary<int, PotReading> { [131] = Decode(Pot131Ripe), [0] = Decode(Zeroed) };

        Assert.Empty(RoomReread.Changes(room, same));
    }

    [Fact]
    public void AReReadSaysWhatChangedInTheBeds()
    {
        IReadOnlyList<BedReading> Patch(byte stage3, bool occupied7)
            => Enumerable.Range(0, 8).Select(slot => slot switch
            {
                2 => new BedReading(2, Krakka, stage3, 0, true),
                6 => new BedReading(6, (ushort)(occupied7 ? Onion : 0), (byte)(occupied7 ? 2 : 0), 0, occupied7),
                _ => new BedReading(slot, 0, 0, 0, false),
            }).ToList();
        var before = new Dictionary<int, IReadOnlyList<BedReading>> { [1313] = Patch(2, occupied7: true), [1293] = Patch(1, occupied7: false) };
        var after = new Dictionary<int, IReadOnlyList<BedReading>> { [1313] = Patch(3, occupied7: false) };

        Assert.Equal(["map key 1293: gone from the map", "map key 1313 bed 3: stage 2 -> 3", "map key 1313 bed 7: emptied"],
            RoomReread.Changes(before, after));
    }
}
