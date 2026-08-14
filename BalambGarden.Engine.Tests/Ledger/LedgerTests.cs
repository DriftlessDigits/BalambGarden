using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Ledger;

public class LedgerTests
{
    private static readonly EstateKey Chelsea = new(TerritoryId: 340, Ward: 11, Plot: 32);
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-13T19:10:00Z");

    private static ClaimedBed NewBed() => new()
    {
        Estate = Chelsea, MapKey = 110, PatchOrdinal = 0, BedSlot = 3,
        IsPot = false, ClaimedAt = T0,
    };

    [Fact] // 0-based raw storage, +1 display only (Frame 2 / probe-proven gotcha)
    public void DisplayConvertsZeroBasedToHuman()
        => Assert.Equal("Ward 12 Plot 33", Chelsea.DisplayWardPlot());

    [Fact]
    public void RingKeepsNewestEight()
    {
        var bed = NewBed();
        for (var i = 0; i < 12; i++)
            bed.Observe(new Observation(T0.AddHours(i), 0x41, 1, ObservationSource.MapSighting));
        Assert.Equal(8, bed.Ring.Count);
        Assert.Equal(T0.AddHours(11), bed.Latest!.At);   // newest kept
        Assert.Equal(T0.AddHours(4), bed.Ring.Min(o => o.At)); // oldest four dropped
    }

    [Fact]
    public void LedgerRoundTripsThroughJson()
    {
        var store = new LedgerStore();
        var bed = NewBed();
        bed.Observe(new Observation(T0, 0x41, 1, ObservationSource.PlantReceipt));
        bed.LastTended = T0;
        store.Beds.Add(bed);
        store.Bindings[$"{Chelsea.TerritoryId}:{Chelsea.Ward}:{Chelsea.Plot}:-1#0"] = 110;

        var restored = LedgerStore.FromJson(store.ToJson());
        var rb = Assert.Single(restored.Beds);
        Assert.Equal(Chelsea, rb.Estate);
        Assert.Equal(110, rb.MapKey);
        Assert.Equal(3, rb.BedSlot);
        Assert.Equal(T0, rb.LastTended);
        var obs = Assert.Single(rb.Ring);
        Assert.Equal(ObservationSource.PlantReceipt, obs.Source);
        Assert.Equal(110, restored.Bindings[$"{Chelsea.TerritoryId}:{Chelsea.Ward}:{Chelsea.Plot}:-1#0"]);
    }
}
