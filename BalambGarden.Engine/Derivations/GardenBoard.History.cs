using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>The History tab's whole content (rethink slice 9, spec ruling 13): the rows the filter
/// lets through, newest first, grouped by the reader's days (<see cref="Days"/>, newest first, a day
/// only while it holds a row the filter lets through); how many there are
/// of the whole record (<see cref="Shown"/> of <see cref="Total"/>, <see cref="Filtered"/> when any
/// filter is set), and when the record begins (<see cref="Since"/>, null = nothing on record yet);
/// and the estate, species and hand lists the filters offer, taken from the whole record whatever the
/// filter. <see cref="Hand"/> is the hand pick in force: the one asked for, or null (All hands) when
/// its line is gone from <see cref="Hands"/> (its alias changed away).</summary>
public sealed record HistoryPage(
    DateTimeOffset? Since,
    int Total,
    int Shown,
    bool Filtered,
    IReadOnlyList<HistoryDay> Days,
    IReadOnlyList<HistoryEstate> Estates,
    IReadOnlyList<ushort> Species,
    IReadOnlyList<HandPick> Hands,
    HandPick? Hand);

/// <summary>An estate as the History tab names it: its nickname, else its label.</summary>
public sealed record HistoryEstate(EstateKey Key, string Name);

/// <summary>One of the reader's days on the History tab: its midnight in the reader's zone and its
/// rows the filter lets through, newest first (never none).</summary>
public sealed record HistoryDay(DateTimeOffset Day, IReadOnlyList<HistoryRowLine> Rows);

/// <summary>One line of the History tab as drawn (<see cref="GardenBoard.HistoryLines"/>): a day
/// heading or a row.</summary>
public abstract record HistoryLine;

/// <summary>The heading over a day (rethink slice 24, ruling 13 amended: folding days): that day's
/// midnight in the reader's zone, how many of its rows the filter lets through, and whether it is
/// open (its rows drawn under it) or folded.</summary>
public sealed record HistoryDayLine(DateTimeOffset Day, int Count, bool Open) : HistoryLine;

/// <summary>One plant or harvest: the row, the name of the estate it happened at, and its instant in
/// the reader's zone.</summary>
public sealed record HistoryRowLine(HistoryEntry Entry, string EstateName, DateTimeOffset LocalAt) : HistoryLine;

/// <summary>How a hand shows (rethink slice 11, spec ruling 15), and so a line of the History tab's
/// hand filter: this install's hand (<see cref="You"/>), every household hand one alias names
/// (<see cref="Named"/>), or every household hand with none (<see cref="Household"/>). The Hand cell's
/// word and the filter's line both come from it (<see cref="WindowFormat.HandName"/>,
/// <see cref="WindowFormat.HandChoice"/>), so each line shows exactly the rows whose cell reads its
/// word, two hands given one alias included.</summary>
public sealed record HandPick
{
    private HandPick(bool own, string? alias)
    {
        Own = own;
        Alias = alias;
    }

    public bool Own { get; }
    public string? Alias { get; }

    public static readonly HandPick You = new(true, null);
    public static readonly HandPick Household = new(false, null);

    public static HandPick Named(string alias) => new(false, alias);

    /// <summary>How <paramref name="hand"/> shows, by the one read rule (<see cref="Aliases.Of"/>).</summary>
    public static HandPick Of(Hand hand, IReadOnlyDictionary<string, string> aliases)
        => hand.IsOwn ? You : Aliases.Of(aliases, hand) is { } alias ? Named(alias) : Household;
}

