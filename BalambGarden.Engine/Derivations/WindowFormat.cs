using System.Globalization;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>
/// How a derived number says what kind of claim it is making. Pure string shaping, kept
/// in the Engine because the honest-surface rules (a provenance marker is TEXT, never
/// colour alone; an age never runs backwards; a pot's water column is "-", not "?") are
/// behaviour worth pinning, not layout.
/// </summary>
public static class WindowFormat
{
    /// <summary>Provenance markers are text on purpose (spec: glyphs, not colour alone) -
    /// a colour-blind reader and a screenshot both have to carry the claim.</summary>
    public static string Mark(Provenance provenance) => provenance switch
    {
        Provenance.Anchored => "[A]",
        Provenance.Bracketed => "[~]",
        _ => "[?]",
    };

    /// <summary>The hover line that says what the marker is claiming, in plain terms.</summary>
    public static string MarkMeaning(Provenance provenance) => provenance switch
    {
        Provenance.Anchored =>
            "anchored: this bed was planted under watch, so the clock starts at a real receipt",
        Provenance.Bracketed =>
            "bracketed: two sightings bound the stage flip - the window is as wide as the gap between visits",
        _ =>
            "estimated: one sighting only, so the window is the whole stage band",
    };

    /// <summary>How the reader tells time. The app sets this from its config at load and
    /// on change; the Engine only formats. False (24h) is the default so the choice is
    /// always an explicit act of the app, never a formatting surprise.</summary>
    public static bool TwelveHourClock;

    private static string Clock(DateTimeOffset t, bool withDay)
    {
        var day = withDay ? t.ToString("ddd ", CultureInfo.InvariantCulture) : "";
        return TwelveHourClock
            ? day + t.ToString("h:mm", CultureInfo.InvariantCulture) + (t.Hour < 12 ? " am" : " pm")
            : day + t.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>A window in the reader's own clock - the caller converts to local time
    /// first, because the Engine has no business knowing where the player lives. A
    /// zero-width window prints as the single time it actually is, never as a range
    /// pretending to have two ends.</summary>
    public static string Range(DateTimeOffset earliest, DateTimeOffset latest)
    {
        var lo = Clock(earliest, withDay: true);
        if (latest <= earliest)
            return lo;

        var hi = Clock(latest, withDay: earliest.Date != latest.Date);
        return $"{lo} - {hi}";
    }

    /// <summary>A window as day-parts ("Tue afternoon-Thu afternoon") - the surface's
    /// voice for a claim the model only holds to within hours (ruling 2026-08-16:
    /// minute-precision display overstates the instrument; the exact range moves to the
    /// hover). Same collapse rules as <see cref="Range"/>: zero-width speaks once,
    /// same-day drops the second day name.</summary>
    public static string Coarse(DateTimeOffset earliest, DateTimeOffset latest)
    {
        var loDay = earliest.ToString("ddd", CultureInfo.InvariantCulture);
        var loPart = DayPart(earliest);
        if (latest <= earliest)
            return $"{loDay} {loPart}";

        var hiDay = latest.ToString("ddd", CultureInfo.InvariantCulture);
        var hiPart = DayPart(latest);

        if (earliest.Date == latest.Date)
            return loPart == hiPart ? $"{loDay} {loPart}" : $"{loDay} {loPart}-{hiPart}";

        // Shared part across days says the part once: "Tue-Thu afternoon". Different
        // parts get breathing room around the dash - two multi-word phrases crammed
        // together read as one.
        return loPart == hiPart
            ? $"{loDay}-{hiDay} {loPart}"
            : $"{loDay} {loPart} - {hiDay} {hiPart}";
    }

    /// <summary>Clamp-at-now voice: a front edge the reader has already lived through is
    /// not spoken as if it were still coming ("Sat night - Wed morning" with Sat gone
    /// reads like a claim about the past). Only the SPOKEN phrase clamps - the hover keeps
    /// <see cref="Range"/>'s raw math, so the model's actual window is always one hover
    /// away. A latest on the reader's own day drops its day name; a window entirely
    /// behind now has no future edge left to speak.</summary>
    public static string Coarse(DateTimeOffset earliest, DateTimeOffset latest, DateTimeOffset now)
    {
        if (now < earliest)
            return Coarse(earliest, latest);
        if (now >= latest)
            return "any time now";

        var day = latest.Date == now.Date ? "" : latest.ToString("ddd ", CultureInfo.InvariantCulture);
        return $"any time now, by {day}{DayPart(latest)}";
    }

    /// <summary>The small hours are "early morning", never a "night" that reads as the
    /// same date's evening.</summary>
    private static string DayPart(DateTimeOffset t) => t.Hour switch
    {
        < 5 => "early morning",
        < 12 => "morning",
        < 17 => "afternoon",
        < 21 => "evening",
        _ => "night",
    };

    /// <summary>A day line's head on the timeline lanes, "Mon 9". Caller converts to local time
    /// first.</summary>
    public static string DayHead(DateTimeOffset midnight)
        => midnight.ToString("ddd d", CultureInfo.InvariantCulture);

    /// <summary>An exact past moment for hovers - the full date, because a "3d ago" only
    /// resolves to a real calendar moment with the date said outright. Caller converts to
    /// local time first, same as <see cref="Range"/>.</summary>
    public static string Exact(DateTimeOffset t)
        => t.ToString("ddd yyyy-MM-dd ", CultureInfo.InvariantCulture) + Clock(t, withDay: false);

    /// <summary>Data age, beside every number the dashboard shows. Clock skew clamps to
    /// "just now" - a negative age would be the surface claiming to have seen the future.</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var span = now - at;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours}h ago"
            : $"{(int)span.TotalDays}d ago";
    }

