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

    [Fact] // the reader's clock choice: same windows, 12h face, am/pm on the number
    public void TwelveHourClockFormatsWithAmPm()
    {
        var lo = DateTimeOffset.Parse("2026-08-14T08:45:00Z");
        try
        {
            WindowFormat.TwelveHourClock = true;
            Assert.Equal("Fri 8:45 am - 12:03 pm", WindowFormat.Range(lo, lo.AddMinutes(198)));
            Assert.Equal("Fri 8:45 am - Sat 1:15 am", WindowFormat.Range(lo, lo.AddHours(16.5)));
            Assert.Equal("Fri 8:45 am", WindowFormat.Range(lo, lo));
        }
        finally
        {
            WindowFormat.TwelveHourClock = false;
        }
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
        Assert.Equal("Fri 18:00 - 23:30", WindowFormat.Range(lo, lo.AddHours(5.5)));
    }

    [Fact]
    public void CrossDayRangeNamesBothDays()
    {
        var lo = DateTimeOffset.Parse("2026-08-14T18:00:00Z");
        Assert.Equal("Fri 18:00 - Sat 06:00", WindowFormat.Range(lo, lo.AddHours(12)));
    }

    [Fact] // the surface speaks in day-parts, never minute-precision it doesn't have
    public void CoarseSpeaksInDayParts()
    {
        var lo = DateTimeOffset.Parse("2026-08-18T13:47:00-05:00"); // Tue
        var hi = DateTimeOffset.Parse("2026-08-20T13:46:00-05:00"); // Thu
        Assert.Equal("Tue-Thu afternoon", WindowFormat.Coarse(lo, hi));
    }

    [Fact] // different parts across days spell both out - nothing collapses that isn't equal
    public void CoarseCrossDayDifferentPartsSpellsBoth()
    {
        var lo = DateTimeOffset.Parse("2026-08-16T13:47:00-05:00"); // Sun afternoon
        var hi = DateTimeOffset.Parse("2026-08-18T05:46:00-05:00"); // Tue morning
        Assert.Equal("Sun afternoon - Tue morning", WindowFormat.Coarse(lo, hi));
    }

    [Fact] // same day, same part: one phrase, no range pretending to have two ends
    public void CoarseCollapsesSameDaySamePart()
    {
        var lo = DateTimeOffset.Parse("2026-08-16T13:47:00-05:00"); // Sun
        Assert.Equal("Sun afternoon", WindowFormat.Coarse(lo, lo.AddHours(2)));
        Assert.Equal("Sun afternoon", WindowFormat.Coarse(lo, lo));
        Assert.Equal("Sun afternoon", WindowFormat.Coarse(lo, lo.AddHours(-3)));
    }

    [Fact] // same day, different part drops the second day name
    public void CoarseSameDayDifferentPartsDropsSecondDay()
    {
        var lo = DateTimeOffset.Parse("2026-08-16T13:47:00-05:00"); // Sun
        Assert.Equal("Sun afternoon-evening", WindowFormat.Coarse(lo, lo.AddHours(5)));
    }

    [Fact] // the small hours are "early morning", not a misleading "night" of the same date
    public void CoarseDayPartBoundaries()
    {
        var sun = DateTimeOffset.Parse("2026-08-16T02:00:00-05:00");
        Assert.Equal("Sun early morning", WindowFormat.Coarse(sun, sun));
        Assert.Equal("Sun morning", WindowFormat.Coarse(sun.AddHours(3), sun.AddHours(3)));    // 05:00
        Assert.Equal("Sun afternoon", WindowFormat.Coarse(sun.AddHours(10), sun.AddHours(10))); // 12:00
        Assert.Equal("Sun evening", WindowFormat.Coarse(sun.AddHours(15), sun.AddHours(15)));   // 17:00
        Assert.Equal("Sun night", WindowFormat.Coarse(sun.AddHours(19), sun.AddHours(19)));     // 21:00
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
