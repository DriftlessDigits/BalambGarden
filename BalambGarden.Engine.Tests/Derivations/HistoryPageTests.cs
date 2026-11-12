using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>The History tab through the garden board's seam (rethink slice 9, spec ruling 13): the
/// history, a filter and the ledger's estates go in; the tab's lines, its count and its filter
/// lists come out. Nothing here reaches inside the board.</summary>
public class HistoryPageTests
{
    private static readonly DomainTables T = DomainTables.Load();

    // Species indexes from Data/SpeciesIndex.json.
    private const ushort Mirror = 17, Krakka = 49, Onion = 50;

    private static readonly EstateKey Fc = new(340, 11, 56), Mama = new(340, 11, 31), Unnamed = new(641, 3, 50), Unknown = new(641, 3, 49);

    /// <summary>Six hours behind UTC, no daylight saving: the household's November clock.</summary>
    private static readonly TimeZoneInfo Cst = TimeZoneInfo.CreateCustomTimeZone("test-cst", TimeSpan.FromHours(-6), "test-cst", "test-cst");

    private static readonly IReadOnlyList<EstateRecord> Estates =
    [
        new() { Key = Fc, Nickname = "FC Estate" },
        new() { Key = Mama, Nickname = "Mama's Place" },
        new() { Key = Unnamed },
    ];

    private static int seq;

    private static HistoryEntry Row(string at, HistoryKind kind, ushort species = Krakka, EstateKey? estate = null,
        string by = "jw9cdj", bool pot = false)
        => new($"{by}:eraaaa:{++seq}", DateTimeOffset.Parse(at), kind, species, estate ?? Fc, pot, 2, pot ? 0 : 6,
            by, SyncSource.Chain, null);

    private static GardenHistory History(params HistoryEntry[] rows)
    {
        var history = new GardenHistory();
        foreach (var row in rows)
            history.Record(row);
        return history;
    }

    private static readonly Dictionary<string, string> NoAliases = [];

    private static HistoryPage Page(GardenHistory history, HistoryFilter? filter = null, HandPick? hand = null,
        Dictionary<string, string>? aliases = null)
        => GardenBoard.History(history, filter ?? new HistoryFilter(), hand, aliases ?? NoAliases, Estates, T, Cst);

    private static string[] Ids(HistoryPage page)
        => page.Days.SelectMany(d => d.Rows).Select(l => l.Entry.Id).ToArray();

    /// <summary>The page's days and rows in order: a day as its midnight, a row as its id.</summary>
    private static string[] DaysAndRows(HistoryPage page, string dayFormat = "yyyy-MM-dd zzz")
        => page.Days.SelectMany(d => d.Rows.Select(r => r.Entry.Id).Prepend(d.Day.ToString(dayFormat))).ToArray();

    [Fact] // an empty record: no lines, nothing to date it from
    public void AnEmptyHistoryHasNoLinesAndNoStart()
    {
        var page = Page(new GardenHistory());
        Assert.Null(page.Since);
        Assert.Equal(0, page.Total);
        Assert.Equal(0, page.Shown);
        Assert.Empty(page.Days);
        Assert.Empty(page.Estates);
        Assert.Empty(page.Species);
    }

    [Fact] // newest first, a day heading wherever the reader's own date changes
    public void RowsRunNewestFirstUnderTheReadersDays()
    {
        var oct16 = Row("2026-10-16T21:30:00Z", HistoryKind.Harvest);       // Oct 16, 3:30 pm local
        var nov7Plant = Row("2026-11-08T00:18:30Z", HistoryKind.Plant);     // Nov 7, 6:18 pm local
        var nov7Harvest = Row("2026-11-08T00:18:10Z", HistoryKind.Harvest); // Nov 7, 6:18 pm local
        var nov8 = Row("2026-11-08T06:30:00Z", HistoryKind.Plant);          // Nov 8, 12:30 am local
        var page = Page(History(oct16, nov7Harvest, nov7Plant, nov8));

        Assert.Equal(DateTimeOffset.Parse("2026-10-16T21:30:00Z"), page.Since);
        Assert.Equal(4, page.Total);
        Assert.Equal(4, page.Shown);

        Assert.Equal(
        [
            "2026-11-08 -06:00", nov8.Id,
            "2026-11-07 -06:00", nov7Plant.Id, nov7Harvest.Id,
            "2026-10-16 -06:00", oct16.Id,
        ], DaysAndRows(page));
    }