    /// <summary>The water deadline as the card says it (dashboard rethink ruling 2): "water by
    /// today ~14:05" or "tonight" (the evening and night day-parts) on the reader's own day, "water by ~Mon 06:15" on
    /// another, and "water now" once it has passed. The "~" says the crop table's wilt hours are
    /// community data, not a game readout. Caller converts both instants to local time.</summary>
    public static string WaterBy(DateTimeOffset deadline, DateTimeOffset now)
    {
        if (deadline <= now)
            return "water now";
        if (deadline.Date == now.Date)
            return $"water by {(DayPart(deadline) is "evening" or "night" ? "tonight" : "today")} ~{Clock(deadline, withDay: false)}";
        return $"water by ~{Clock(deadline, withDay: true)}";
    }

    /// <summary>The countdown beside a deadline: "in 4h 45m", "in 2d 3h", "in 25m"; under a
    /// minute "now"; past it, how far over ("3h over"). Two units at most - the third would
    /// claim a precision the wilt table does not have.</summary>
    public static string Countdown(DateTimeOffset deadline, DateTimeOffset now)
    {
        var span = deadline - now;
        var abs = span.Duration();
        if (abs < TimeSpan.FromMinutes(1))
            return "now";
        var over = span < TimeSpan.Zero;
        var text = abs.TotalDays >= 1 ? $"{(int)abs.TotalDays}d {abs.Hours}h"
            : abs.TotalHours >= 1 ? over ? $"{(int)abs.TotalHours}h" : $"{(int)abs.TotalHours}h {abs.Minutes}m"
            : $"{abs.Minutes}m";
        return over ? $"{text} over" : $"in {text}";
    }

    /// <summary>The water state as a word. "-" means the state makes no claim for this row
    /// (pot wilt is unverified - the twins labs will say; until then pots assert nothing);
    /// "?" means we genuinely do not know. Two different silences, two different marks.</summary>
    public static string Water(WaterState state) => state switch
    {
        WaterState.Watered => "watered",
        WaterState.Due => "due",
        WaterState.Overdue => "overdue",
        WaterState.Danger => "danger",
        WaterState.NotApplicable => "-",
        _ => "?",
    };