public static partial class GardenBoard
{
    /// <summary>The History tab: the rows of <paramref name="history"/> that
    /// <paramref name="filter"/> lets through (<see cref="HistoryQuery.Find"/>, newest first) and, when
    /// one is picked and still listed, whose <paramref name="hand"/> line they fall under by
    /// <paramref name="aliases"/>; grouped into days wherever the date in <paramref name="zone"/> changes,
    /// each row's estate named from <paramref name="estates"/> (an estate the ledger does not name
    /// reads its label). The history is the only store: nothing here is kept between calls.</summary>
    public static HistoryPage History(GardenHistory history, HistoryFilter filter, HandPick? hand,
        IReadOnlyDictionary<string, string> aliases, IReadOnlyList<EstateRecord> estates, DomainTables tables, TimeZoneInfo zone)
    {
        var estateName = EstateNamer(estates);
        var hands = HandList(history, aliases);
        if (hand is not null && !hands.Contains(hand))
            hand = null;

        var rows = HistoryQuery.Find(history, filter)
            .Where(row => hand is null || HandPick.Of(new Hand(row.By), aliases) == hand)
            .ToList();
        var days = new List<HistoryDay>();
        List<HistoryRowLine>? dayRows = null;
        DateTime? day = null;
        foreach (var row in rows)
        {
            var local = TimeZoneInfo.ConvertTime(row.At, zone);
            if (local.Date != day)
            {
                day = local.Date;
                dayRows = [];
                days.Add(new HistoryDay(TimelineSpan.AtMidnight(local.Date, zone), dayRows));
            }
            dayRows!.Add(new HistoryRowLine(row, estateName(row.Estate), local));
        }

        return new HistoryPage(
            Since: history.Entries.Count == 0 ? null : history.Entries.Min(e => e.At),
            Total: history.Entries.Count,
            Shown: rows.Count,
            Filtered: filter != new HistoryFilter() || hand is not null,
            Days: days,
            Estates: history.Entries.Select(e => e.Estate).Distinct()
                .Select(key => new HistoryEstate(key, estateName(key)))
                .OrderBy(e => e.Name, StringComparer.CurrentCulture)
                .ToList(),
            Species: history.Entries.Select(e => e.Species).Distinct()
                .OrderBy(tables.SpeciesName, StringComparer.CurrentCulture)
                .ToList(),
            Hands: hands,
            Hand: hand);
    }

    /// <summary>The History tab's lines as drawn (rethink slice 24, ruling 13 amended: folding days):
    /// a heading for each day of <paramref name="page"/> with its count, and under each day in
    /// <paramref name="open"/> (by its midnight) its rows; every other day folded to its heading. The
    /// open days are the reader's, kept across a filter change and a landing row; a day the filter
    /// empties is not listed, so no heading ever stands over nothing.</summary>
    public static IReadOnlyList<HistoryLine> HistoryLines(HistoryPage page, IReadOnlySet<DateTimeOffset> open)
    {
        var lines = new List<HistoryLine>();
        foreach (var day in page.Days)
        {
            var isOpen = open.Contains(day.Day);
            lines.Add(new HistoryDayLine(day.Day, day.Rows.Count, isOpen));
            if (isOpen)
                lines.AddRange(day.Rows);
        }
        return lines;
    }

    /// <summary>The days open when the History tab is opened: the newest day only (ruled 2026-11-11,
    /// "newest day open"); none when nothing is listed. A day that lands later arrives folded.</summary>
    public static IReadOnlySet<DateTimeOffset> HistoryOpenOnArrival(HistoryPage page)
        => page.Days.Count == 0 ? new HashSet<DateTimeOffset>() : new HashSet<DateTimeOffset> { page.Days[0].Day };

    /// <summary>Where <paramref name="line"/> now stands in <paramref name="lines"/>: a heading by its
    /// day, a row by its entry; -1 when it is not there (filtered away, or its day folded). The window
    /// keeps the line it was reading at the top of its view with it, so a landing row moves nothing.</summary>
    public static int HistoryLineIndex(IReadOnlyList<HistoryLine> lines, HistoryLine line)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var same = (lines[i], line) switch
            {
                (HistoryDayLine a, HistoryDayLine b) => a.Day == b.Day,
                (HistoryRowLine a, HistoryRowLine b) => a.Entry.Id == b.Entry.Id,
                _ => false,
            };
            if (same)
                return i;
        }
        return -1;
    }

    /// <summary>The hand filter's lines past "All hands": "You" always, then each alias on record by
    /// name, then "Household" while a household hand with no alias is on record.</summary>
    private static List<HandPick> HandList(GardenHistory history, IReadOnlyDictionary<string, string> aliases)
    {
        var onRecord = history.Entries.Select(e => e.By).Distinct()
            .Select(slug => HandPick.Of(new Hand(slug), aliases))
            .ToHashSet();
        return
        [
            HandPick.You,
            .. onRecord.Where(p => p.Alias is not null).OrderBy(p => p.Alias, StringComparer.CurrentCulture),
            .. onRecord.Contains(HandPick.Household) ? [HandPick.Household] : Array.Empty<HandPick>(),
        ];
    }
}
