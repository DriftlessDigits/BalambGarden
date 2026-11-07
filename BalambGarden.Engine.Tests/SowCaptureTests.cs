using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
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
}