    /// <summary>A card's thirst as the Garden tab's hovers name it ("worst bed here: wilting"):
    /// the colour map's words, so the colour is never the only channel. Null where the beds ask
    /// nothing.</summary>
    public static string? Thirst(ContainerLook thirst) => thirst switch
    {
        ContainerLook.WaterSoon => "water soon",
        ContainerLook.Wilting => "wilting",
        ContainerLook.Dying => "dying",
        _ => null,
    };

    /// <summary>A bed's water state in its hover, said with what it means: water soon, wilting, or
    /// dying with when it withers (<paramref name="diesAt"/>; once that has passed, that it may
    /// already be lost). Null where the bed asks nothing. Caller converts both instants to local
    /// time.</summary>
    public static string? ThirstLine(ContainerLook look, DateTimeOffset? diesAt, DateTimeOffset now) => look switch
    {
        ContainerLook.WaterSoon => "water soon: past three quarters of its wilt hours",
        ContainerLook.Wilting => "wilting: past its wilt hours",
        ContainerLook.Dying => diesAt is { } dies && dies > now
            ? $"dying: withers ~{Clock(dies, withDay: true)} unless watered"
            : "dying: past its wither hours - it may already be lost",
        _ => null,
    };

    /// <summary>A card's pot-wilting note (spec ruling 4, rethink slice 19). On the live read in the
    /// room (<paramref name="seenAt"/> null) it says "water now"; away it says when the game last
    /// showed one wilting: "pot wilting (seen 2h ago)", "4 pots wilting (seen 12m ago)".</summary>
    public static string PotsWilting(int count, DateTimeOffset? seenAt, DateTimeOffset now)
    {
        var pots = count == 1 ? "pot wilting" : $"{count} pots wilting";
        return seenAt is { } seen ? $"{pots} (seen {Ago(seen, now)})" : $"{pots} - water now";
    }

    /// <summary>What a card line is about, by kind and place, never by number (spec ruling 23,
    /// ruled 2026-11-10: "I don't really care about which pot, just where that pot is"): "a pot", "a bed
    /// in patch 2"; more than one, the counts: "4 pots", "2 beds, 1 pot".</summary>
    public static string Containers(ContainerCount count) => count.Total == 1
        ? count.Pots == 1 ? "a pot" : count.Patch is { } patch ? $"a bed in patch {patch + 1}" : "a bed"
        : Counts(count);

    /// <summary>The deadline's hover: "A pot wilts at Sun 6:14 pm" (ruled 2026-11-10). Caller
    /// converts <paramref name="at"/> to local time.</summary>
    public static string FirstToWilt(ContainerCount count, DateTimeOffset at)
        => $"{Capital(Containers(count))} {(count.Total == 1 ? "wilts" : "wilt")} at {Clock(at, withDay: true)}";

    /// <summary>The card's worst look and what wears it: "worst: 4 pots, wilting". Null while
    /// nothing asks for water.</summary>
    public static string? Worst(ContainerCount count, ContainerLook thirst)
        => Thirst(thirst) is { } word && count.Total > 0 ? $"worst: {Containers(count)}, {word}" : null;

    /// <summary>The water bar's hover: whose clock it is and where it starts. "A pot: sown 1d ago, no
    /// watering recorded", "A bed in patch 1: watered 20h ago", and on the seen-fine floor "A pot:
    /// seen fine 10m ago" (never "watered": a bound, not a receipt).</summary>
    public static string WaterFrom(ContainerCount count, bool fromSowing, DateTimeOffset from, DateTimeOffset now,
        bool seenFine = false)
        => $"{Capital(Containers(count))}: "
           + (seenFine ? SeenFine(from, now)
               : fromSowing ? $"sown {Ago(from, now)}, no watering recorded" : $"watered {Ago(from, now)}");

