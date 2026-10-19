using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

public class ReceiptLifecycleTests
{
    private static readonly EstateKey Garden = new(340, 11, 32);
    private static readonly DateTimeOffset Opened = new(2026, 8, 23, 21, 0, 0, TimeSpan.Zero);

    private static PendingReceipt Bed(SyncKind kind = SyncKind.Tend, bool delivered = false) => new(
        kind, IsPot: false, OpenedAt: Opened, Estate: Garden,
        PatchOrdinal: 0, BedSlot: 3, PatchId: 0x3312, PotKey: -1, Species: 88, Stage: 2,
        Delivered: delivered);

    private static PendingReceipt Pot(SyncKind kind = SyncKind.PotWater, bool delivered = false) => new(
        kind, IsPot: true, OpenedAt: Opened, Estate: Garden,
        PatchOrdinal: 7, BedSlot: 0, PatchId: 0, PotKey: 7, Species: 88, Stage: 2,
        Delivered: delivered);

    [Fact]
    public void The_matching_line_inside_the_window_closes_the_act()
    {
        Assert.True(ReceiptLifecycle.Matches(Bed(), ChatVerdict.TendBed, Opened.AddSeconds(4)));
        Assert.True(ReceiptLifecycle.Matches(Pot(), ChatVerdict.WaterPot, Opened.AddSeconds(4)));
    }

    [Fact] // capture 2026-10-18: the sow line names its bed, so it closes only that bed's act
    public void A_sow_line_closes_the_plant_on_the_bed_it_names()
    {
        var plant = Bed(SyncKind.Plant);
        Assert.True(ReceiptLifecycle.Matches(plant, ChatVerdict.PlantBed, Opened.AddSeconds(1), lineBed: (0, 3)));
        Assert.False(ReceiptLifecycle.Matches(plant, ChatVerdict.PlantBed, Opened.AddSeconds(1), lineBed: (0, 2)));
        Assert.False(ReceiptLifecycle.Matches(plant, ChatVerdict.PlantBed, Opened.AddSeconds(1), lineBed: (1, 3)));
        Assert.False(ReceiptLifecycle.Matches(Pot(SyncKind.Plant), ChatVerdict.PlantBed, Opened.AddSeconds(1), lineBed: (0, 0)));
    }

    [Fact]
    public void The_open_instant_itself_closes()
        => Assert.True(ReceiptLifecycle.Matches(Bed(), ChatVerdict.TendBed, Opened));

    [Fact]
    public void A_different_kind_never_closes()
    {
        Assert.False(ReceiptLifecycle.Matches(Bed(), ChatVerdict.FertilizeBed, Opened.AddSeconds(1)));
        Assert.False(ReceiptLifecycle.Matches(Bed(SyncKind.Plant), ChatVerdict.TendBed, Opened.AddSeconds(1)));
    }

    [Fact]
    public void The_same_kind_in_the_wrong_container_never_closes()
    {
        // Both sides are SyncKind.Fertilize - only the game's own sentence separates them, and
        // this is the case the kind check alone would wave through.
        Assert.False(ReceiptLifecycle.Matches(
            Bed(SyncKind.Fertilize), ChatVerdict.FertilizePot, Opened.AddSeconds(1)));
        Assert.False(ReceiptLifecycle.Matches(
            Pot(SyncKind.Fertilize), ChatVerdict.FertilizeBed, Opened.AddSeconds(1)));
    }

    [Fact]
    public void A_line_past_the_window_does_not_close_an_act()
        => Assert.False(ReceiptLifecycle.Matches(
            Bed(), ChatVerdict.TendBed, Opened + ReceiptLifecycle.CloseWindow + TimeSpan.FromMilliseconds(1)));

    [Fact]
    public void A_line_from_before_the_act_is_not_evidence()
        => Assert.False(ReceiptLifecycle.Matches(Bed(), ChatVerdict.TendBed, Opened.AddSeconds(-1)));

    // ---- The harvest close (capture 2026-08-26: LootNotice/62 obtain line, no container) ----

    [Fact]
    public void The_obtain_line_closes_a_harvest_in_either_container()
    {
        // The line names no container, so the pending's own container is the only claim standing
        // - a bed harvest and a pot harvest both close on the same words.
        Assert.True(ReceiptLifecycle.Matches(
            Bed(SyncKind.Harvest), ChatVerdict.HarvestObtain, Opened.AddSeconds(4)));
        Assert.True(ReceiptLifecycle.Matches(
            Pot(SyncKind.Harvest), ChatVerdict.HarvestObtain, Opened.AddSeconds(4)));
    }

