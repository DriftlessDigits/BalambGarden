using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

/// <summary>The cross seed on the harvest row (ruled 2026-10-18), off the same night's capture:
/// crop line, then a seed line in the same millisecond when the cross takes, nothing at all when
/// it misses. Three states - crossed, missed, not heard.</summary>
public class HarvestGatherTests
{
    private static readonly DateTimeOffset Line = new(2026, 10, 19, 1, 8, 56, TimeSpan.Zero);
    private const uint CurielSeeds = 8169, SunLemonSeeds = 8170;

    [Fact] // "You obtain a bag of sun lemon seeds." in the crop line's millisecond
    public void A_seed_line_in_the_window_is_a_cross()
    {
        var g = new HarvestGather(Line);
        Assert.True(g.Offer(SunLemonSeeds, 1, Line));
        Assert.Equal((1, SunLemonSeeds), g.Result(heard: true));
    }

    [Fact] // 19:56:55 bed 3: crop line, then 35s of silence
    public void A_closed_empty_window_is_a_miss_zero_and_null()
    {
        var g = new HarvestGather(Line);
        Assert.False(g.Due(Line + HarvestGather.Window));
        Assert.True(g.Due(Line + HarvestGather.Window + TimeSpan.FromMilliseconds(1)));
        Assert.Equal((0, (uint?)null), g.Result(heard: true));
    }

    [Fact] // cut short with nothing heard (an unload mid-listen) is not a miss
    public void A_window_cut_short_is_not_heard()
        => Assert.Equal(((int?)null, (uint?)null), new HarvestGather(Line).Result(heard: false));

    [Fact] // a seed that did arrive stands even if the window is cut short
    public void A_seed_already_heard_survives_a_cut_window()
    {
        var g = new HarvestGather(Line);
        g.Offer(CurielSeeds, 1, Line);
        Assert.Equal((1, CurielSeeds), g.Result(heard: false));
    }

    [Fact]
    public void A_late_seed_line_is_refused()
        => Assert.False(new HarvestGather(Line).Offer(CurielSeeds, 1, Line + TimeSpan.FromSeconds(3)));

    [Fact] // the clock moved under us - not evidence
    public void A_seed_line_before_the_crop_line_is_refused()
        => Assert.False(new HarvestGather(Line).Offer(CurielSeeds, 1, Line - TimeSpan.FromSeconds(1)));

    [Fact] // same seed twice adds; a different seed is a shape never seen, refused
    public void Same_seed_adds_and_a_different_seed_is_refused()
    {
        var g = new HarvestGather(Line);
        Assert.True(g.Offer(CurielSeeds, 1, Line));
        Assert.True(g.Offer(CurielSeeds, 2, Line));
        Assert.False(g.Offer(SunLemonSeeds, 1, Line));
        Assert.Equal((3, CurielSeeds), g.Result(heard: true));
    }

    [Fact] // on the wire a miss is an explicit 0; not heard is absent; the round trip keeps both
    public void Seed_fields_ride_the_wire_in_all_three_states()
    {
        SyncReceipt Harvest(SyncData d) => new(
            "hhbbaa:eraaaa:1", "entity-a", SyncEntityType.Bed, SyncKind.Harvest, Line, SyncSource.Chat, d);

        var miss = Harvest(new SyncData(Species: 17, SeedQty: 0)).ToJson();
        Assert.Contains("\"seedQty\":0", miss);
        Assert.DoesNotContain("seedItem", miss);

        var notHeard = Harvest(new SyncData(Species: 17)).ToJson();
        Assert.DoesNotContain("seed", notHeard);

        var hit = SyncReceipt.FromJson(Harvest(new SyncData(Species: 17, SeedQty: 1, SeedItem: SunLemonSeeds)).ToJson())!;
        Assert.Equal(1, hit.Data.SeedQty);
        Assert.Equal(SunLemonSeeds, hit.Data.SeedItem);
    }

    [Fact] // a household harvest's seed lands on this install's history row
    public void A_foreign_harvest_carries_its_seed_into_history()
    {
        var bed = new ClaimedBed { Estate = new EstateKey(340, 11, 57), MapKey = 1293, PatchOrdinal = 0, BedSlot = 3 };
        var r = new SyncReceipt("hhbbaa:eraaaa:7", "entity-a", SyncEntityType.Bed, SyncKind.Harvest, Line,
            SyncSource.Chain, new SyncData(Species: 17, SeedQty: 1, SeedItem: SunLemonSeeds));
        var e = HistoryEntry.FromForeign(r, bed, new Hand("hhbbaa"))!;
        Assert.Equal(1, e.SeedQty);
        Assert.Equal(SunLemonSeeds, e.SeedItem);
    }

    [Fact] // history.json written before this change loads every harvest as not heard
    public void An_old_history_row_loads_as_not_heard()
    {
        var json = """
            {"Entries":[{"Id":"qqkx86:t47727:2730","At":"2026-10-19T00:40:53+00:00","Kind":"Harvest",
            "Species":49,"Estate":{"TerritoryId":340,"Ward":11,"Plot":57,"Room":-1,"IsApartment":false,
            "ApartmentDivision":-1,"IsIndoorOnly":false},"IsPot":false,"PatchOrdinal":0,"BedSlot":0,
            "By":"own","Source":"Chat","Yields":[{"item":8165,"qty":9}]}]}
            """;
        var e = Assert.Single(GardenHistory.FromJson(json).Entries);
        Assert.Null(e.SeedQty);
        Assert.Null(e.SeedItem);
    }
}
