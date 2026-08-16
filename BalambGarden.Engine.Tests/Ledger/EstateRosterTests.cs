using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Ledger;

public class EstateRosterTests
{
    private static readonly EstateKey Chelsea = new(340, 11, 32);
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-14T18:00:00Z");

    [Fact] // Frame 2: estates discovered on visit and remembered
    public void UpsertCreatesThenUpdates()
    {
        var store = new LedgerStore();
        var first = store.UpsertEstate(Chelsea, T0);
        Assert.Equal(T0, first.FirstSeen);
        Assert.Equal(T0, first.LastVisited);

        var second = store.UpsertEstate(Chelsea, T0.AddDays(1));
        Assert.Same(first, second);
        Assert.Single(store.Estates);
        Assert.Equal(T0, second.FirstSeen);
        Assert.Equal(T0.AddDays(1), second.LastVisited);
    }

    [Fact]
    public void NicknameWinsDisplay()
    {
        var record = new EstateRecord { Key = Chelsea, FirstSeen = T0, LastVisited = T0 };
        Assert.Equal("Ward 12 Plot 33", record.DisplayName);
        record.Nickname = "Chelsea's";
        Assert.Equal("Chelsea's", record.DisplayName);
    }

    [Fact] // the roster must survive the JSON round trip with beds and bindings intact
    public void RosterRoundTripsThroughJson()
    {
        var store = new LedgerStore();
        store.UpsertEstate(Chelsea, T0).Nickname = "Chelsea's";
        store.Bindings[Chelsea.BindingKey(0)] = 110;
        store.Beds.Add(new ClaimedBed
        {
            Estate = Chelsea, MapKey = 110, PatchOrdinal = 0, BedSlot = 3, FirstRecorded = T0,
        });

        var restored = LedgerStore.FromJson(store.ToJson());
        var estate = Assert.Single(restored.Estates);
        Assert.Equal("Chelsea's", estate.Nickname);
        Assert.Equal(Chelsea, estate.Key);
        Assert.Equal(110, restored.Bindings[Chelsea.BindingKey(0)]);
        Assert.Equal(3, Assert.Single(restored.Beds).BedSlot);
    }
}