    /// <summary>When the room last showed a pot fine (rethink slice 21, spec ruling 23): "seen fine 10m ago".</summary>
    public static string SeenFine(DateTimeOffset at, DateTimeOffset now) => $"seen fine {Ago(at, now)}";

    /// <summary>A pot cell's clock line on the seen-fine floor: what the clock rests on, and what the
    /// plugin recorded under it. "seen fine 10m ago - no watering recorded", "seen fine 10m ago -
    /// last watering recorded 2d ago".</summary>
    public static string SeenFineClock(DateTimeOffset seenFine, DateTimeOffset? lastTended, DateTimeOffset now)
        => $"{SeenFine(seenFine, now)} - "
           + (lastTended is { } tended ? $"last watering recorded {Ago(tended, now)}" : "no watering recorded");

    /// <summary>The headline of a card whose deadline rests on the seen-fine floor: the act, since
    /// the deadline is the look itself (ruled 2026-11-10: "best thing to do is rewater to be safe").</summary>
    public const string WaterToBeSafe = "water to be safe";

    /// <summary>A pot cell's water line on the seen-fine floor, in place of "water soon: past three
    /// quarters of its wilt hours".</summary>
    public const string MayWiltLine = "may wilt any time - water to be safe";

    /// <summary>A pot cell's water line on the seen-fine floor from its dying point, the earliest it
    /// could be dying.</summary>
    public const string MayBeDyingLine = "may be dying - water now";

    /// <summary>A pot cell's water line on the seen-fine floor: <see cref="MayBeDyingLine"/> from the
    /// floor's dying point, else <see cref="MayWiltLine"/> (its wilting band, or the room's live read
    /// showing it fine).</summary>
    public static string SeenFineThirstLine(ContainerLook look)
        => look == ContainerLook.Dying ? MayBeDyingLine : MayWiltLine;

    /// <summary>The card's headline and the dim words beside it. Normally the deadline and its
    /// countdown ("water by tonight ~7:44 pm", "in 27m"); on the seen-fine floor, the act and when the
    /// pots were seen ("water to be safe", "seen fine 10m ago"; "water now" once they may be dying).
    /// Caller converts <paramref name="by"/> and <paramref name="now"/> to local time.</summary>
    public static (string Text, string Aside) Headline(DateTimeOffset by, SeenFineDeadline? seenFine, DateTimeOffset now)
        => seenFine is null
            ? (WaterBy(by, now), Countdown(by, now))
            : (seenFine.MayBeDying ? "water now" : WaterToBeSafe, SeenFine(seenFine.SeenAt, now));

    /// <summary>The headline's hover: what wilts first and when ("A pot wilts at Sun 6:14 pm"); on the
    /// seen-fine floor, "A pot may wilt any time" or, from its dying point, "A pot may be dying".
    /// Caller converts <paramref name="by"/> to local time.</summary>
    public static string HeadlineHover(ContainerCount count, DateTimeOffset by, SeenFineDeadline? seenFine)
        => seenFine is null ? FirstToWilt(count, by)
            : $"{Capital(Containers(count))} may {(seenFine.MayBeDying ? "be dying" : "wilt any time")}";

    /// <summary>The first line of the water drop's hover: the deadline and its countdown, or on the
    /// seen-fine floor the headline's own words. Caller converts both instants to local time.</summary>
    public static string DropHead(DateTimeOffset by, SeenFineDeadline? seenFine, DateTimeOffset now)
        => seenFine is null ? $"{WaterBy(by, now)} ({Countdown(by, now)})" : Headline(by, seenFine, now).Text;

    /// <summary>A pot cell's hover when the game itself showed it wilting: "game shows it wilting"
    /// on the live read in its room (<paramref name="seenAt"/> null), "seen wilting 12m ago" from
    /// the sighting its record keeps.</summary>
    public static string ShownWilt(DateTimeOffset? seenAt, DateTimeOffset now)
        => seenAt is { } seen ? $"seen wilting {Ago(seen, now)}" : "game shows it wilting";

