using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>The History tab's whole content (rethink slice 9, spec ruling 13): the rows the filter
/// lets through, newest first, under a heading for each of the reader's days; how many there are
/// of the whole record (<see cref="Shown"/> of <see cref="Total"/>), and when the record begins
/// (<see cref="Since"/>, null = nothing on record yet); and the estate and crop lists the filters
/// offer, taken from the whole record whatever the filter.</summary>
public sealed record HistoryPage(
    DateTimeOffset? Since,
    int Total,
    int Shown,
    IReadOnlyList<HistoryLine> Lines,
    IReadOnlyList<HistoryEstate> Estates,
    IReadOnlyList<ushort> Crops);

/// <summary>An estate as the History tab names it: its nickname, else its label.</summary>
public sealed record HistoryEstate(EstateKey Key, string Name);

/// <summary>One line of the History tab: a day heading or a row.</summary>
public abstract record HistoryLine;

/// <summary>The heading over a day's rows: that day's midnight in the reader's zone.</summary>
public sealed record HistoryDayLine(DateTimeOffset Day) : HistoryLine;

/// <summary>One plant or harvest, with the name of the estate it happened at.</summary>
public sealed record HistoryRowLine(HistoryEntry Entry, string Estate) : HistoryLine;

public static partial class GardenBoard
{
    /// <summary>The History tab: the rows of <paramref name="history"/> that
    /// <paramref name="filter"/> lets through (<see cref="HistoryQuery.Find"/>, newest first), a day
    /// heading wherever the date in <paramref name="zone"/> changes, each row's estate named from
    /// <paramref name="estates"/> (an estate the ledger does not name reads its label). The history
    /// is the only store: nothing here is kept between calls.</summary>
    public static HistoryPage History(GardenHistory history, HistoryFilter filter,
        IReadOnlyList<EstateRecord> estates, DomainTables tables, TimeZoneInfo zone)
    {
        var names = new Dictionary<EstateKey, string>();
        foreach (var record in estates)
            names.TryAdd(record.Key, record.DisplayName);
        string Name(EstateKey key) => names.TryGetValue(key, out var name) ? name : key.DisplayLabel();

        var rows = HistoryQuery.Find(history, filter);
        var lines = new List<HistoryLine>(rows.Count + 8);
        DateTime? day = null;
        foreach (var row in rows)
        {
            var local = TimeZoneInfo.ConvertTime(row.At, zone);
            if (local.Date != day)
            {
                day = local.Date;
                lines.Add(new HistoryDayLine(new DateTimeOffset(local.Date, zone.GetUtcOffset(local.Date))));
            }
            lines.Add(new HistoryRowLine(row, Name(row.Estate)));
        }

        return new HistoryPage(
            Since: history.Entries.Count == 0 ? null : history.Entries.Min(e => e.At),
            Total: history.Entries.Count,
            Shown: rows.Count,
            Lines: lines,
            Estates: history.Entries.Select(e => e.Estate).Distinct()
                .Select(key => new HistoryEstate(key, Name(key)))
                .OrderBy(e => e.Name, StringComparer.CurrentCulture)
                .ToList(),
            Crops: history.Entries.Select(e => e.Species).Distinct()
                .OrderBy(tables.SpeciesName, StringComparer.CurrentCulture)
                .ToList());
    }
}
