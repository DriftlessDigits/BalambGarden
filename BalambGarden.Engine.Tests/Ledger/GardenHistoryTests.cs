using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests.Ledger;

/// <summary>The permanent plant/harvest history (ruled 2026-10-14): append-only, both hands,
/// yields kept, dedup by id, born empty, its own file.</summary>
public class GardenHistoryTests
{
    private static readonly EstateKey Garden = new(340, 11, 32);
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 6, 0, 0, TimeSpan.Zero);
    private const string OwnUser = "k7f3qx";
    private const ushort Onion = 52, Fig = 51;

    private static LoggedReceipt Own(long seq, SyncKind kind, DateTimeOffset at, ushort? species = Onion,
        bool isPot = false, IReadOnlyList<SyncYield>? yields = null)
        => new(seq, "eraaaa", kind, SyncSource.Chain, at, Garden, isPot,
            PatchOrdinal: 2, BedSlot: 5, PatchId: 1234, PotKey: isPot ? 129 : -1,
            Data: new SyncData(Species: species, Yields: yields));

    private static HistoryEntry Row(string id, HistoryKind kind, DateTimeOffset at, string by, ushort species = Onion, int qty = 0)
        => new(id, at, kind, species, Garden, false, 2, 5, by, SyncSource.Chain,
            qty == 0 ? null : [new SyncYield(4789, qty)]);

    [Fact]
    public void Record_appends_once_per_id_and_marks_dirty()
    {
        var history = new GardenHistory();
        Assert.False(history.Dirty);
        Assert.True(history.Record(Row("a:b:1", HistoryKind.Plant, T0, "own")));
        Assert.False(history.Record(Row("a:b:1", HistoryKind.Plant, T0, "own")));
        Assert.Single(history.Entries);
        Assert.True(history.Dirty);
        history.MarkSaved();
        Assert.False(history.Dirty);
        Assert.False(history.Record(null));
    }

    [Fact] // own rows carry the install's real wire slug in the id and "own" as the hand
    public void Own_entries_map_from_the_receipt_log()
    {
        var plant = HistoryEntry.FromOwn(Own(7, SyncKind.Plant, T0), OwnUser)!;
        Assert.Equal($"{OwnUser}:eraaaa:7", plant.Id);
        Assert.Equal(Hand.OwnSlug, plant.By);
        Assert.True(plant.IsOwn);
        Assert.Equal(HistoryKind.Plant, plant.Kind);
        Assert.Equal(Onion, plant.Species);
        Assert.Equal(2, plant.PatchOrdinal);
        Assert.Equal(5, plant.BedSlot);

        var harvest = HistoryEntry.FromOwn(Own(8, SyncKind.Harvest, T0.AddDays(5), yields: [new SyncYield(4789, 9)]), OwnUser)!;
        Assert.Equal(HistoryKind.Harvest, harvest.Kind);
        Assert.Equal(9, harvest.YieldQty);

        Assert.Null(HistoryEntry.FromOwn(Own(9, SyncKind.Tend, T0), OwnUser));
        Assert.Null(HistoryEntry.FromOwn(Own(10, SyncKind.Fertilize, T0), OwnUser));
    }

    [Fact] // a pot's identity is its key as the ordinal, slot 0 - the ledger row's convention
    public void Own_pot_entries_use_the_pot_key_as_ordinal()
    {
        var row = HistoryEntry.FromOwn(Own(1, SyncKind.Harvest, T0, isPot: true), OwnUser)!;
        Assert.True(row.IsPot);
        Assert.Equal(129, row.PatchOrdinal);
        Assert.Equal(0, row.BedSlot);
    }

    [Fact]
    public void Foreign_entries_take_the_row_identity_and_the_hand()
    {
        var bed = new ClaimedBed { Estate = Garden, MapKey = 110, PatchOrdinal = 1, BedSlot = 4, IsPot = false };
        var receipt = new SyncReceipt("hhbbaa:erabbb:3", "entity-a", SyncEntityType.Bed, SyncKind.Harvest,
            T0, SyncSource.Chat, new SyncData(Species: Fig, Yields: [new SyncYield(100, 2)]));

        var row = HistoryEntry.FromForeign(receipt, bed, new Hand("hhbbaa"))!;

        Assert.Equal("hhbbaa:erabbb:3", row.Id);
        Assert.Equal("hhbbaa", row.By);
        Assert.False(row.IsOwn);
        Assert.Equal(1, row.PatchOrdinal);
        Assert.Equal(4, row.BedSlot);
        Assert.Equal(SyncSource.Chat, row.Source);
        Assert.Equal(2, row.YieldQty);
        Assert.Null(HistoryEntry.FromForeign(receipt with { Kind = SyncKind.Tend }, bed, new Hand("hhbbaa")));
    }

    [Fact] // RUN, not reasoned: estate keys, enums by name, yields and nulls all survive the file
    public void History_round_trips_through_json()
    {
        var history = new GardenHistory();
        history.Record(Row("own:e:1", HistoryKind.Plant, T0, "own"));
        history.Record(Row("hhbbaa:e:1", HistoryKind.Harvest, T0.AddDays(5), "hhbbaa", qty: 9));

        var json = history.ToJson();
        Assert.Contains("\"Kind\": \"Harvest\"", json);
        Assert.DoesNotContain("Dirty", json);

        var back = GardenHistory.FromJson(json);
        Assert.Equal(2, back.Entries.Count);
        Assert.Equal(Garden, back.Entries[1].Estate);
        Assert.Equal(9, back.Entries[1].YieldQty);
        Assert.Null(back.Entries[0].Yields);
        Assert.False(back.Dirty);
        Assert.False(back.Record(Row("own:e:1", HistoryKind.Plant, T0, "own")));   // ids rebuilt from the file
    }

    [Fact] // the onion question: harvests of one species, newest first, with totals and hands
    public void Query_answers_newest_first_by_species_kind_and_hand()
    {
        var history = new GardenHistory();
        history.Record(Row("a:e:1", HistoryKind.Plant, T0, "own"));
        history.Record(Row("a:e:2", HistoryKind.Harvest, T0.AddDays(5), "own", qty: 3));
        history.Record(Row("b:e:1", HistoryKind.Harvest, T0.AddDays(9), "hhbbaa", qty: 4));
        history.Record(Row("b:e:2", HistoryKind.Harvest, T0.AddDays(7), "hhbbaa", species: Fig, qty: 1));

        var onions = HistoryQuery.Harvests(history, Onion);
        Assert.Equal(["b:e:1", "a:e:2"], onions.Select(r => r.Id).ToArray());
        Assert.Equal(7, HistoryQuery.YieldTotal(onions));
        Assert.Equal(("hhbbaa", 1), HistoryQuery.ByHand(onions)[0]);

        Assert.Single(HistoryQuery.Find(history, new HistoryFilter(By: "own", Kind: HistoryKind.Harvest)));
        Assert.Equal(4, HistoryQuery.Find(history, new HistoryFilter(Estate: Garden)).Count);
        Assert.Empty(HistoryQuery.Find(history, new HistoryFilter(IsPot: true)));

        var bed = new ClaimedBed { Estate = Garden, MapKey = 110, PatchOrdinal = 2, BedSlot = 5 };
        Assert.Equal("b:e:1", HistoryQuery.LastAt(history, bed)!.Id);
    }
}
