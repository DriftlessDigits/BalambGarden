using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

public class ReceiptLogTests
{
    private static readonly EstateKey Garden = new(340, 11, 32);
    private static readonly EstateKey Other = new(340, 11, 33);
    private static readonly DateTimeOffset T0 = new(2026, 8, 23, 6, 0, 0, TimeSpan.Zero);

    private static LoggedReceipt Bed(
        ReceiptLog log, SyncKind kind, DateTimeOffset at,
        int patchOrdinal = 0, int bedSlot = 3, EstateKey? estate = null)
        => log.Append(kind, SyncSource.Chain, at, estate ?? Garden, isPot: false,
            patchOrdinal: patchOrdinal, bedSlot: bedSlot, patchId: 0x3312, potKey: -1);

    private static LoggedReceipt Pot(
        ReceiptLog log, SyncKind kind, DateTimeOffset at, int potKey = 3)
        => log.Append(kind, SyncSource.Chain, at, Garden, isPot: true,
            patchOrdinal: 0, bedSlot: 0, patchId: 0, potKey: potKey);

    [Fact]
    public void Append_assigns_monotonic_seq_from_one()
    {
        var log = new ReceiptLog();
        Assert.Equal(1, Bed(log, SyncKind.Tend, T0).Seq);
        Assert.Equal(2, Bed(log, SyncKind.Fertilize, T0).Seq);
        Assert.Equal(3, Pot(log, SyncKind.PotWater, T0).Seq);
        Assert.Equal(3, log.Counter);
        Assert.Equal(3, log.Entries.Count);
    }

    [Fact]
    public void Append_returns_the_entry_it_stored()
    {
        var log = new ReceiptLog();
        var entry = log.Append(SyncKind.Plant, SyncSource.Chat, T0, Garden, isPot: false,
            patchOrdinal: 1, bedSlot: 2, patchId: 0x3312, potKey: -1,
            data: new SyncData(Species: 50, Soil: 7031));
        Assert.Same(entry, log.Entries[0]);
        Assert.Equal((ushort)50, entry.Data.Species);
    }

    [Fact]
    public void Populated_log_round_trips()
    {
        var log = new ReceiptLog { InstallId = "k7f3q" };
        Bed(log, SyncKind.Plant, T0);
        log.Append(SyncKind.Harvest, SyncSource.Chat, T0.AddHours(2), Garden, isPot: false,
            patchOrdinal: 0, bedSlot: 3, patchId: 0x3312, potKey: -1,
            data: new SyncData(Yields: [new SyncYield(4842, 2)]));
        Pot(log, SyncKind.Fertilize, T0.AddHours(3));
        log.MarkSent(2);

        var back = ReceiptLog.FromJson(log.ToJson());

        Assert.Equal("k7f3q", back.InstallId);
        Assert.Equal(3, back.Counter);
        Assert.Equal(2, back.LastSentSeq);
        // Record equality on SyncData compares Yields by reference, so the whole-log
        // comparison rides the serialized form; the entries themselves are checked field-wise.
        Assert.Equal(log.ToJson(), back.ToJson());
        Assert.Equal(3, back.Entries.Count);
        Assert.Equal(log.Entries[0], back.Entries[0]);
        Assert.Equal(log.Entries[2], back.Entries[2]);
        Assert.Equal(SyncKind.Harvest, back.Entries[1].Kind);
        Assert.Equal(SyncSource.Chat, back.Entries[1].Source);
        Assert.Equal(4842u, back.Entries[1].Data.Yields![0].Item);
        Assert.Equal(2, back.Entries[1].Data.Yields![0].Qty);
        Assert.Equal(T0.AddHours(3), back.Entries[2].At);
        Assert.Equal(Garden, back.Entries[2].Estate);
    }

    [Fact]
    public void Empty_log_round_trips()
    {
        var back = ReceiptLog.FromJson(new ReceiptLog().ToJson());
        Assert.Empty(back.Entries);
        Assert.Equal("", back.InstallId);
        Assert.Equal(0, back.Counter);
        Assert.Equal(0, back.LastSentSeq);
    }