    [Fact]
    public void An_obtain_line_never_closes_a_non_harvest_act()
        => Assert.False(ReceiptLifecycle.Matches(Bed(), ChatVerdict.HarvestObtain, Opened.AddSeconds(1)));

    [Fact]
    public void An_item_that_contradicts_the_pending_species_refuses_the_close()
        // The obtain shape is generic - a venture yield or loot roll inside the window rides the
        // same structure. The ItemPayload is the game naming what arrived; if that resolves to a
        // species and the pending banked a different one, this is not our line. The pending then
        // times out chain-unconfirmed, which is the honest word for it.
        => Assert.False(ReceiptLifecycle.Matches(
            Pot(SyncKind.Harvest), ChatVerdict.HarvestObtain, Opened.AddSeconds(1), itemSpecies: 12));

    [Fact]
    public void An_unresolvable_item_still_closes_a_harvest()
        // Flowers: the obtain names a bouquet, which is an item, not a species - it resolves to
        // nothing. Absence is not a mismatch on the chain branch; the pending's armed identity
        // plus the 6s window carry the claim.
        => Assert.True(ReceiptLifecycle.Matches(
            Pot(SyncKind.Harvest), ChatVerdict.HarvestObtain, Opened.AddSeconds(1), itemSpecies: 0));

    [Fact]
    public void An_agreeing_item_closes_a_harvest()
        => Assert.True(ReceiptLifecycle.Matches(
            Pot(SyncKind.Harvest), ChatVerdict.HarvestObtain, Opened.AddSeconds(1), itemSpecies: 88));

    [Fact]
    public void An_obtain_line_past_the_window_does_not_close_a_harvest()
        => Assert.False(ReceiptLifecycle.Matches(
            Pot(SyncKind.Harvest), ChatVerdict.HarvestObtain,
            Opened + ReceiptLifecycle.CloseWindow + TimeSpan.FromMilliseconds(1)));

    [Fact]
    public void None_closes_nothing_and_does_not_throw()
    {
        // The adapter drops None lines before the desk sees them, but the desk must not be the
        // thing that breaks chat if one ever arrives.
        Assert.False(ReceiptLifecycle.Matches(Bed(), ChatVerdict.None, Opened.AddSeconds(1)));
    }

    [Fact]
    public void An_act_inside_the_window_has_not_expired()
    {
        Assert.False(ReceiptLifecycle.Expired(Bed(), Opened));
        Assert.False(ReceiptLifecycle.Expired(Bed(), Opened.AddSeconds(5)));
    }

    [Fact]
    public void The_window_edge_is_still_live()
    {
        // RULED EDGE: elapsed == CloseWindow is live, not expired - the exact complement of the
        // Matches timing test, so no instant exists where the sweep would bury an act the
        // classifier would still have closed.
        var edge = Opened + ReceiptLifecycle.CloseWindow;
        Assert.False(ReceiptLifecycle.Expired(Bed(), edge));
        Assert.True(ReceiptLifecycle.Matches(Bed(), ChatVerdict.TendBed, edge));
    }

    [Fact]
    public void One_tick_past_the_edge_is_expired()
    {
        var late = Opened + ReceiptLifecycle.CloseWindow + TimeSpan.FromMilliseconds(1);
        Assert.True(ReceiptLifecycle.Expired(Bed(), late));
        Assert.False(ReceiptLifecycle.Matches(Bed(), ChatVerdict.TendBed, late));
    }

    [Fact]
    public void An_unknown_close_line_leaves_the_act_to_time_out()
    {
        // Harvest and the bed sow line are not taped yet (2026-08-23), so their acts are meant
        // to reach the sweep and be labelled chain-unconfirmed rather than quietly confirmed.
        var harvest = Bed(SyncKind.Harvest);
        foreach (var verdict in new[]
                 {
                     ChatVerdict.FertilizeBed, ChatVerdict.FertilizePot,
                     ChatVerdict.PlantPot, ChatVerdict.TendBed, ChatVerdict.WaterPot,
                     ChatVerdict.PlantBed,
                 })
            Assert.False(ReceiptLifecycle.Matches(harvest, verdict, Opened.AddSeconds(1)));

        Assert.True(ReceiptLifecycle.Expired(harvest, Opened.AddSeconds(30)));
    }

