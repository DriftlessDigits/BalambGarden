using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

/// <summary>Sow capture (v0.4.1, Sow Capture design, ratified 2026-11-07): every bed sow banks the
/// patch at sow - all eight beds raw, as the map read them - and every sow banks its soil. Both
/// ride the plant receipt as additive fields and land in both installs' history. Seam 1: a
/// receipt in, a history row out.</summary>
public class SowCaptureTests
{
    private static readonly EstateKey Garden = new(340, 11, 32);
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 15, 0, 0, TimeSpan.Zero);
    private const string OwnUser = "k7f3qx";
    private const string Other = "hhbbaa";          // a neutral household slug - never a name
    private const ushort Krakka = 22, MirrorApple = 31;
    private const uint Grade3Thanalan = 7766;

    /// <summary>The 10-18 shape: Krakka on the odd beds, Mirror Apple on the even, bed 3 just
    /// sown (stage 0, the map already naming it).</summary>
    private static readonly IReadOnlyList<SyncPatchBed> Patch =
    [
        new(0, true, Krakka, 2), new(1, true, MirrorApple, 2),
        new(2, true, Krakka, 0), new(3, true, MirrorApple, 2),
        new(4, true, Krakka, 2), new(5, true, MirrorApple, 2),
        new(6, true, Krakka, 2), new(7, false, 0, 0),
    ];

    private static readonly SyncData SowData = new(Species: Krakka, Soil: Grade3Thanalan, Patch: Patch);

    [Fact] // own act: the logged plant receipt's soil and patch reach the history row
    public void An_own_sow_carries_soil_and_patch_into_history()
    {
        var logged = new LoggedReceipt(7, "eraaaa", SyncKind.Plant, SyncSource.Chain, T0, Garden, false,
            PatchOrdinal: 0, BedSlot: 2, PatchId: 1234, PotKey: -1, Data: SowData);

        var row = HistoryEntry.FromOwn(logged, OwnUser)!;

        Assert.Equal(Grade3Thanalan, row.Soil);
        Assert.Equal(Patch, row.Patch);
    }

    /// <summary>Written by hand, not serialized from <see cref="SowData"/>, so the wire words are
    /// checked against the spec rather than against themselves.</summary>
    private const string HouseholdSowPage = """
        {"receipts":[
          {"id":"hhbbaa:eraaaa:41","entity":"entity-a","entityType":"bed","kind":"plant",
           "at":"2026-11-07T15:00:00+00:00","source":"chain",
           "data":{"species":22,"soil":7766,"patch":[
             {"slot":0,"occupied":true,"species":22,"stage":2},
             {"slot":1,"occupied":true,"species":31,"stage":2},
             {"slot":2,"occupied":true,"species":22,"stage":0},
             {"slot":3,"occupied":true,"species":31,"stage":2},
             {"slot":4,"occupied":true,"species":22,"stage":2},
             {"slot":5,"occupied":true,"species":31,"stage":2},
             {"slot":6,"occupied":true,"species":22,"stage":2},
             {"slot":7,"occupied":false,"species":0,"stage":0}]}}
        ],"cursor":"9"}
        """;

    private static LedgerStore LedgerWithBed()
    {
        var ledger = new LedgerStore();
        ledger.Beds.Add(new ClaimedBed
        {
            Estate = Garden, MapKey = 2, PatchOrdinal = 0, BedSlot = 2,
            FirstRecorded = T0.AddDays(-30), WireEntity = "entity-a",
        });
        return ledger;
    }

    [Fact] // household act: relay JSON through the normal inbox step lands both fields on the row
    public void A_household_sow_from_the_relay_carries_soil_and_patch_into_history()
    {
        var ledger = LedgerWithBed();
        var history = new GardenHistory();
        var page = RelayFeed.Parse(HouseholdSowPage)!;

        ForeignApply.AbsorbPage(ledger, page, OwnUser, T0.AddMinutes(1), history,
            () => { history.MarkSaved(); return true; });

        var row = Assert.Single(history.Entries);
        Assert.Equal(Other, row.By);
        Assert.Equal(Grade3Thanalan, row.Soil);
        Assert.Equal(Patch, row.Patch);
    }

    [Fact] // RUN, not reasoned: both fields survive history.json and receipts.json
    public void Soil_and_patch_survive_the_history_file_and_the_receipt_log()
    {
        var history = new GardenHistory();
        history.Record(HistoryEntry.FromOwn(new LoggedReceipt(7, "eraaaa", SyncKind.Plant, SyncSource.Chat, T0,
            Garden, false, 0, 2, 1234, -1, SowData), OwnUser));
        var row = Assert.Single(GardenHistory.FromJson(history.ToJson()).Entries);
        Assert.Equal(Grade3Thanalan, row.Soil);
        Assert.Equal(Patch, row.Patch);

        var log = new ReceiptLog();
        log.MintEra("q7f3nx");
        log.Append(SyncKind.Plant, SyncSource.Chat, T0, Garden, isPot: false,
            patchOrdinal: 0, bedSlot: 2, patchId: 1234, potKey: -1, data: SowData);
        var logged = Assert.Single(ReceiptLog.FromJson(log.ToJson()).Entries);
        Assert.Equal(Grade3Thanalan, logged.Data.Soil);
        Assert.Equal(Patch, logged.Data.Patch);
    }

    [Fact] // three states on the wire: no patch heard = no "patch" key at all, never an empty list
    public void A_sow_with_no_covering_read_sends_no_patch()
    {
        var json = new SyncReceipt($"{OwnUser}:eraaaa:1", "entity-a", SyncEntityType.Bed, SyncKind.Plant, T0,
            SyncSource.Chat, new SyncData(Species: Krakka)).ToJson();
        Assert.DoesNotContain("patch", json);
        Assert.DoesNotContain("soil", json);
    }

    [Fact] // a v0.4.0 history.json (plant row, no soil, no patch) loads with both not heard
    public void An_old_history_row_loads_with_no_soil_and_no_patch()
    {
        var json = """
            {"Entries":[{"Id":"qqkx86:t47727:2731","At":"2026-10-19T00:41:10+00:00","Kind":"Plant",
            "Species":22,"Estate":{"TerritoryId":340,"Ward":11,"Plot":57,"Room":-1,"IsApartment":false,
            "ApartmentDivision":-1,"IsIndoorOnly":false},"IsPot":false,"PatchOrdinal":0,"BedSlot":0,
            "By":"own","Source":"Chat","Yields":null}]}
            """;
        var e = Assert.Single(GardenHistory.FromJson(json).Entries);
        Assert.Null(e.Soil);
        Assert.Null(e.Patch);
    }

    [Fact] // a v0.4.0 plant receipt off the relay (species only) lands its row with both not heard
    public void An_old_household_sow_lands_with_no_soil_and_no_patch()
    {
        var ledger = LedgerWithBed();
        var history = new GardenHistory();
        var page = RelayFeed.Parse("""
            {"receipts":[{"id":"hhbbaa:eraaaa:40","entity":"entity-a","entityType":"bed","kind":"plant",
             "at":"2026-11-07T15:00:00+00:00","source":"chat","data":{"species":22}}],"cursor":"8"}
            """)!;
        ForeignApply.AbsorbPage(ledger, page, OwnUser, T0.AddMinutes(1), history,
            () => { history.MarkSaved(); return true; });
        var row = Assert.Single(history.Entries);
        Assert.Null(row.Soil);
        Assert.Null(row.Patch);
    }

    /// <summary>The older-install guard. The feed parse is unchanged since v0.4.0, so a page whose
    /// data carries fields this build does not know - here a made-up future one beside the real
    /// v0.4.1 pair - parsing whole proves a v0.4.0 install reads v0.4.1 sows the same way.</summary>
    [Fact]
    public void The_feed_parse_accepts_data_fields_it_does_not_know()
    {
        var page = RelayFeed.Parse("""
            {"receipts":[{"id":"hhbbaa:eraaaa:42","entity":"entity-a","entityType":"bed","kind":"plant",
             "at":"2026-11-07T15:00:00+00:00","source":"chain",
             "data":{"species":22,"soil":7766,"patch":[{"slot":0,"occupied":true,"species":22,"stage":2}],
                     "someFutureField":{"nested":[1,2,3]}}}],"cursor":"10"}
            """);
        Assert.NotNull(page);
        var r = Assert.Single(page!.Receipts);
        Assert.Equal((ushort)Krakka, r.Data.Species);
    }

    // ------------------------------------------------------------------ seam 2: map read in, patch at sow out

    [Fact] // eight readings in, eight raw entries out, by the map's own slot order
    public void The_patch_at_sow_is_the_eight_beds_as_read()
    {
        // Out of order on purpose: the record is by slot, whatever order the read listed.
        var read = new List<BedReading>
        {
            new(7, 0, 0, 0, false), new(0, Krakka, 2, 0x11, true), new(1, MirrorApple, 2, 0, true),
            new(2, Krakka, 0, 0, true), new(3, MirrorApple, 2, 0, true), new(4, Krakka, 2, 0, true),
            new(5, MirrorApple, 2, 0, true), new(6, Krakka, 2, 0, true),
        };

        Assert.Equal(Patch, PatchAtSow.From(read));
    }

    [Fact] // no map read covered the patch: absent, never an empty or guessed patch
    public void No_read_is_no_patch()
    {
        Assert.Null(PatchAtSow.From(null));
        Assert.Null(PatchAtSow.From([]));
    }

    [Fact] // a flower can read species 0 while occupied, a seedling stage 0 - both copied as the map gave them
    public void Flowers_and_seedlings_are_copied_raw()
    {
        var read = new List<BedReading> { new(0, 0, 3, 0, true), new(1, Krakka, 0, 0, true) };

        Assert.Equal(
            [new SyncPatchBed(0, true, 0, 3), new SyncPatchBed(1, true, Krakka, 0)],
            PatchAtSow.From(read));
    }

    // ------------------------------------------------------------------ seam 4: open receipt in, wire data out

    private static PendingReceipt ChainSow(bool delivered, uint? soil, IReadOnlyList<SyncPatchBed>? patch) => new(
        SyncKind.Plant, IsPot: false, OpenedAt: T0, Estate: Garden, PatchOrdinal: 0, BedSlot: 2,
        PatchId: 1234, PotKey: -1, Species: Krakka, Stage: 0, Delivered: delivered,
        Soil: soil, Patch: patch);

    [Fact] // the soil the chain checked against the sow question, and the delivery's patch, reach the wire
    public void A_chain_sow_carries_its_checked_soil_and_patch_onto_the_wire()
    {
        var data = ReceiptLifecycle.DataOf(ChainSow(delivered: true, Grade3Thanalan, Patch));

        Assert.Equal((ushort)Krakka, data.Species);
        Assert.Equal(Grade3Thanalan, data.Soil);
        Assert.Equal(Patch, data.Patch);
        Assert.Null(data.Stage);          // stage 0 stays the honest unknown on the wire, as before
    }

    [Fact] // a chain-unconfirmed close is an honest degrade, not a lossy one: what was known still travels
    public void A_chain_unconfirmed_sow_still_carries_soil_and_patch()
    {
        var pending = ChainSow(delivered: true, Grade3Thanalan, Patch);
        Assert.Equal(ReceiptLifecycle.TimeoutOutcome.WriteChainUnconfirmed, ReceiptLifecycle.OnTimeout(pending));

        var data = ReceiptLifecycle.DataOf(pending);
        Assert.Equal(Grade3Thanalan, data.Soil);
        Assert.Equal(Patch, data.Patch);
    }

    [Fact] // a pot sow: soil, never a patch; an unknown soil stays absent
    public void A_pot_sow_carries_soil_and_no_patch()
    {
        var pot = new PendingReceipt(SyncKind.Plant, IsPot: true, OpenedAt: T0, Estate: Garden, PatchOrdinal: 0,
            BedSlot: 0, PatchId: 0, PotKey: 129, Species: Krakka, Stage: 0, Soil: 5810);
        var data = ReceiptLifecycle.DataOf(pot);
        Assert.Equal(5810u, data.Soil);
        Assert.Null(data.Patch);

        Assert.Null(ReceiptLifecycle.DataOf(ChainSow(delivered: true, soil: null, patch: null)).Soil);
    }

    [Fact] // the harvest's yield still rides at close, as before the move
    public void A_harvest_pending_keeps_its_yield()
    {
        var harvest = new PendingReceipt(SyncKind.Harvest, IsPot: false, OpenedAt: T0, Estate: Garden,
            PatchOrdinal: 0, BedSlot: 2, PatchId: 1234, PotKey: -1, Species: Krakka, Stage: 4,
            Yields: [new SyncYield(4789, 9)]);
        var data = ReceiptLifecycle.DataOf(harvest);
        Assert.Equal([new SyncYield(4789, 9)], data.Yields);
        Assert.Equal((byte)4, data.Stage);
    }
}