    [Fact] // across the clock change each heading keeps its own day's offset, and each row its own time
    public void DayHeadingsAndRowTimesFollowTheClockChange()
    {
        var central = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time"); // daylight time ends Sun 2026-11-01 2am
        var sat = Row("2026-10-31T23:00:00Z", HistoryKind.Plant); // Sat Oct 31, 6:00 pm daylight time
        var sun = Row("2026-11-01T23:00:00Z", HistoryKind.Plant); // Sun Nov 1, 5:00 pm standard time
        var page = GardenBoard.History(History(sat, sun), new HistoryFilter(), null, NoAliases, Estates, T, central);

        Assert.Equal(["2026-11-01T00:00-05:00", "2026-11-01T17:00-06:00", "2026-10-31T00:00-05:00", "2026-10-31T18:00-05:00"],
            page.Days.SelectMany(d => d.Rows.Select(r => r.LocalAt.ToString("yyyy-MM-ddTHH:mmzzz"))
                .Prepend(d.Day.ToString("yyyy-MM-ddTHH:mmzzz"))).ToArray());
    }

    [Fact] // each row carries its estate's name; an estate with no name set, or none in the ledger, reads its label
    public void RowsNameTheirEstate()
    {
        var page = Page(History(
            Row("2026-11-07T10:00:00Z", HistoryKind.Plant, estate: Mama),
            Row("2026-11-07T11:00:00Z", HistoryKind.Plant, estate: Unnamed),
            Row("2026-11-07T12:00:00Z", HistoryKind.Plant, estate: Unknown)));

        Assert.Equal(["Ward 4 Plot 50", "Ward 4 Plot 51", "Mama's Place"], page.Days.SelectMany(d => d.Rows).Select(l => l.EstateName).ToArray());
    }

    [Fact] // the four filters through the existing history filter; the count is over the whole record
    public void FiltersNarrowTheRowsByEstateCropHandAndKind()
    {
        var a = Row("2026-11-07T10:00:00Z", HistoryKind.Harvest, Mirror, Fc);
        var b = Row("2026-11-07T11:00:00Z", HistoryKind.Harvest, Mirror, Fc, by: Hand.OwnSlug);
        var c = Row("2026-11-07T12:00:00Z", HistoryKind.Plant, Mirror, Fc);
        var d = Row("2026-11-07T13:00:00Z", HistoryKind.Harvest, Krakka, Fc);
        var e = Row("2026-11-07T14:00:00Z", HistoryKind.Harvest, Mirror, Mama);
        var history = History(a, b, c, d, e);

        var page = Page(history, new HistoryFilter(Estate: Fc, Species: Mirror, Own: false, Kind: HistoryKind.Harvest));
        Assert.Equal([a.Id], Ids(page));
        Assert.Equal(1, page.Shown);
        Assert.Equal(5, page.Total);
        Assert.True(page.Filtered);
        Assert.False(Page(history).Filtered);

        Assert.Equal([e.Id, d.Id, b.Id, a.Id], Ids(Page(history, new HistoryFilter(Kind: HistoryKind.Harvest))));
        Assert.Equal([b.Id], Ids(Page(history, new HistoryFilter(Own: true))));
        Assert.Equal([e.Id], Ids(Page(history, new HistoryFilter(Estate: Mama))));
        Assert.Equal([d.Id], Ids(Page(history, new HistoryFilter(Species: Krakka))));
    }

    [Fact] // a filter that hides every row: no lines, the record's size still known
    public void AFilterThatMatchesNothingLeavesNoLines()
    {
        var page = Page(History(Row("2026-11-07T10:00:00Z", HistoryKind.Plant)), new HistoryFilter(Kind: HistoryKind.Harvest));
        Assert.Empty(page.Days);
        Assert.Equal(0, page.Shown);
        Assert.Equal(1, page.Total);
        Assert.NotNull(page.Since);
    }