    /// <summary>A dead plant's hover (rethink slice 23): what it was, that it is dead, and the game's
    /// own words from its inspect line ("This crop is beyond hope.") with when the read that showed
    /// it was taken: "Pot 126: Thavnairian Onion, dead" / "beyond hope (seen 7m ago)". Without a
    /// read time it says no time rather than invent one.</summary>
    public static string DeadHover(string name, string crop, DateTimeOffset? seenAt, DateTimeOffset now)
        => $"{name}: {crop}, dead\nbeyond hope" + (seenAt is { } seen ? $" (seen {Ago(seen, now)})" : "");

    /// <summary>The replant chip's hover. <paramref name="replant"/> is the chip's count, the emptied
    /// containers and the <paramref name="dead"/> ones together; it says the emptied ones, then the
    /// dead ones to clear first ("4 pots dead - clear and sow"), each line only when it has
    /// something to say.</summary>
    public static string ReplantDue(int replant, ContainerCount dead)
        => string.Join("\n", new[]
        {
            replant > dead.Total || dead.Total == 0 ? "emptied by a harvest or an empty read, nothing sown since" : null,
            dead.Total > 0 ? $"{Capital(Containers(dead))} dead - clear and sow" : null,
        }.OfType<string>());

    /// <summary>The headline's hover on a card with no water clock: why there is no deadline.</summary>
    public const string NoDeadline = "no deadline: ripe, flowers, dead, or nothing recorded";

    /// <summary>The water chip's hover: "4 pots due within 24h of the deadline".</summary>
    public static string WaterDue(ContainerCount count, string span)
        => $"{(count.Total == 0 ? "nothing" : Containers(count))} due within {span} of the deadline";

    /// <summary>The card's note for beds and pots whose deadline rests on the sowing: "3 pots with
    /// no watering recorded", "2 beds, 3 pots with no watering recorded".</summary>
    public static string NoWatering(ContainerCount count) => $"{Counts(count)} with no watering recorded";

    private static string Counts(ContainerCount count)
        => string.Join(", ", new[] { Plural(count.Beds, "bed"), Plural(count.Pots, "pot") }.OfType<string>());

    private static string? Plural(int n, string word) => n switch
    {
        0 => null,
        1 => $"1 {word}",
        _ => $"{n} {word}s",
    };

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // ------------------------------------------------------------------ the Yields tab (rethink slice 7)

    /// <summary>The Yields tab with no history yet.</summary>
    public const string NoYields = "Nothing planted or harvested on record yet.";

    /// <summary>The Yields tab's header, the record's first day in the reader's time: "SINCE OCT 16".</summary>
    public static string YieldsSince(DateTimeOffset since) => $"SINCE {ShortDay(since).ToUpperInvariant()}";

    public static string Percent(int percent) => $"{percent}%";

    /// <summary>The Crossed cell of a bed crop whose cross was never heard either way.</summary>
    public const string NoCrossHeard = NotHeard;

    /// <summary>A value the plugin never heard either way: a harvest's yield, its cross seed.</summary>
    public const string NotHeard = "-";

    /// <summary>A cross seed by its crop's name; an item the crop table does not know by its id.</summary>
    public static string SeedName(string? cropName, uint seedItem) => cropName ?? $"seed {seedItem}";

    /// <summary>The cross rate's hover: its count, and a caution when it rests on too few harvests.</summary>
    public static string CrossRateHover(CrossRate rate)
        => $"{rate.Crossed} of {Harvests(rate.Heard)} gave a cross seed"
           + (rate.Firm ? "" : $"\nUnder {GardenBoard.FirmRateHarvests} harvests: too few to lean on");

