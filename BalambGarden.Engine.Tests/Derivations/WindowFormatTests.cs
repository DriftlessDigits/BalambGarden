using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;
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

    [Fact] // an exact stamp carries the full date - "3d ago" needs a calendar moment behind it
    public void ExactSaysTheFullDateInTheReadersClock()
    {
        var at = DateTimeOffset.Parse("2026-08-14T18:07:00Z");
        Assert.Equal("Fri 2026-08-14 18:07", WindowFormat.Exact(at));
        try
        {
            WindowFormat.TwelveHourClock = true;
            Assert.Equal("Fri 2026-08-14 6:07 pm", WindowFormat.Exact(at));
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

    [Fact] // a window whose front edge is already behind the reader clamps at now: "ripens
    // Sat night - Wed morning" with Sat gone reads like a claim about the past. The spoken
    // phrase clamps; the hover (Range) keeps the raw math.
    public void CoarseClampsAtNow()
    {
        var lo = DateTimeOffset.Parse("2026-08-15T21:30:00-05:00"); // Sat night
        var hi = DateTimeOffset.Parse("2026-08-19T05:30:00-05:00"); // Wed morning

        // now before the window: unchanged
        var before = DateTimeOffset.Parse("2026-08-15T12:00:00-05:00");
        Assert.Equal("Sat night - Wed morning", WindowFormat.Coarse(lo, hi, before));

        // now inside the window: the past edge stops being spoken
        var inside = DateTimeOffset.Parse("2026-08-16T19:00:00-05:00"); // Sun
        Assert.Equal("any time now, by Wed morning", WindowFormat.Coarse(lo, hi, inside));

        // latest lands on the reader's own day: the day name is noise
        var sameDay = DateTimeOffset.Parse("2026-08-19T02:00:00-05:00"); // Wed small hours
        Assert.Equal("any time now, by morning", WindowFormat.Coarse(lo, hi, sameDay));

        // the whole window is behind now: no future edge left to speak
        var after = DateTimeOffset.Parse("2026-08-19T12:00:00-05:00");
        Assert.Equal("any time now", WindowFormat.Coarse(lo, hi, after));
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

    // ------------------------------------------------------------- the water deadline
    // Dashboard rethink ruling 2 (2026-11-08): the water number is a deadline, "water by
    // tonight ~6:15pm", never an age. The caller converts both instants to local time.

    [Fact]
    public void WaterBySaysTodayTonightOrTheDay()
    {
        var sun = DateTimeOffset.Parse("2026-11-08T10:35:00-06:00");
        Assert.Equal("water by today ~14:05", WindowFormat.WaterBy(sun.AddHours(3.5), sun));
        Assert.Equal("water by tonight ~18:15", WindowFormat.WaterBy(DateTimeOffset.Parse("2026-11-08T18:15:00-06:00"), sun));
        Assert.Equal("water by ~Mon 06:15", WindowFormat.WaterBy(DateTimeOffset.Parse("2026-11-09T06:15:00-06:00"), sun));
        Assert.Equal("water now", WindowFormat.WaterBy(sun.AddMinutes(-1), sun));
    }

    [Fact]
    public void WaterByFollowsTheReadersClock()
    {
        var sun = DateTimeOffset.Parse("2026-11-08T10:35:00-06:00");
        try
        {
            WindowFormat.TwelveHourClock = true;
            Assert.Equal("water by tonight ~6:15 pm", WindowFormat.WaterBy(DateTimeOffset.Parse("2026-11-08T18:15:00-06:00"), sun));
        }
        finally
        {
            WindowFormat.TwelveHourClock = false;
        }
    }

    [Fact] // the countdown beside the deadline; past it, how far over
    public void CountdownSaysHowLongIsLeft()
    {
        Assert.Equal("in 4h 45m", WindowFormat.Countdown(Now.AddHours(4.75), Now));
        Assert.Equal("in 25m", WindowFormat.Countdown(Now.AddMinutes(25.5), Now));
        Assert.Equal("in 2d 3h", WindowFormat.Countdown(Now.AddHours(51.5), Now));
        Assert.Equal("3h over", WindowFormat.Countdown(Now.AddHours(-3.2), Now));
        Assert.Equal("now", WindowFormat.Countdown(Now.AddSeconds(20), Now));
    }

    [Fact] // ruling 22: colour is never the only channel - a bed's hover says its water state in words
    public void ABedsHoverSaysItsWaterStateInWords()
    {
        var sun = DateTimeOffset.Parse("2026-11-08T10:35:00-06:00");
        var dies = DateTimeOffset.Parse("2026-11-10T14:00:00-06:00");
        Assert.Equal("water soon: past three quarters of its wilt hours", WindowFormat.ThirstLine(ContainerLook.WaterSoon, dies, sun));
        Assert.Equal("wilting: past its wilt hours", WindowFormat.ThirstLine(ContainerLook.Wilting, dies, sun));
        Assert.Equal("dying: withers ~Tue 14:00 unless watered", WindowFormat.ThirstLine(ContainerLook.Dying, dies, sun));
        Assert.Equal("dying: past its wither hours - it may already be lost", WindowFormat.ThirstLine(ContainerLook.Dying, sun.AddHours(-1), sun));
        Assert.Null(WindowFormat.ThirstLine(ContainerLook.Growing, dies, sun));
    }

    [Fact] // the headline and the lane's drop name the card's thirst in a word
    public void ACardsThirstHasAWord()
    {
        Assert.Equal("water soon", WindowFormat.Thirst(ContainerLook.WaterSoon));
        Assert.Equal("wilting", WindowFormat.Thirst(ContainerLook.Wilting));
        Assert.Equal("dying", WindowFormat.Thirst(ContainerLook.Dying));
        Assert.Null(WindowFormat.Thirst(ContainerLook.Growing));
    }

    [Fact] // spec ruling 4: "pot wilting (seen 2h ago)" away from the room; in the room the note reads as before slice 19
    public void ThePotWiltNoteSaysWhenItWasSeenAwayFromTheRoom()
    {
        Assert.Equal("pot wilting (seen 2h ago)", WindowFormat.PotsWilting(1, Now.AddHours(-2), Now));
        Assert.Equal("4 pots wilting (seen 12m ago)", WindowFormat.PotsWilting(4, Now.AddMinutes(-12), Now));
        Assert.Equal("pot wilting - water now", WindowFormat.PotsWilting(1, seenAt: null, Now));
        Assert.Equal("4 pots wilting - water now", WindowFormat.PotsWilting(4, seenAt: null, Now));
    }

    [Fact] // spec ruling 23 (ruled 2026-11-10: "I don't really care about which pot, just where that pot is"): a kind and a place, never a number
    public void TheCardNamesWhatNeedsWaterByKindAndPlace()
    {
        Assert.Equal("a pot", WindowFormat.Containers(new ContainerCount(0, 1, null)));
        Assert.Equal("a bed in patch 2", WindowFormat.Containers(new ContainerCount(1, 0, 1)));
        Assert.Equal("4 pots", WindowFormat.Containers(new ContainerCount(0, 4, null)));
        Assert.Equal("2 beds, 1 pot", WindowFormat.Containers(new ContainerCount(2, 1, null)));
        Assert.Equal("1 bed, 3 pots", WindowFormat.Containers(new ContainerCount(1, 3, 0)));
        Assert.Equal("a bed", WindowFormat.Containers(new ContainerCount(1, 0, null)));   // no patch known: say less, never "patch "
    }

    [Fact] // a pot's hover says when the game itself showed it wilting
    public void APotsHoverSaysWhereItsWiltWasSeen()
    {
        Assert.Equal("game shows it wilting", WindowFormat.ShownWilt(seenAt: null, Now));
        Assert.Equal("seen wilting 12m ago", WindowFormat.ShownWilt(Now.AddMinutes(-12), Now));
    }

    [Fact]
    public void TheDeadlineHoverSaysWhatWiltsFirst()
    {
        var at = DateTimeOffset.Parse("2026-11-08T18:14:00-06:00");
        Assert.Equal("A pot wilts at Sun 18:14", WindowFormat.FirstToWilt(new ContainerCount(0, 1, null), at));
        Assert.Equal("A bed in patch 1 wilts at Sun 18:14", WindowFormat.FirstToWilt(new ContainerCount(1, 0, 0), at));
        Assert.Equal("4 pots wilt at Sun 18:14", WindowFormat.FirstToWilt(new ContainerCount(0, 4, null), at));
    }

    [Fact]
    public void TheWorstLineNamesItsKind()
    {
        Assert.Equal("worst: 4 pots, wilting", WindowFormat.Worst(new ContainerCount(0, 4, null), ContainerLook.Wilting));
        Assert.Equal("worst: a bed in patch 1, dying", WindowFormat.Worst(new ContainerCount(1, 0, 0), ContainerLook.Dying));
        Assert.Null(WindowFormat.Worst(ContainerCount.None, ContainerLook.Growing));
    }

    [Fact] // the water bar's hover: whose clock it is and where it starts
    public void TheWaterBarSaysWhoseClockItIs()
    {
        Assert.Equal("A pot: sown 1d ago, no watering recorded",
            WindowFormat.WaterFrom(new ContainerCount(0, 1, null), fromSowing: true, Now.AddHours(-25), Now));
        Assert.Equal("A bed in patch 1: watered 20h ago",
            WindowFormat.WaterFrom(new ContainerCount(1, 0, 0), fromSowing: false, Now.AddHours(-20), Now));
    }

    [Fact]
    public void TheWaterChipAndTheNoWateringNoteCountBedsAndPots()
    {
        Assert.Equal("4 pots due within 24h of the deadline", WindowFormat.WaterDue(new ContainerCount(0, 4, null), "24h"));
        Assert.Equal("2 beds, 1 pot due within 24h of the deadline", WindowFormat.WaterDue(new ContainerCount(2, 1, null), "24h"));
        Assert.Equal("nothing due within 24h of the deadline", WindowFormat.WaterDue(ContainerCount.None, "24h"));
        Assert.Equal("3 pots with no watering recorded", WindowFormat.NoWatering(new ContainerCount(0, 3, null)));
        Assert.Equal("2 beds, 3 pots with no watering recorded", WindowFormat.NoWatering(new ContainerCount(2, 3, null)));
        Assert.Equal("1 bed with no watering recorded", WindowFormat.NoWatering(new ContainerCount(1, 0, 4)));
    }

    [Fact] // rethink slice 23: a dead plant's hover says the game's words and when it was seen
    public void ADeadPlantsHoverSaysBeyondHopeAndWhenItWasSeen()
    {
        Assert.Equal("Pot 126: Thavnairian Onion, dead\nbeyond hope (seen 7m ago)",
            WindowFormat.DeadHover("Pot 126", "Thavnairian Onion", Now.AddMinutes(-7), Now));
        Assert.Equal("Bed 3: Krakka Root, dead\nbeyond hope (seen 2d ago)",
            WindowFormat.DeadHover("Bed 3", "Krakka Root", Now.AddHours(-50), Now));
        Assert.Equal("Pot 126: Thavnairian Onion, dead\nbeyond hope",
            WindowFormat.DeadHover("Pot 126", "Thavnairian Onion", seenAt: null, Now));
    }

    [Fact] // rethink slice 23: the replant chip counts the dead with the empty, and says which
    public void TheReplantChipSaysWhatIsDeadAndWhatIsEmpty()
    {
        Assert.Equal("emptied by a harvest or an empty read, nothing sown since",
            WindowFormat.ReplantDue(replant: 2, ContainerCount.None));
        Assert.Equal("4 pots dead - clear and sow",
            WindowFormat.ReplantDue(replant: 4, new ContainerCount(0, 4, null)));
        Assert.Equal("emptied by a harvest or an empty read, nothing sown since\nA bed in patch 2 dead - clear and sow",
            WindowFormat.ReplantDue(replant: 2, new ContainerCount(1, 0, 1)));
        Assert.Equal("A pot dead - clear and sow", WindowFormat.ReplantDue(replant: 1, new ContainerCount(0, 1, null)));
    }

    [Fact] // a card with no water clock says why, dead plants among the reasons
    public void TheNoDeadlineHoverNamesItsReasons()
        => Assert.Equal("no deadline: ripe, flowers, dead, or nothing recorded", WindowFormat.NoDeadline);

    [Fact] // the lanes' day heads read the same on every machine, whatever its culture
    public void DayHeadIsTheWeekdayAndTheDayOfTheMonth()
        => Assert.Equal("Mon 9", WindowFormat.DayHead(DateTimeOffset.Parse("2026-11-09T00:00:00-06:00")));

    [Fact] // rethink slice 21: a pot on the floor says it was seen fine, never "watered" (spec ruling 23: "seen fine 10m ago")
    public void APotOnTheFloorSaysSeenFine()
    {
        Assert.Equal("seen fine 10m ago", WindowFormat.SeenFine(Now.AddMinutes(-10), Now));
        Assert.Equal("seen fine 10m ago - no watering recorded",
            WindowFormat.SeenFineClock(Now.AddMinutes(-10), lastTended: null, Now));
        Assert.Equal("seen fine 10m ago - last watering recorded 2d ago",
            WindowFormat.SeenFineClock(Now.AddMinutes(-10), Now.AddDays(-2), Now));
        Assert.Equal("A pot: seen fine 10m ago",
            WindowFormat.WaterFrom(new ContainerCount(0, 1, null), fromSowing: false, Now.AddMinutes(-10), Now, seenFine: true));
    }

    [Fact] // rethink slice 21: the act, not the mechanism ("best thing to do is rewater to be safe", ruled 2026-11-10)
    public void APotOnTheFloorSaysToWaterToBeSafe()
    {
        var fine = new SeenFineDeadline(Now.AddMinutes(-10), MayBeDying: false);
        var dying = new SeenFineDeadline(Now.AddHours(-12), MayBeDying: true);
        var pot = new ContainerCount(0, 1, null);

        Assert.Equal(("water to be safe", "seen fine 10m ago"), WindowFormat.Headline(Now.AddMinutes(-10), fine, Now));
        Assert.Equal(("water now", "seen fine 12h ago"), WindowFormat.Headline(Now.AddHours(-12), dying, Now));
        Assert.Equal(("water now", "1h over"), WindowFormat.Headline(Now.AddHours(-1), seenFine: null, Now));
        Assert.Equal("A pot may wilt any time", WindowFormat.HeadlineHover(pot, Now, fine));
        Assert.Equal("2 pots may be dying", WindowFormat.HeadlineHover(new ContainerCount(0, 2, null), Now, dying));
        Assert.Equal("water to be safe", WindowFormat.DropHead(Now.AddMinutes(-10), fine, Now));
        Assert.Equal("may wilt any time - water to be safe", WindowFormat.SeenFineThirstLine(ContainerLook.WaterSoon));
        Assert.Equal("may wilt any time - water to be safe", WindowFormat.SeenFineThirstLine(ContainerLook.Growing));
        Assert.Equal("may be dying - water now", WindowFormat.SeenFineThirstLine(ContainerLook.Dying));
    }

    // ------------------------------------------------------------------ the Yields tab (rethink slice 7)
    // Each expected line is the plan mock's text (2026-11-11), typed here, never rebuilt with the
    // code's own formatter.

    [Fact]
    public void TheYieldsHeaderSaysWhenTheRecordBegins()
        => Assert.Equal("SINCE OCT 16", WindowFormat.YieldsSince(DateTimeOffset.Parse("2026-10-16T16:30:00-05:00")));

    [Fact]
    public void TheCrossRateReadsAsAPercentWithItsCountOnHover()
    {
        Assert.Equal("87%", WindowFormat.Percent(new CrossRate(40, 46).Percent));
        Assert.Equal("40 of 46 harvests gave a cross seed", WindowFormat.CrossRateHover(new CrossRate(40, 46)));
        Assert.Equal("3 of 4 harvests gave a cross seed\nUnder 10 harvests: too few to lean on",
            WindowFormat.CrossRateHover(new CrossRate(3, 4)));
        Assert.Equal("1 of 1 harvest gave a cross seed\nUnder 10 harvests: too few to lean on",
            WindowFormat.CrossRateHover(new CrossRate(1, 1)));
    }

    [Fact]
    public void HarvestsWithNoSowingOnRecordAreNamedOnTheHarvestedHover()
    {
        var since = DateTimeOffset.Parse("2026-10-16T16:30:00-05:00");
        Assert.Equal("25 more harvests have no sowing on record (sown before Oct 16);\ntheir items and cross seeds still count",
            WindowFormat.UnmatchedHover(25, since, isPot: false));
        Assert.Equal("1 more harvest has no sowing on record (sown before Oct 16);\nits items and cross seed still count",
            WindowFormat.UnmatchedHover(1, since, isPot: false));
    }

    [Fact] // review 2026-11-11: a pot's key can move, so its reason is not known; and pots never cross
    public void APotsUnmatchedHarvestsClaimNoReasonAndNoCrossSeed()
    {
        var since = DateTimeOffset.Parse("2026-10-16T16:30:00-05:00");
        Assert.Equal("1 more harvest has no sowing on record;\nits items still count",
            WindowFormat.UnmatchedHover(1, since, isPot: true));
        Assert.Equal("3 more harvests have no sowing on record;\ntheir items still count",
            WindowFormat.UnmatchedHover(3, since, isPot: true));
    }

    [Fact]
    public void ACropWithNoCrossHeardAndAnUnknownSeedSayLittle()
    {
        Assert.Equal("-", WindowFormat.NoCrossHeard);
        Assert.Equal("-", WindowFormat.NotHeard);
        Assert.Equal("seed 7728", WindowFormat.SeedName(null, 7728));
        Assert.Equal("Sun Lemon", WindowFormat.SeedName("Sun Lemon", 7728));
    }

    [Fact]
    public void GatheredSaysHowManyHarvestsItRestsOn()
    {
        Assert.Equal("from the 48 harvests with a yield recorded", WindowFormat.GatheredHover(48));
        Assert.Equal("from the 1 harvest with a yield recorded", WindowFormat.GatheredHover(1));
    }

    [Fact]
    public void CrossSeedsReadByShareWithTheirCountsOnHover()
    {
        SeedShare[] mirror = [new(7750, 19, 48), new(7728, 11, 27), new(8169, 10, 25)];
        string Name(uint seed) => seed switch { 7750 => "Royal Kukuru", 7728 => "Sun Lemon", _ => "Curiel Root" };

        Assert.Equal("Royal Kukuru 48% · Sun Lemon 27% · Curiel Root 25%", WindowFormat.SeedShares(mirror, Name));
        Assert.Equal("Royal Kukuru 19 · Sun Lemon 11 · Curiel Root 10", WindowFormat.SeedCounts(mirror, Name));
    }

    // ------------------------------------------------------------------ the History tab (rethink slice 9)
    // Every expected string is the plan mock's literal text (Bench out/rethink9/history-plan-mock.png).

    private static readonly EstateKey FcEstate = new(340, 11, 56);

    private static HistoryEntry HistoryRow(HistoryKind kind, string by = "jw9cdj", bool pot = false,
        int patch = 2, int slot = 6, int? qty = null, int? seedQty = null, uint? seedItem = null)
        => new("jw9cdj:c33aty:1", DateTimeOffset.Parse("2026-11-08T00:18:00Z"), kind, 49, FcEstate, pot,
            patch, pot ? 0 : slot, by, SyncSource.Chain, qty is { } q ? [new SyncYield(8165, q)] : null, seedQty, seedItem);

    [Fact]
    public void TheHistoryCountSaysHowManyAndSince()
    {
        var since = DateTimeOffset.Parse("2026-10-16T16:30:00-05:00");
        Assert.Equal("527 on record since Oct 16", WindowFormat.HistoryCount(527, 527, since, filtered: false));
        Assert.Equal("1 on record since Oct 16", WindowFormat.HistoryCount(1, 1, since, filtered: false));
        Assert.Equal("36 of 527 match", WindowFormat.HistoryCount(36, 527, since, filtered: true));
        Assert.Equal("1 of 527 matches", WindowFormat.HistoryCount(1, 527, since, filtered: true));
        Assert.Equal("0 of 527 match", WindowFormat.HistoryCount(0, 527, since, filtered: true));
    }

    [Fact] // an empty record and a filter that hides every row are two different silences
    public void TheHistoryEmptyStatesSayWhichEmptyItIs()
    {
        Assert.Equal("Nothing planted or harvested on record yet.", WindowFormat.NoHistory);
        Assert.Equal("No plants or harvests match these filters.", WindowFormat.NoHistoryMatch);
    }

    [Fact]
    public void TheHistoryDayHeadingAndTimeReadAsTheMock()
    {
        var at = DateTimeOffset.Parse("2026-11-07T18:18:00-06:00");
        Assert.Equal("SAT NOV 7", WindowFormat.HistoryDay(at));
        Assert.Equal("18:18", WindowFormat.HistoryTime(at));
        try
        {
            WindowFormat.TwelveHourClock = true;
            Assert.Equal("6:18 pm", WindowFormat.HistoryTime(at));
        }
        finally
        {
            WindowFormat.TwelveHourClock = false;
        }
    }

    [Fact]
    public void AHistoryRowSaysWhatWhereAndWhoseHand()
    {
        Assert.Equal("planted", WindowFormat.HistoryAct(HistoryKind.Plant));
        Assert.Equal("harvested", WindowFormat.HistoryAct(HistoryKind.Harvest));

        Assert.Equal("FC Estate, patch 3, bed 7", WindowFormat.HistoryWhere("FC Estate", HistoryRow(HistoryKind.Plant)));
        Assert.Equal("Mama's Place, pot", WindowFormat.HistoryWhere("Mama's Place", HistoryRow(HistoryKind.Plant, pot: true, patch: 131)));
        Assert.Equal("Ward 4 Plot 51, patch 1, bed 2", WindowFormat.HistoryWhere("Ward 4 Plot 51", HistoryRow(HistoryKind.Plant, patch: 0, slot: 1)));

        Assert.Equal("household", WindowFormat.HistoryHand(HistoryRow(HistoryKind.Plant)));
        Assert.Equal("you", WindowFormat.HistoryHand(HistoryRow(HistoryKind.Plant, by: Hand.OwnSlug)));
    }

    [Fact] // the conductor's check, 2026-11-11: an own act, as the receipt log hands it to the history, reads "you"
    public void AnOwnActFromTheReceiptLogReadsYou()
    {
        var receipt = new LoggedReceipt(7, "eraaaa", SyncKind.Harvest, SyncSource.Chain, DateTimeOffset.Parse("2026-11-07T16:47:00Z"),
            FcEstate, false, PatchOrdinal: 2, BedSlot: 6, PatchId: 1234, PotKey: -1, Data: new SyncData(Species: 49));
        Assert.Equal("you", WindowFormat.HistoryHand(HistoryEntry.FromOwn(receipt, "k7f3qx")!));
    }

    [Fact] // a plant has no yield; "-" = not heard; a number = heard
    public void AHistoryYieldCellSaysHeardOrNot()
    {
        Assert.Equal(("", false), WindowFormat.HistoryYield(HistoryRow(HistoryKind.Plant)));
        Assert.Equal(("-", true), WindowFormat.HistoryYield(HistoryRow(HistoryKind.Harvest)));
        Assert.Equal(("9", false), WindowFormat.HistoryYield(HistoryRow(HistoryKind.Harvest, qty: 9)));
    }

    [Fact] // the cross seed's three states on a bed harvest; nothing on a plant or a pot (pots never cross)
    public void AHistoryCrossCellSaysCrossedMissedOrNotHeard()
    {
        string Name(uint seed) => WindowFormat.SeedName(seed == 7728 ? "Sun Lemon" : null, seed);

        Assert.Equal(("", false), WindowFormat.HistoryCross(HistoryRow(HistoryKind.Plant), Name));
        Assert.Equal(("", false), WindowFormat.HistoryCross(HistoryRow(HistoryKind.Harvest, pot: true, qty: 1), Name));
        Assert.Equal(("-", true), WindowFormat.HistoryCross(HistoryRow(HistoryKind.Harvest, qty: 9), Name));
        Assert.Equal(("no cross", true), WindowFormat.HistoryCross(HistoryRow(HistoryKind.Harvest, qty: 9, seedQty: 0), Name));
        Assert.Equal(("Sun Lemon", false), WindowFormat.HistoryCross(HistoryRow(HistoryKind.Harvest, seedQty: 1, seedItem: 7728), Name));
        Assert.Equal(("Sun Lemon x2", false), WindowFormat.HistoryCross(HistoryRow(HistoryKind.Harvest, seedQty: 2, seedItem: 7728), Name));
        Assert.Equal(("seed 12345", false), WindowFormat.HistoryCross(HistoryRow(HistoryKind.Harvest, seedQty: 1, seedItem: 12345), Name));
        Assert.Equal(("-", true), WindowFormat.HistoryCross(HistoryRow(HistoryKind.Harvest, seedQty: 1), Name)); // a seed with no item: which seed was not heard
    }

    [Fact]
    public void TheHistoryFiltersReadAsTheMock()
    {
        Assert.Equal("All estates", WindowFormat.AllEstates);
        Assert.Equal("All crops", WindowFormat.AllCrops);
        Assert.Equal(["All hands", "You", "Household"], new[] { WindowFormat.HandChoice(null), WindowFormat.HandChoice(true), WindowFormat.HandChoice(false) });
        Assert.Equal(["Plants and harvests", "Plants", "Harvests"],
            new[] { WindowFormat.KindChoice(null), WindowFormat.KindChoice(HistoryKind.Plant), WindowFormat.KindChoice(HistoryKind.Harvest) });
    }

    // The settings window's foreign hands (rethink slice 10, spec ruling 15; on screen "Household hands"). Each expected line is
    // the plan's literal text, never rebuilt with the format's own helpers.

    private static SeenHand Seen(int onRecord, HistoryKind kind = HistoryKind.Plant, string estate = "FC Estate",
        string localAt = "2026-11-07T18:18:00-06:00")
        => new(new Hand("jw9cdj"), onRecord, new HistoryRowLine(HistoryRow(kind), estate, DateTimeOffset.Parse(localAt)));

    private static string Crop(ushort species) => species == 49 ? "Krakka Root" : "?";

    [Fact]
    public void AForeignHandSaysHowManyAreItsOnRecordAndTheNewest()
    {
        Assert.Equal("382 plants and harvests on record, the last: planted Krakka Root at FC Estate, Nov 7 18:18.",
            WindowFormat.ForeignHandLine(Seen(382), Crop));
        Assert.Equal("1 plant or harvest on record: harvested Krakka Root at Mama's Place, Nov 17 09:05.",
            WindowFormat.ForeignHandLine(Seen(1, HistoryKind.Harvest, "Mama's Place", "2026-11-17T09:05:00-06:00"), Crop));
        try
        {
            WindowFormat.TwelveHourClock = true;
            Assert.Equal("382 plants and harvests on record, the last: planted Krakka Root at FC Estate, Nov 7 6:18 pm.",
                WindowFormat.ForeignHandLine(Seen(382), Crop));
        }
        finally
        {
            WindowFormat.TwelveHourClock = false;
        }
    }

    [Fact] // a hand only the ledger holds has no act to name
    public void AHouseholdHandWithNothingInTheHistorySaysSo()
        => Assert.Equal("Nothing in the History tab yet.", WindowFormat.ForeignHandLine(new SeenHand(new Hand("p1ant0"), 0, null), Crop));

    [Fact]
    public void TheForeignHandsSectionReadsAsThePlan()
    {
        Assert.Equal("household", WindowFormat.Household);
        Assert.Equal("Household hands", WindowFormat.ForeignHandsHeading);
        Assert.Equal("Name the other installs that have gardened with you.", WindowFormat.HandsIntro);
        Assert.Equal("No household hands heard yet. Each install that gardens with you through household sync shows up here to be named.",
            WindowFormat.NoForeignHands);
        Assert.Equal("Your name for this hand. Left empty, it reads \"household\".\n"
            + "Kept in this install's settings only, never sent.\n"
            + "Changing the household phrase clears every name.", WindowFormat.AliasHover);
        Assert.Equal("The tag its receipts carry: a random code, never a name.", WindowFormat.SlugHover);
    }
}
