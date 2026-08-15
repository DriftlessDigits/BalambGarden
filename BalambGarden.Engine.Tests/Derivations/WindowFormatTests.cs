using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

public class WindowFormatTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T18:00:00Z");

    [Fact] // provenance is carried by text, never by colour alone (spec)
    public void MarksAreDistinctText()
    {
        Assert.Equal("[A]", WindowFormat.Mark(Provenance.Anchored));
        Assert.Equal("[~]", WindowFormat.Mark(Provenance.Bracketed));
        Assert.Equal("[?]", WindowFormat.Mark(Provenance.Estimated));
        Assert.Equal(3, new[]
        {
            WindowFormat.MarkMeaning(Provenance.Anchored),
            WindowFormat.MarkMeaning(Provenance.Bracketed),
            WindowFormat.MarkMeaning(Provenance.Estimated),
        }.Distinct().Count());
    }

    [Fact] // a zero-width window is a time, not a range pretending to have two ends
    public void ZeroWidthWindowPrintsOneTime()
    {
        var at = DateTimeOffset.Parse("2026-08-14T18:00:00Z");
        Assert.Equal("Fri 18:00", WindowFormat.Range(at, at));
        Assert.Equal("Fri 18:00", WindowFormat.Range(at, at.AddHours(-3)));
    }

    [Fact]
    public void SameDayRangeDropsTheSecondDayName()
    {
        var lo = DateTimeOffset.Parse("2026-08-14T18:00:00Z");
        Assert.Equal("Fri 18:00-23:30", WindowFormat.Range(lo, lo.AddHours(5.5)));
    }

    [Fact]
    public void CrossDayRangeNamesBothDays()
    {
        var lo = DateTimeOffset.Parse("2026-08-14T18:00:00Z");
        Assert.Equal("Fri 18:00-Sat 06:00", WindowFormat.Range(lo, lo.AddHours(12)));
    }

    [Fact] // clock skew must never print an age from the future
    public void AgeClampsAtJustNow()
    {
        Assert.Equal("just now", WindowFormat.Ago(Now.AddMinutes(30), Now));
        Assert.Equal("just now", WindowFormat.Ago(Now, Now));
        Assert.Equal("5m ago", WindowFormat.Ago(Now.AddMinutes(-5), Now));
        Assert.Equal("3h ago", WindowFormat.Ago(Now.AddHours(-3), Now));
        Assert.Equal("2d ago", WindowFormat.Ago(Now.AddDays(-2), Now));
    }

    [Fact] // "-" (does not apply) and "?" (we don't know) are different silences
    public void PotWaterRendersAsNotApplicableNotUnknown()
    {
        Assert.Equal("-", WindowFormat.Water(WaterState.NotApplicable));
        Assert.Equal("?", WindowFormat.Water(WaterState.Unknown));
        Assert.Equal("danger", WindowFormat.Water(WaterState.Danger));
    }
}