    /// <summary>The Harvested hover when harvests found no sowing on record: they never count as
    /// harvested, but their items (and, on beds, their cross seeds) still count (ruled 2026-11-11).
    /// A bed's reason is the history's start; a pot's key can move, so a pot claims no reason.</summary>
    public static string UnmatchedHover(int unmatched, DateTimeOffset since, bool isPot)
    {
        var one = unmatched == 1;
        var head = $"{unmatched} more {(one ? "harvest has" : "harvests have")} no sowing on record"
                   + (isPot ? "" : $" (sown before {ShortDay(since)})");
        var counts = isPot ? "items" : one ? "items and cross seed" : "items and cross seeds";
        return $"{head};\n{(one ? "its" : "their")} {counts} still count";
    }

    /// <summary>The Gathered hover: how many harvests the total rests on.</summary>
    public static string GatheredHover(int from) => $"from the {Harvests(from)} with a yield recorded";

    private static string Harvests(int n) => n == 1 ? "1 harvest" : $"{n} harvests";

    /// <summary>"Oct 16", the same on every machine.</summary>
    private static string ShortDay(DateTimeOffset at) => at.ToString("MMM d", CultureInfo.InvariantCulture);

    /// <summary>A crop's cross seeds by share: "Sun Lemon 55% · Curiel Root 45%".</summary>
    public static string SeedShares(IEnumerable<SeedShare> seeds, Func<uint, string> name)
        => string.Join(" · ", seeds.Select(s => $"{name(s.SeedItem)} {s.Percent}%"));

    /// <summary>The seed shares' hover: the harvests behind each, "Sun Lemon 17 · Curiel Root 14".</summary>
    public static string SeedCounts(IEnumerable<SeedShare> seeds, Func<uint, string> name)
        => string.Join(" · ", seeds.Select(s => $"{name(s.SeedItem)} {s.Harvests}"));

    // ------------------------------------------------------------------ the History tab (rethink slice 9)

    /// <summary>The History tab with nothing on record. The history is read at plugin start, before
    /// the window draws, so this always means the record is empty, never that it is still loading.</summary>
    public const string NoHistory = "Nothing planted or harvested on record yet.";

    /// <summary>The History tab when the filters hide every row of a record that has some.</summary>
    public const string NoHistoryMatch = "No plants or harvests match these filters.";

    /// <summary>The line over the rows: "527 on record since Oct 16" with no filter set; with one,
    /// how many of the whole record it lets through ("36 of 527 match", "1 of 527 matches").</summary>
    public static string HistoryCount(int shown, int total, DateTimeOffset since, bool filtered)
        => filtered
            ? $"{shown} of {total} {(shown == 1 ? "matches" : "match")}"
            : $"{total} on record since {ShortDay(since)}";

    /// <summary>A day's heading over its rows: "SAT NOV 7". Caller converts to local time first.</summary>
    public static string HistoryDay(DateTimeOffset day)
        => day.ToString("ddd MMM d", CultureInfo.InvariantCulture).ToUpperInvariant();

    /// <summary>A row's time of day in the reader's clock: "6:18 pm". Caller converts to local time first.</summary>
    public static string HistoryTime(DateTimeOffset at) => Clock(at, withDay: false);

    /// <summary>What happened: "planted" or "harvested".</summary>
    public static string HistoryAct(HistoryKind kind) => kind == HistoryKind.Plant ? "planted" : "harvested";

    /// <summary>Where: "FC Estate, patch 3, bed 7"; a pot by its place alone, "Mama's Place, pot" (spec
    /// ruling 23: "I don't really care about which pot, just where that pot is").</summary>
    public static string HistoryWhere(string estate, HistoryEntry row)
        => row.IsPot ? $"{estate}, pot" : $"{estate}, patch {row.PatchOrdinal + 1}, bed {row.BedSlot + 1}";

    /// <summary>Whose hand: "you" for this install's, "household" for every other until the labels land.</summary>
    public static string HistoryHand(HistoryEntry row) => row.IsOwn ? "you" : Household;

    /// <summary>What every hand but this install's reads without a label.</summary>
    public const string Household = "household";

