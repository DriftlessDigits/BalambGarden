using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>The History tab's whole content (rethink slice 9, spec ruling 13): the rows the filter
/// lets through, newest first, under a heading for each of the reader's days; how many there are
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
    IReadOnlyList<HistoryLine> Lines,
    IReadOnlyList<HistoryEstate> Estates,
    IReadOnlyList<ushort> Species,
    IReadOnlyList<HandPick> Hands,
    HandPick? Hand);

/// <summary>An estate as the History tab names it: its nickname, else its label.</summary>
public sealed record HistoryEstate(EstateKey Key, string Name);

/// <summary>One line of the History tab: a day heading or a row.</summary>
public abstract record HistoryLine;

/// <summary>The heading over a day's rows: that day's midnight in the reader's zone.</summary>
public sealed record HistoryDayLine(DateTimeOffset Day) : HistoryLine;

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
    /// <paramref name="aliases"/>; a day heading wherever the date in <paramref name="zone"/> changes,
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
        var lines = new List<HistoryLine>(rows.Count + 8);
        DateTime? day = null;
        foreach (var row in rows)
        {
            var local = TimeZoneInfo.ConvertTime(row.At, zone);
            if (local.Date != day)
            {
                day = local.Date;
                lines.Add(new HistoryDayLine(TimelineSpan.AtMidnight(local.Date, zone)));
            }
            lines.Add(new HistoryRowLine(row, estateName(row.Estate), local));
        }

        return new HistoryPage(
            Since: history.Entries.Count == 0 ? null : history.Entries.Min(e => e.At),
            Total: history.Entries.Count,
            Shown: rows.Count,
            Filtered: filter != new HistoryFilter() || hand is not null,
            Lines: lines,
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
