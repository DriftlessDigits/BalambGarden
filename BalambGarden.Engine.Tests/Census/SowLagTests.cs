using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

/// <summary>The sow-lag reads-empty race (Infra Fix 2.6, review pass 2 #15), closed against the
/// 2026-10-18 capture. The fear: a map read that still shows the bed EMPTY just after a sow would
/// reset the fresh planting and lose its anchor. The answer:
///
/// <para>HAND SOWS - by construction. The plant lands only once the map names the seed (the parked
/// sow), so no empty read of that bed can follow it.</para>
///
/// <para>CHAIN SOWS - by the field. All 45 bed sows in the maintainer's trail (the 2026-10-18
/// evening: chain replants and hand sows) show no reads-empty reset within 15 seconds of any of
/// them; the map already names the seedling by the next read. One evening, one estate - field
/// evidence, not a proof; a reads-empty reset logged right after a sow would reopen this.</para>
///
/// <para>What the desk can pin is the read itself: the capture's first map read after a sow names
/// the seedling, and that read keeps the anchor at the sow line's instant.</para></summary>
public class SowLagTests
{
    private static readonly EstateKey Estate = new(340, 11, 57);
    private const int MapKey = 1293;
    private const ushort MirrorApple = 0x11;

    private static DateTimeOffset At(string local) => DateTimeOffset.Parse($"2026-10-18T{local}-05:00");

    private static byte[] Bytes(string hex)
        => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();

    // captures/2026-10-18-sow-lag-map-reads.log, key=1293. Bed 2 (slot 1) ripe Mirror Apple, then
    // the same patch after the bed 2 sow: slot 1 is Mirror Apple at stage 1.
    private const string BeforeHarvest =
        "31 00 01 00 00 10 11 00 04 00 00 00 31 00 04 00 00 00 11 00 04 00 00 00 " +
        "31 00 04 00 00 87 11 00 04 00 00 01 31 00 04 00 00 00 11 00 04 00 00 00";
    private const string AfterSow =
        "31 00 01 00 00 10 11 00 01 00 00 00 31 00 04 00 00 00 11 00 04 00 00 00 " +
        "31 00 04 00 00 87 11 00 04 00 00 01 31 00 04 00 00 00 11 00 04 00 00 00";

    [Fact] // line 19:50:51.372 "You plant seeds in the 2nd bed, 1st patch."; next read 19:50:54.199
    public void The_first_read_after_a_sow_keeps_the_anchor_at_the_line()
    {
        var ledger = new LedgerStore();
        var engine = new CensusEngine(ledger);
        engine.Bind(Estate, 0, mapKey: MapKey);

        engine.OnMapSighting(Estate, MapKey, MapFormat.DecodeOutdoorEntry(Bytes(BeforeHarvest)),
            At("19:49:03.896"), mayRecord: true);
        engine.OnReceipt(new ReceiptEvent(Estate, 0, 1, ReceiptVerb.Harvest, MirrorApple, 4, At("19:50:13.598")));
        var line = At("19:50:51.372");
        engine.OnReceipt(new ReceiptEvent(Estate, 0, 1, ReceiptVerb.Plant, MirrorApple, 1, line));

        var rebases = new List<RebaseEvent>();
        var read = MapFormat.DecodeOutdoorEntry(Bytes(AfterSow));
        Assert.Equal((MirrorApple, (byte)1, true), (read[1].SpeciesIndex, read[1].Stage, read[1].Occupied));
        engine.OnMapSighting(Estate, MapKey, read, At("19:50:54.199"), mayRecord: true, rebases: rebases);

        var bed = ledger.Beds.Single(b => b.MapKey == MapKey && b.BedSlot == 1);
        Assert.Equal(line, bed.PlantedAt);
        Assert.DoesNotContain(rebases, r => r.Bed.BedSlot == 1 && r.RemovedAnything);
    }
}
