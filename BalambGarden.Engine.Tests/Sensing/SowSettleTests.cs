using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Sensing;

/// <summary>The chain sow's settle (v0.4.1 field-fire, 2026-11-07): the chain re-reads the map the
/// moment the player's Yes lands, a beat before the game puts the seed in, so the bed reads empty
/// while the record already holds the new plant - and the dashboard called that "replanted without
/// me?" until the next read. The bed now waits for the map to show its own sow; while it waits, an
/// empty read is the game catching up, not news.</summary>
public class SowSettleTests
{
    private static readonly DateTimeOffset Yes = new(2026, 11, 7, 16, 24, 1, 721, TimeSpan.Zero);
    private const int Patch2 = 1293;

    private static Dictionary<int, IReadOnlyList<BedReading>> Read(int key, int slot, bool occupied)
        => new()
        {
            [key] = Enumerable.Range(0, 8)
                .Select(s => new BedReading(s, s == slot && occupied ? (ushort)65 : (ushort)0,
                    s == slot && occupied ? (byte)1 : (byte)0, 0, s == slot && occupied))
                .ToList(),
        };

    [Fact] // the field case: Yes, an empty read, then the seed shows
    public void A_sown_bed_waits_until_the_map_shows_its_seed()
    {
        var settle = new SowSettle();
        settle.Watch(Patch2, 0, Yes);

        Assert.Empty(settle.Settle(Read(Patch2, 0, occupied: false), Yes + TimeSpan.FromMilliseconds(250)));
        Assert.True(settle.Waiting(Patch2, 0));

        var done = Assert.Single(settle.Settle(Read(Patch2, 0, occupied: true), Yes + TimeSpan.FromMilliseconds(750)));
        Assert.True(done.Shown);
        Assert.Equal(TimeSpan.FromMilliseconds(750), done.After);
        Assert.False(settle.Waiting(Patch2, 0));
        Assert.False(settle.Any);
    }

    [Fact] // a map that never shows the seed stops being excused - after that, empty is news again
    public void The_wait_gives_up_at_the_limit()
    {
        var settle = new SowSettle();
        settle.Watch(Patch2, 3, Yes);

        Assert.Empty(settle.Settle(Read(Patch2, 3, occupied: false), Yes + SowSettle.Limit - TimeSpan.FromMilliseconds(1)));
        var done = Assert.Single(settle.Settle(Read(Patch2, 3, occupied: false), Yes + SowSettle.Limit));
        Assert.False(done.Shown);
        Assert.False(settle.Waiting(Patch2, 3));
    }

    [Fact] // only the sown bed is excused; its neighbors and other patches still speak
    public void Only_the_sown_bed_waits()
    {
        var settle = new SowSettle();
        settle.Watch(Patch2, 0, Yes);

        Assert.False(settle.Waiting(Patch2, 1));
        Assert.False(settle.Waiting(Patch2 + 1, 0));
    }

    [Fact] // the patch went missing from the read (walked off mid-run): keep waiting, the limit decides
    public void A_read_without_the_patch_keeps_waiting()
    {
        var settle = new SowSettle();
        settle.Watch(Patch2, 0, Yes);

        Assert.Empty(settle.Settle(new Dictionary<int, IReadOnlyList<BedReading>>(), Yes + TimeSpan.FromSeconds(1)));
        Assert.True(settle.Waiting(Patch2, 0));
    }
}