    [Fact]
    public void Last_fertilized_returns_the_latest_for_that_bed()
    {
        var log = new ReceiptLog();
        Bed(log, SyncKind.Fertilize, T0);
        Bed(log, SyncKind.Fertilize, T0.AddHours(9));
        Bed(log, SyncKind.Fertilize, T0.AddHours(4));
        Bed(log, SyncKind.Fertilize, T0.AddHours(20), bedSlot: 4);

        Assert.Equal(T0.AddHours(9),
            log.LastFertilizedAt(Garden, isPot: false, potKey: -1, patchOrdinal: 0, bedSlot: 3));
    }

    [Fact]
    public void Last_fertilized_is_null_when_never_fertilized()
    {
        var log = new ReceiptLog();
        Assert.Null(log.LastFertilizedAt(Garden, false, -1, 0, 3));

        Bed(log, SyncKind.Fertilize, T0, estate: Other);
        Assert.Null(log.LastFertilizedAt(Garden, false, -1, 0, 3));
    }

    [Fact]
    public void A_tend_never_answers_for_fertilize()
    {
        var log = new ReceiptLog();
        Bed(log, SyncKind.Tend, T0);
        Assert.Null(log.LastFertilizedAt(Garden, false, -1, 0, 3));

        Bed(log, SyncKind.Fertilize, T0.AddHours(1));
        Bed(log, SyncKind.Tend, T0.AddHours(5));
        Assert.Equal(T0.AddHours(1), log.LastFertilizedAt(Garden, false, -1, 0, 3));
    }

    [Fact]
    public void Pot_and_bed_identities_never_cross_match()
    {
        var log = new ReceiptLog();
        Pot(log, SyncKind.Fertilize, T0, potKey: 3);

        // Same numbers on the bed side: ordinal 0, slot 3, key 3.
        Assert.Null(log.LastFertilizedAt(Garden, isPot: false, potKey: 3, patchOrdinal: 0, bedSlot: 3));
        Assert.Equal(T0,
            log.LastFertilizedAt(Garden, isPot: true, potKey: 3, patchOrdinal: 0, bedSlot: 3));

        var beds = new ReceiptLog();
        Bed(beds, SyncKind.Fertilize, T0);
        Assert.Null(beds.LastFertilizedAt(Garden, isPot: true, potKey: 3, patchOrdinal: 0, bedSlot: 3));
    }

    [Fact]
    public void A_fresh_log_is_entirely_unsent()
    {
        var log = new ReceiptLog();
        Bed(log, SyncKind.Tend, T0);
        Bed(log, SyncKind.Tend, T0.AddHours(1));
        Assert.Equal([1L, 2L], log.Unsent().Select(e => e.Seq));
    }

    [Fact]
    public void Marking_sent_shrinks_the_outbox()
    {
        var log = new ReceiptLog();
        for (var i = 0; i < 4; i++)
            Bed(log, SyncKind.Tend, T0.AddHours(i));

        log.MarkSent(2);
        Assert.Equal([3L, 4L], log.Unsent().Select(e => e.Seq));

        log.MarkSent(4);
        Assert.Empty(log.Unsent());

        Bed(log, SyncKind.Fertilize, T0.AddHours(9));
        Assert.Equal([5L], log.Unsent().Select(e => e.Seq));
    }

    [Fact]
    public void Marking_sent_lower_is_ignored()
    {
        var log = new ReceiptLog();
        for (var i = 0; i < 3; i++)
            Bed(log, SyncKind.Tend, T0.AddHours(i));

        log.MarkSent(3);
        log.MarkSent(1);
        Assert.Equal(3, log.LastSentSeq);
        Assert.Empty(log.Unsent());
    }

    [Fact]
    public void Minting_opens_a_fresh_id_space_and_keeps_history()
    {
        var log = new ReceiptLog { InstallId = "old" };
        Bed(log, SyncKind.Fertilize, T0);
        Bed(log, SyncKind.Tend, T0.AddHours(1));
        log.MarkSent(2);

        log.MintInstallId("k7f3q");

        Assert.Equal("k7f3q", log.InstallId);
        Assert.Equal(0, log.Counter);
        Assert.Equal(0, log.LastSentSeq);
        Assert.Equal(2, log.Entries.Count);

        Assert.Equal(1, Bed(log, SyncKind.Tend, T0.AddHours(2)).Seq);

        // History survives the re-mint - the fertilize is still local truth.
        Assert.Equal(T0, log.LastFertilizedAt(Garden, false, -1, 0, 3));
    }
}
