using BalambGarden.Engine.Derivations;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>Rethink slice 27: the words that tell a state the clock alone holds from the same state
/// seen (spec ruling 24), as drawn on the plan picture (`rethink27-plan-mock.png`, option B, the
/// builder's pick while the player's ruling waits). Tonight at 8:00 pm on the live data: 15 seen
/// ripe and 12 ripe by the clock at FC Estate, pot 208 wilting by the clock. 24-hour clock (the
/// default; the 12-hour words are the same with am/pm).</summary>
public class ClockWordsTests
{
    private static DateTimeOffset Cst(string local) => DateTimeOffset.Parse(local + "-06:00");

    [Fact]
    public void TheHarvestChipSaysSeenAndByTheClockApart()
    {
        Assert.Equal("15 seen ripe, 12 ripe by the clock; the rest may ripen within 24h", WindowFormat.HarvestDue(27, 12, "24h"));
        Assert.Equal("15 seen ripe; the rest may ripen within 24h", WindowFormat.HarvestDue(15, 0, "24h"));
        Assert.Equal("12 ripe by the clock; the rest may ripen within 24h", WindowFormat.HarvestDue(12, 12, "24h"));
        Assert.Equal("may ripen within 24h", WindowFormat.HarvestDue(0, 0, "24h"));
    }

    [Fact]
    public void TheWorstLineSaysByTheClock()
    {
        Assert.Equal("worst: a pot, wilting by the clock",
            WindowFormat.Worst(new ContainerCount(0, 1, null), ContainerLook.Wilting, byClock: true));
        Assert.Equal("worst: 4 pots, wilting", WindowFormat.Worst(new ContainerCount(0, 4, null), ContainerLook.Wilting));
    }

    [Fact]
    public void TheReplantChipSaysDeadByTheClock()
    {
        var one = new ContainerCount(0, 1, null);
        Assert.Equal("1 pot to clear and sow: dead by the clock", WindowFormat.ReplantDue(1, one, deadByClock: one));
        Assert.Equal("A pot dead - clear and sow", WindowFormat.ReplantDue(1, one));
    }

    [Fact]
    public void ACellRipeByTheClockSaysSinceWhenFromWhatAndItsLastRead()
        => Assert.Equal(
            "ripe by the clock since ~15:18\nsown Sat 14:18, 120 h to grow\nlast read stage 3, Wed 19:23",
            WindowFormat.RipeByClock(Cst("2026-11-12T15:18:00"), Cst("2026-11-07T14:18:00"), 120, 3,
                Cst("2026-11-11T19:23:00"), Cst("2026-11-12T20:00:00")));

    [Fact]
    public void TheLaneSaysSeenAndByTheClockApart()
    {
        Assert.Equal("Krakka Root ripe now", WindowFormat.LaneRipeLabel("Krakka Root", byClock: false));
        Assert.Equal("Mirror Apple ripe by the clock", WindowFormat.LaneRipeLabel("Mirror Apple", byClock: true));
        Assert.Equal("Krakka Root: seen ripe - harvest any time", WindowFormat.LaneRipeHover("Krakka Root", byClock: false));
        Assert.Equal("Mirror Apple: ripe by the clock - harvest any time", WindowFormat.LaneRipeHover("Mirror Apple", byClock: true));
    }

    [Fact]
    public void ACellSeenRipeSaysWhenItWasSeen()
        => Assert.Equal("ripe now (seen Wed 19:23)", WindowFormat.SeenRipe(Cst("2026-11-11T19:23:00")));

    [Fact]
    public void ACellWiltingByTheClockSaysSoAndWhenItStartsDying()
    {
        var now = Cst("2026-11-12T20:00:00");
        Assert.Equal("wilting by the clock: past its wilt hours",
            WindowFormat.ThirstLine(ContainerLook.Wilting, Cst("2026-11-13T19:31:00"), now, byClock: true));
        Assert.Equal("dying from ~Fri 07:31 if not watered", WindowFormat.DyingFrom(Cst("2026-11-13T07:31:00"), now));
    }

    [Fact]
    public void ACellDeadByTheClockSaysSinceWhenAndItsLastWatering()
        => Assert.Equal("Pot 208: Thavnairian Onion\ndead by the clock since Fri 19:31\nlast watered Wed 19:31",
            WindowFormat.DeadByClockHover("Pot 208", "Thavnairian Onion", Cst("2026-11-13T19:31:00"), Cst("2026-11-11T19:31:00")));
}