    [Fact] // the estate and crop lists offer only what the record holds, by name, whatever the filter
    public void TheFilterListsHoldWhatTheRecordHoldsByName()
    {
        var page = Page(History(
                Row("2026-11-07T10:00:00Z", HistoryKind.Plant, Onion, Mama, pot: true),
                Row("2026-11-07T11:00:00Z", HistoryKind.Plant, Mirror, Fc),
                Row("2026-11-07T12:00:00Z", HistoryKind.Plant, Krakka, Unnamed),
                Row("2026-11-07T13:00:00Z", HistoryKind.Harvest, Mirror, Fc)),
            new HistoryFilter(Estate: Fc));

        Assert.Equal(["FC Estate", "Mama's Place", "Ward 4 Plot 51"], page.Estates.Select(e => e.Name).ToArray());
        Assert.Equal([Fc, Mama, Unnamed], page.Estates.Select(e => e.Key).ToArray());
        Assert.Equal([Krakka, Mirror, Onion], page.Species.ToArray()); // Krakka Root, Mirror Apple, Thavnairian Onion
    }

    // ---- hands by alias (rethink slice 11, spec ruling 15; on screen a "nickname")

    [Fact] // the hand filter: You, then each alias on record by name, then Household while a household hand with no alias is on record
    public void TheHandListIsYouThenEachAliasThenHousehold()
    {
        // Recorded, and newest first too, in the opposite of the list's order, so the order is the rule's.
        var history = History(
            Row("2026-11-07T12:00:00Z", HistoryKind.Plant, by: "q8m2zz"),
            Row("2026-11-07T11:00:00Z", HistoryKind.Plant, by: "zz11aa"),
            Row("2026-11-07T10:00:00Z", HistoryKind.Plant, by: "jw9cdj"),
            Row("2026-11-07T09:00:00Z", HistoryKind.Plant, by: Hand.OwnSlug));
        var aliases = new Dictionary<string, string> { ["zz11aa"] = "Mog", ["jw9cdj"] = "Kupo" };

        Assert.Equal([HandPick.You, HandPick.Named("Kupo"), HandPick.Named("Mog"), HandPick.Household],
            Page(history, aliases: aliases).Hands.ToArray());

        aliases["q8m2zz"] = "Kupo"; // two hands, one alias: one line; nobody left unnamed: no Household
        Assert.Equal([HandPick.You, HandPick.Named("Kupo"), HandPick.Named("Mog")], Page(history, aliases: aliases).Hands.ToArray());

        Assert.Equal([HandPick.You, HandPick.Household], Page(history).Hands.ToArray());
        Assert.Equal([HandPick.You], Page(History(Row("2026-11-07T12:00:00Z", HistoryKind.Plant, by: Hand.OwnSlug))).Hands.ToArray());
    }

    [Fact] // each line of the hand filter shows exactly the rows whose Hand cell reads it
    public void TheHandFilterShowsTheRowsThatReadItsWord()
    {
        var mine = Row("2026-11-07T09:00:00Z", HistoryKind.Plant, by: Hand.OwnSlug);
        var kupo = Row("2026-11-07T10:00:00Z", HistoryKind.Plant, by: "jw9cdj");
        var unnamed = Row("2026-11-07T11:00:00Z", HistoryKind.Plant, by: "q8m2zz");
        var kupoHarvest = Row("2026-11-07T12:00:00Z", HistoryKind.Harvest, by: "jw9cdj");
        var history = History(mine, kupo, unnamed, kupoHarvest);
        var aliases = new Dictionary<string, string> { ["jw9cdj"] = "Kupo" };

        Assert.Equal([kupoHarvest.Id, kupo.Id], Ids(Page(history, hand: HandPick.Named("Kupo"), aliases: aliases)));
        Assert.Equal([unnamed.Id], Ids(Page(history, hand: HandPick.Household, aliases: aliases)));
        Assert.Equal([mine.Id], Ids(Page(history, hand: HandPick.You, aliases: aliases)));
        Assert.Equal([kupoHarvest.Id], Ids(Page(history, new HistoryFilter(Kind: HistoryKind.Harvest), HandPick.Named("Kupo"), aliases)));

        var page = Page(history, hand: HandPick.Household, aliases: aliases);
        Assert.True(page.Filtered);
        Assert.Equal(HandPick.Household, page.Hand);
        Assert.Equal(4, page.Total);
        Assert.Equal(1, page.Shown);
    }

    // ---- folding days (rethink slice 24, ruling 13 amended 2026-11-11: "Build C, folding days, newest day open?" - "yes")

