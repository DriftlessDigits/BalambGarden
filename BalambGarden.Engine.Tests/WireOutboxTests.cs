using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

/// <summary>The send-time half of the outbox: unsent entries become wire envelopes, or the
/// batch dams at the first entry that cannot mint its ratified pseudonym (patch id 0 on a
/// bed, house id 0 on anything - a 0 in the HMAC input would mint a phantom entity no other
/// install can match). Damming, not skipping, because MarkSent is a single high-water mark:
/// skipping an entry and sending past it would mark the skipped fact delivered.</summary>
public class WireOutboxTests
{
    private static readonly EstateKey Garden = new(340, 11, 32);
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 6, 0, 0, TimeSpan.Zero);
    private const ulong House = 0x0037015401CB0039;
    private const string User = "k7f3qx";
    private static readonly byte[] Key = HouseholdKeys.NamingKey("test household");

    private static ReceiptLog Live()
    {
        var log = new ReceiptLog();
        log.MintEra("era2ax");
        return log;
    }

    private static void Bed(ReceiptLog log, ushort patchId = 0x3312, ulong houseId = House)
        => log.Append(SyncKind.Tend, SyncSource.Chain, T0, Garden, isPot: false,
            patchOrdinal: 0, bedSlot: 3, patchId: patchId, potKey: -1, houseId: houseId);

    private static void Pot(ReceiptLog log, ulong houseId = House)
        => log.Append(SyncKind.PotWater, SyncSource.Chat, T0, Garden, isPot: true,
            patchOrdinal: 0, bedSlot: 0, patchId: 0, potKey: 208, houseId: houseId);

    [Fact]
    public void Builds_envelopes_with_the_ratified_identity()
    {
        var log = Live();
        Bed(log);
        Pot(log);

        var batch = WireOutbox.Build(log, User, Key);

        Assert.Null(batch.Refusal);
        Assert.Equal(2, batch.Receipts.Count);
        Assert.Equal(2, batch.ThroughSeq);

        var bed = batch.Receipts[0];
        Assert.Equal("k7f3qx:era2ax:1", bed.Id);
        Assert.Equal(EntityName.ForBed(Key, House, 0x3312, 3), bed.Entity);
        Assert.Equal(SyncEntityType.Bed, bed.EntityType);
        Assert.Equal(SyncKind.Tend, bed.Kind);
        Assert.Equal(SyncSource.Chain, bed.Source);
        Assert.Equal(T0, bed.At);

        var pot = batch.Receipts[1];
        Assert.Equal("k7f3qx:era2ax:2", pot.Id);
        Assert.Equal(EntityName.ForPot(Key, House, 208), pot.Entity);
        Assert.Equal(SyncEntityType.Pot, pot.EntityType);
    }

    [Fact]
    public void Sends_only_past_the_high_water_mark()
    {
        var log = Live();
        Bed(log);
        Bed(log);
        log.MarkSent(1);

        var batch = WireOutbox.Build(log, User, Key);

        Assert.Single(batch.Receipts);
        Assert.Equal("k7f3qx:era2ax:2", batch.Receipts[0].Id);
        Assert.Equal(2, batch.ThroughSeq);
    }

    [Fact]
    public void Empty_outbox_builds_an_empty_batch()
    {
        var batch = WireOutbox.Build(Live(), User, Key);
        Assert.Empty(batch.Receipts);
        Assert.Equal(0, batch.ThroughSeq);
        Assert.Null(batch.Refusal);
    }

    [Fact]
    public void Bed_with_patch_id_zero_dams_the_batch()
    {
        var log = Live();
        Bed(log);
        Bed(log, patchId: 0);   // the honest-unknown - ruled 2026-08-23, never sent
        Bed(log);               // behind the dam: sendable but must not ship past it

        var batch = WireOutbox.Build(log, User, Key);

        Assert.Single(batch.Receipts);
        Assert.Equal(1, batch.ThroughSeq);
        Assert.NotNull(batch.Refusal);
        Assert.Contains("patch id", batch.Refusal);
    }

    [Fact]
    public void House_id_zero_dams_the_batch()
    {
        var log = Live();
        Pot(log, houseId: 0);   // pre-upgrade shape: entry minted before the field existed

        var batch = WireOutbox.Build(log, User, Key);

        Assert.Empty(batch.Receipts);
        Assert.Equal(0, batch.ThroughSeq);
        Assert.NotNull(batch.Refusal);
        Assert.Contains("house id", batch.Refusal);
    }

    [Fact]
    public void Batch_caps_at_the_relay_maximum()
    {
        var log = Live();
        for (var i = 0; i < WireOutbox.MaxBatch + 1; i++)
            Bed(log);

        var batch = WireOutbox.Build(log, User, Key);

        Assert.Equal(WireOutbox.MaxBatch, batch.Receipts.Count);
        Assert.Equal(WireOutbox.MaxBatch, batch.ThroughSeq);
        Assert.Null(batch.Refusal);   // a full batch is not a dam - the rest follows next send
    }

    [Fact]
    public void Body_json_is_the_relay_shape()
    {
        var log = Live();
        Bed(log);
        var batch = WireOutbox.Build(log, User, Key);

        var body = WireOutbox.ToJson(batch.Receipts);

        Assert.StartsWith("{\"receipts\":[", body);
        Assert.Contains("\"id\":\"k7f3qx:era2ax:1\"", body);
        Assert.Contains("\"kind\":\"tend\"", body);
        Assert.EndsWith("]}", body);
    }
}