    // ---- RULING 6: an undelivered pending cancels instead of writing --------------------

    [Fact]
    public void A_pending_starts_undelivered()
    {
        // The default matters: every open goes through the same constructor, and an act that
        // forgot to say it completed must read as "we tried", never as "it happened".
        Assert.False(Bed().Delivered);
        Assert.False(Pot().Delivered);
    }

    [Fact]
    public void A_delivered_act_still_writes_its_chain_unconfirmed_line()
    {
        // The old behaviour, unchanged: the chain's own receipt landed, the census already
        // believes it, and only the game's sentence is missing.
        Assert.Equal(
            ReceiptLifecycle.TimeoutOutcome.WriteChainUnconfirmed,
            ReceiptLifecycle.OnTimeout(Bed(delivered: true)));
        Assert.Equal(
            ReceiptLifecycle.TimeoutOutcome.WriteChainUnconfirmed,
            ReceiptLifecycle.OnTimeout(Pot(SyncKind.Harvest, delivered: true)));
    }

    [Fact]
    public void An_act_nobody_witnessed_cancels_and_writes_nothing()
    {
        // Two absent witnesses - no chain receipt, no chat line - are not a fact. A menu that
        // closed under us or a sow the prompt refused must not enter an append-only history.
        Assert.Equal(ReceiptLifecycle.TimeoutOutcome.Cancel, ReceiptLifecycle.OnTimeout(Bed()));
        Assert.Equal(
            ReceiptLifecycle.TimeoutOutcome.Cancel, ReceiptLifecycle.OnTimeout(Pot(SyncKind.Plant)));
    }

    // ---- THE REVERSE RACE: a delivery arriving after its own line closed the act ---------

    private static ReceiptLifecycle.RecentClose Closed(
        SyncKind kind = SyncKind.PotWater, bool isPot = true)
        => new(kind, isPot, Opened);

    [Fact]
    public void A_delivery_right_after_its_own_line_closed_the_act_opens_nothing()
        => Assert.True(ReceiptLifecycle.DeliveryAlreadyClosed(
            SyncKind.PotWater, isPot: true, Closed(), Opened.AddSeconds(2)));

    [Fact]
    public void The_window_edge_still_suppresses()
        => Assert.True(ReceiptLifecycle.DeliveryAlreadyClosed(
            SyncKind.PotWater, isPot: true, Closed(), Opened + ReceiptLifecycle.CloseWindow));

    [Fact]
    public void A_delivery_long_after_the_close_is_its_own_act()
    {
        // Past the window the suppression has to let go, or the NEXT water of the same pot
        // would be swallowed by the last one's memory.
        var late = Opened + ReceiptLifecycle.CloseWindow + TimeSpan.FromMilliseconds(1);
        Assert.False(ReceiptLifecycle.DeliveryAlreadyClosed(
            SyncKind.PotWater, isPot: true, Closed(), late));
    }

    [Fact]
    public void A_close_from_the_future_is_not_evidence()
        => Assert.False(ReceiptLifecycle.DeliveryAlreadyClosed(
            SyncKind.PotWater, isPot: true, Closed(), Opened.AddSeconds(-1)));

    [Fact]
    public void No_close_has_happened_at_all()
        => Assert.False(ReceiptLifecycle.DeliveryAlreadyClosed(
            SyncKind.PotWater, isPot: true, lastClose: null, Opened.AddSeconds(1)));

    [Fact]
    public void A_close_of_a_different_kind_suppresses_nothing()
        => Assert.False(ReceiptLifecycle.DeliveryAlreadyClosed(
            SyncKind.Plant, isPot: true, Closed(SyncKind.PotWater), Opened.AddSeconds(1)));

    [Fact]
    public void A_close_in_the_other_container_suppresses_nothing()
    {
        // Same SyncKind, different container - the case the kind check alone waves through,
        // exactly as at the Matches desk: a bed fertilize must not be silenced by a pot one.
        Assert.False(ReceiptLifecycle.DeliveryAlreadyClosed(
            SyncKind.Fertilize, isPot: false,
            Closed(SyncKind.Fertilize, isPot: true), Opened.AddSeconds(1)));
        Assert.False(ReceiptLifecycle.DeliveryAlreadyClosed(
            SyncKind.Fertilize, isPot: true,
            Closed(SyncKind.Fertilize, isPot: false), Opened.AddSeconds(1)));
    }
}