    /// <summary>The lines as the tab draws them: a day heading as "day count open|folded", a row as its id.</summary>
    private static string[] Drawn(IReadOnlyList<HistoryLine> lines) => lines.Select(l => l switch
    {
        HistoryDayLine d => $"{d.Day:yyyy-MM-dd} {d.Count} {(d.Open ? "open" : "folded")}",
        HistoryRowLine r => r.Entry.Id,
        _ => "?",
    }).ToArray();

    private static DateTimeOffset Midnight(string day) => DateTimeOffset.Parse($"{day}T00:00:00-06:00");

    [Fact] // the tab opens on the newest day open and every older day folded to its heading and count
    public void TheTabOpensOnTheNewestDayWithTheRestFolded()
    {
        // Recorded oldest first, so newest-first is the rule's order.
        var oct31a = Row("2026-10-31T16:00:00Z", HistoryKind.Plant);  // Sat Oct 31, 10:00 am local
        var oct31b = Row("2026-10-31T17:00:00Z", HistoryKind.Harvest);
        var nov4 = Row("2026-11-04T23:00:00Z", HistoryKind.Harvest);  // Wed Nov 4, 5:00 pm local
        var nov7a = Row("2026-11-07T16:00:00Z", HistoryKind.Plant);   // Sat Nov 7
        var nov7b = Row("2026-11-07T17:00:00Z", HistoryKind.Harvest);
        var page = Page(History(oct31a, oct31b, nov4, nov7a, nov7b));

        var open = GardenBoard.HistoryOpenOnArrival(page);
        Assert.Equal([Midnight("2026-11-07")], open.ToArray());
        Assert.Equal(
        [
            "2026-11-07 2 open", nov7b.Id, nov7a.Id,
            "2026-11-04 1 folded",
            "2026-10-31 2 folded",
        ], Drawn(GardenBoard.HistoryLines(page, open)));
    }

    [Fact] // an older day opened shows exactly its rows, newest first; the days together hold every row once
    public void AnOlderDayOpenHoldsExactlyItsRows()
    {
        var oct31a = Row("2026-10-31T16:00:00Z", HistoryKind.Plant);
        var oct31b = Row("2026-10-31T17:00:00Z", HistoryKind.Harvest);
        var nov4 = Row("2026-11-04T23:00:00Z", HistoryKind.Harvest);
        var nov7 = Row("2026-11-07T16:00:00Z", HistoryKind.Plant);
        var page = Page(History(oct31a, oct31b, nov4, nov7));

        Assert.Equal(
        [
            "2026-11-07 1 folded",
            "2026-11-04 1 folded",
            "2026-10-31 2 open", oct31b.Id, oct31a.Id,
        ], Drawn(GardenBoard.HistoryLines(page, new HashSet<DateTimeOffset> { Midnight("2026-10-31") })));

        Assert.Equal([nov7.Id, nov4.Id, oct31b.Id, oct31a.Id], page.Days.SelectMany(d => d.Rows).Select(r => r.Entry.Id).ToArray());
        Assert.Equal(page.Shown, page.Days.Sum(d => d.Rows.Count));
        Assert.Equal([nov7.Id, nov4.Id, oct31b.Id, oct31a.Id],
            GardenBoard.HistoryLines(page, page.Days.Select(d => d.Day).ToHashSet()).OfType<HistoryRowLine>().Select(r => r.Entry.Id).ToArray());
    }

