using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Ledger;

public class EstateRosterTests
{
    private static readonly EstateKey Gardener = new(340, 11, 32);
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-14T18:00:00Z");

    [Fact] // Frame 2: estates discovered on visit and remembered
    public void UpsertCreatesThenUpdates()
    {
        var store = new LedgerStore();
        var first = store.UpsertEstate(Gardener, T0);
        Assert.Equal(T0, first.FirstSeen);
        Assert.Equal(T0, first.LastVisited);

        var second = store.UpsertEstate(Gardener, T0.AddDays(1));
        Assert.Same(first, second);
        Assert.Single(store.Estates);
        Assert.Equal(T0, second.FirstSeen);
        Assert.Equal(T0.AddDays(1), second.LastVisited);
    }

    [Fact]
    public void NicknameWinsDisplay()
    {
        var record = new EstateRecord { Key = Gardener, FirstSeen = T0, LastVisited = T0 };
        Assert.Equal("Ward 12 Plot 33", record.DisplayName);
        record.Nickname = "Gardener's";
        Assert.Equal("Gardener's", record.DisplayName);
    }

    [Fact] // the roster must survive the JSON round trip with beds and bindings intact
    public void RosterRoundTripsThroughJson()
    {
        var store = new LedgerStore();
        store.UpsertEstate(Gardener, T0).Nickname = "Gardener's";
        store.Bindings[Gardener.BindingKey(0)] = 110;
        store.Beds.Add(new ClaimedBed
        {
            Estate = Gardener, MapKey = 110, PatchOrdinal = 0, BedSlot = 3, FirstRecorded = T0,
        });

        var restored = LedgerStore.FromJson(store.ToJson());
        var estate = Assert.Single(restored.Estates);
        Assert.Equal("Gardener's", estate.Nickname);
        Assert.Equal(Gardener, estate.Key);
        Assert.Equal(110, restored.Bindings[Gardener.BindingKey(0)]);
        Assert.Equal(3, Assert.Single(restored.Beds).BedSlot);
    }
}
