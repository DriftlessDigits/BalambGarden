using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

public class ReceiptLifecycleTests
{
    private static readonly EstateKey Garden = new(340, 11, 32);
    private static readonly DateTimeOffset Opened = new(2026, 8, 23, 21, 0, 0, TimeSpan.Zero);

    private static PendingReceipt Bed(SyncKind kind = SyncKind.Tend) => new(
        kind, IsPot: false, OpenedAt: Opened, Estate: Garden,
        PatchOrdinal: 0, BedSlot: 3, PatchId: 0x3312, PotKey: -1, Species: 88, Stage: 2);

    private static PendingReceipt Pot(SyncKind kind = SyncKind.PotWater) => new(
        kind, IsPot: true, OpenedAt: Opened, Estate: Garden,
        PatchOrdinal: 7, BedSlot: 0, PatchId: 0, PotKey: 7, Species: 88, Stage: 2);

    [Fact]
    public void The_matching_line_inside_the_window_closes_the_act()
    {
        Assert.True(ReceiptLifecycle.Matches(Bed(), ChatVerdict.TendBed, Opened.AddSeconds(4)));
        Assert.True(ReceiptLifecycle.Matches(Pot(), ChatVerdict.WaterPot, Opened.AddSeconds(4)));
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
                 })
            Assert.False(ReceiptLifecycle.Matches(harvest, verdict, Opened.AddSeconds(1)));

        Assert.True(ReceiptLifecycle.Expired(harvest, Opened.AddSeconds(30)));
    }
}