    [Fact] // under a filter a day is listed only while it holds a match, its count the matches; an open day stays open
    public void UnderAFilterOnlyDaysWithAMatchAreListedAndCountTheirMatches()
    {
        var oct31Mirror = Row("2026-10-31T16:00:00Z", HistoryKind.Harvest, Mirror);
        var oct31Krakka = Row("2026-10-31T17:00:00Z", HistoryKind.Harvest, Krakka);
        var nov4Krakka = Row("2026-11-04T23:00:00Z", HistoryKind.Harvest, Krakka);
        var nov4AtMama = Row("2026-11-04T22:00:00Z", HistoryKind.Plant, Krakka, Mama);
        var nov7Mirror = Row("2026-11-07T16:00:00Z", HistoryKind.Plant, Mirror);
        var nov7Mirror2 = Row("2026-11-07T17:00:00Z", HistoryKind.Harvest, Mirror);
        var history = History(oct31Mirror, oct31Krakka, nov4AtMama, nov4Krakka, nov7Mirror, nov7Mirror2);
        var open = new HashSet<DateTimeOffset> { Midnight("2026-10-31") }; // opened under All crops

        var mirror = Page(history, new HistoryFilter(Species: Mirror));
        Assert.Equal(
        [
            "2026-11-07 2 folded",
            "2026-10-31 1 open", oct31Mirror.Id,
        ], Drawn(GardenBoard.HistoryLines(mirror, open)));
        Assert.All(mirror.Days, d => Assert.NotEmpty(d.Rows));

        var harvests = Page(history, new HistoryFilter(Kind: HistoryKind.Harvest));
        Assert.Equal(["2026-11-07 1 folded", "2026-11-04 1 folded", "2026-10-31 2 open", oct31Krakka.Id, oct31Mirror.Id],
            Drawn(GardenBoard.HistoryLines(harvests, open)));

        var atMama = Page(history, new HistoryFilter(Estate: Mama));
        Assert.Equal(["2026-11-04 1 open", nov4AtMama.Id],
            Drawn(GardenBoard.HistoryLines(atMama, new HashSet<DateTimeOffset> { Midnight("2026-11-04") })));
        Assert.Equal(["2026-11-04 2 folded"], Drawn(GardenBoard.HistoryLines(Page(history, new HistoryFilter(Species: Krakka, Kind: null, Estate: null)), open))
            .Where(s => s.StartsWith("2026-11-04")).ToArray());

        // A filter that matches nothing lists no day at all: the tab says "No plants or harvests match these filters."
        var none = Page(history, new HistoryFilter(Species: Onion));
        Assert.Empty(none.Days);
        Assert.Empty(GardenBoard.HistoryLines(none, open));
        Assert.Empty(GardenBoard.HistoryOpenOnArrival(none));

        // An empty record: no day, nothing to open; the tab says "Nothing planted or harvested on record yet."
        var empty = Page(new GardenHistory());
        Assert.Empty(empty.Days);
        Assert.Empty(GardenBoard.HistoryOpenOnArrival(empty));
    }

    [Fact] // the hand filter (a nickname, Household, You) narrows each day to the rows whose Hand cell reads it
    public void TheHandFilterNarrowsEachDay()
    {
        var oct31Kupo = Row("2026-10-31T16:00:00Z", HistoryKind.Harvest, by: "jw9cdj");
        var oct31Mine = Row("2026-10-31T17:00:00Z", HistoryKind.Harvest, by: Hand.OwnSlug);
        var oct31Other = Row("2026-10-31T18:00:00Z", HistoryKind.Harvest, by: "q8m2zz");
        var nov7Mine = Row("2026-11-07T16:00:00Z", HistoryKind.Plant, by: Hand.OwnSlug);
        var history = History(oct31Kupo, oct31Mine, oct31Other, nov7Mine);
        var aliases = new Dictionary<string, string> { ["jw9cdj"] = "Kupo" };
        var open = new HashSet<DateTimeOffset> { Midnight("2026-10-31") };

        Assert.Equal(["2026-10-31 1 open", oct31Kupo.Id],
            Drawn(GardenBoard.HistoryLines(Page(history, hand: HandPick.Named("Kupo"), aliases: aliases), open)));
        Assert.Equal(["2026-10-31 1 open", oct31Other.Id],
            Drawn(GardenBoard.HistoryLines(Page(history, hand: HandPick.Household, aliases: aliases), open)));
        Assert.Equal(["2026-11-07 1 folded", "2026-10-31 1 open", oct31Mine.Id],
            Drawn(GardenBoard.HistoryLines(Page(history, hand: HandPick.You, aliases: aliases), open)));
    }