    /// <summary>The Yield cell and whether it draws dim: blank on a plant; the items gathered when
    /// the harvest's yield was heard, else "-" (not heard).</summary>
    public static (string Text, bool Dim) HistoryYield(HistoryEntry row)
        => row.Kind == HistoryKind.Plant ? ("", false)
            : row.Yields is not { Count: > 0 } ? (NotHeard, true)
            : (row.YieldQty.ToString(CultureInfo.InvariantCulture), false);

    /// <summary>The Cross seed cell and whether it draws dim: blank on a plant and on a pot (pots
    /// never cross); on a bed harvest the seed it gave ("Sun Lemon", "Sun Lemon x2"), "no cross"
    /// when the cross was heard and no seed came, "-" when it was not heard either way.</summary>
    public static (string Text, bool Dim) HistoryCross(HistoryEntry row, Func<uint, string> seedName)
    {
        if (row.Kind == HistoryKind.Plant || row.IsPot)
            return ("", false);
        if (row.SeedQty is not { } qty)
            return (NotHeard, true);
        if (qty == 0)
            return ("no cross", true);
        if (row.SeedItem is not { } seed)
            return (NotHeard, true);
        return (qty == 1 ? seedName(seed) : $"{seedName(seed)} x{qty}", false);
    }

    /// <summary>The estate filter's first line, which clears it.</summary>
    public const string AllEstates = "All estates";

    /// <summary>The crop filter's first line, which clears it.</summary>
    public const string AllCrops = "All crops";

    /// <summary>The hand filter's lines: "All hands", "You" (this install), "Household" (every other hand).</summary>
    public static string HandChoice(bool? own) => own switch
    {
        null => "All hands",
        true => "You",
        false => "Household",
    };

    /// <summary>The kind filter's lines: "Plants and harvests", "Plants", "Harvests".</summary>
    public static string KindChoice(HistoryKind? kind) => kind switch
    {
        null => "Plants and harvests",
        HistoryKind.Plant => "Plants",
        _ => "Harvests",
    };

    // ------------------------------------------------------------------ the settings window's foreign hands (rethink slice 10; on screen "Household hands")

    /// <summary>The section's heading.</summary>
    public const string ForeignHandsHeading = "Household hands";

    /// <summary>The line under the heading when it lists a hand.</summary>
    public const string HandsIntro = "Name the others who garden with you.";

    /// <summary>The section with no foreign hand on record. True with sync off as well: nothing has
    /// been heard.</summary>
    public const string NoForeignHands =
        "No household hands heard yet. Each install that gardens with you through household sync shows up here to be named.";

    /// <summary>The alias box's hover.</summary>
    public const string AliasHover = "Your name for this hand. Left empty, it reads \"household\".\n"
        + "Kept in this install's settings only, never sent.\n"
        + "Changing the household phrase clears every name.";

    /// <summary>The slug's hover (on screen, "the tag").</summary>
    public const string SlugHover = "The tag its receipts carry: a random code, never a name.";

    /// <summary>What tells one hand from another: how many of the history's rows are its and the newest
    /// of them, "382 plants and harvests on record, the last: planted Krakka Root at FC Estate, Nov 7
    /// 6:18 pm."; with one, "1 plant or harvest on record: ..."; a hand only the ledger holds, "No plant
    /// or harvest on record yet." Says "on record", never "last" alone: the history can lag the garden.</summary>
    public static string ForeignHandLine(SeenHand hand, Func<ushort, string> speciesName)
    {
        if (hand.Last is not { } last)
            return "No plant or harvest on record yet.";
        var count = hand.HistoryRows == 1 ? "1 plant or harvest on record:" : $"{hand.HistoryRows} plants and harvests on record, the last:";
        return $"{count} {HistoryAct(last.Entry.Kind)} {speciesName(last.Entry.Species)} at {last.EstateName}, "
               + $"{ShortDay(last.LocalAt)} {Clock(last.LocalAt, withDay: false)}.";
    }
}