    [Fact] // a row landing never moves the reader: an older open day stays open, its line found one further down, a new day folded
    public void ARowLandingLeavesTheReaderWhereTheyWere()
    {
        var oct31a = Row("2026-10-31T16:00:00Z", HistoryKind.Plant);
        var oct31b = Row("2026-10-31T17:00:00Z", HistoryKind.Harvest);
        var nov7 = Row("2026-11-07T16:00:00Z", HistoryKind.Plant);
        var history = History(oct31a, oct31b, nov7);
        var open = new HashSet<DateTimeOffset> { Midnight("2026-11-07"), Midnight("2026-10-31") };

        var before = GardenBoard.HistoryLines(Page(history), open);
        var reading = before.OfType<HistoryRowLine>().Single(r => r.Entry.Id == oct31a.Id);
        var at = GardenBoard.HistoryLineIndex(before, reading);
        Assert.Equal(4, at); // Nov 7, its row, Oct 31, oct31b, oct31a

        // A row lands on the open newest day: one line above the reader.
        var nov7Late = Row("2026-11-07T20:00:00Z", HistoryKind.Harvest);
        history.Record(nov7Late);
        var after = GardenBoard.HistoryLines(Page(history), open);
        Assert.Equal(at + 1, GardenBoard.HistoryLineIndex(after, reading));
        Assert.Equal(["2026-11-07 2 open", nov7Late.Id, nov7.Id, "2026-10-31 2 open", oct31b.Id, oct31a.Id], Drawn(after));

        // A row lands on a new day: it arrives folded, one heading above the reader, nothing else moves.
        var nov11 = Row("2026-11-11T20:00:00Z", HistoryKind.Plant);
        history.Record(nov11);
        var later = GardenBoard.HistoryLines(Page(history), open);
        Assert.Equal(at + 2, GardenBoard.HistoryLineIndex(later, reading));
        Assert.Equal("2026-11-11 1 folded", Drawn(later)[0]);

        // A heading is found by its day; a line filtered away is not found.
        Assert.Equal(1, GardenBoard.HistoryLineIndex(later, new HistoryDayLine(Midnight("2026-11-07"), 0, false)));
        Assert.Equal(-1, GardenBoard.HistoryLineIndex(GardenBoard.HistoryLines(Page(history, new HistoryFilter(Kind: HistoryKind.Harvest)), open), reading));
    }

    [Fact] // the days are the reader's: midnight in their zone, and both 1:30 ams of the clock change on the same day
    public void DaysFoldAtTheReadersMidnightAcrossTheClockChange()
    {
        var central = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time"); // daylight time ends Sun 2026-11-01 2am
        var sat2350 = Row("2026-11-01T04:50:00Z", HistoryKind.Plant);    // Sat Oct 31, 11:50 pm daylight time
        var sun0010 = Row("2026-11-01T05:10:00Z", HistoryKind.Plant);    // Sun Nov 1, 12:10 am daylight time
        var sun0130Cdt = Row("2026-11-01T06:30:00Z", HistoryKind.Plant); // Sun Nov 1, the first 1:30 am (daylight)
        var sun0130Cst = Row("2026-11-01T07:30:00Z", HistoryKind.Plant); // Sun Nov 1, the second 1:30 am (standard)
        var page = GardenBoard.History(History(sat2350, sun0010, sun0130Cdt, sun0130Cst), new HistoryFilter(), null, NoAliases, Estates, T, central);

        Assert.Equal(["2026-11-01T00:00-05:00", "2026-10-31T00:00-05:00"], page.Days.Select(d => d.Day.ToString("yyyy-MM-ddTHH:mmzzz")).ToArray());
        Assert.Equal([sun0130Cst.Id, sun0130Cdt.Id, sun0010.Id], page.Days[0].Rows.Select(r => r.Entry.Id).ToArray());
        Assert.Equal([sat2350.Id], page.Days[1].Rows.Select(r => r.Entry.Id).ToArray());
        Assert.Equal(["2026-11-01 3 open", "2026-10-31 1 folded"],
            Drawn(GardenBoard.HistoryLines(page, new HashSet<DateTimeOffset> { DateTimeOffset.Parse("2026-11-01T00:00:00-05:00") }))
                .Where(s => s.Contains(' ')).ToArray());
    }

    [Fact] // a pick whose line is gone (its alias changed away) falls back to All hands
    public void APickWhoseLineIsGoneFallsBackToAllHands()
    {
        var kupo = Row("2026-11-07T10:00:00Z", HistoryKind.Plant, by: "jw9cdj");
        var mine = Row("2026-11-07T09:00:00Z", HistoryKind.Plant, by: Hand.OwnSlug);
        var history = History(kupo, mine);

        var page = Page(history, hand: HandPick.Named("Kupo"), aliases: new() { ["jw9cdj"] = "Mog" });

        Assert.Null(page.Hand);
        Assert.False(page.Filtered);
        Assert.Equal([kupo.Id, mine.Id], Ids(page));
    }
}
