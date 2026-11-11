using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>A foreign hand as the settings window offers it for an alias: the hand, how many of the
/// history's rows are its (<see cref="HistoryRows"/>), and its newest row there with the estate named
/// and the instant in the reader's zone (<see cref="Last"/>, null = a hand only the ledger holds).</summary>
public sealed record SeenHand(Hand Hand, int HistoryRows, HistoryRowLine? Last);

public static partial class GardenBoard
{
    /// <summary>The settings window's foreign hands (rethink slice 10, spec ruling 15; on screen
    /// "Household hands"): every slug <paramref name="history"/> or the ledger's <paramref name="beds"/>
    /// (planted by, last tended by) holds, the own one left out (it reads "you" by rule). Newest act
    /// first; hands with nothing in the history after them, by slug. Estates named as the History tab
    /// names them (<see cref="EstateNamer"/>).</summary>
    public static IReadOnlyList<SeenHand> ForeignHands(IEnumerable<ClaimedBed> beds, GardenHistory history,
        IReadOnlyList<EstateRecord> estates, TimeZoneInfo zone)
    {
        var rows = new Dictionary<string, int>();
        var newest = new Dictionary<string, HistoryEntry>();
        foreach (var row in history.Entries)
        {
            if (row.IsOwn)
                continue;
            rows[row.By] = rows.GetValueOrDefault(row.By) + 1;
            if (!newest.TryGetValue(row.By, out var last) || row.At > last.At)
                newest[row.By] = row;
        }

        var ledgerOnly = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var bed in beds)
            foreach (var hand in new[] { bed.PlantedBy, bed.LastTendedBy })
                if (hand is { IsOwn: false } h && !rows.ContainsKey(h.Slug))
                    ledgerOnly.Add(h.Slug);

        var estateName = EstateNamer(estates);
        return newest.Values
            .OrderByDescending(row => row.At)
            .Select(row => new SeenHand(new Hand(row.By), rows[row.By],
                new HistoryRowLine(row, estateName(row.Estate), TimeZoneInfo.ConvertTime(row.At, zone))))
            .Concat(ledgerOnly.Select(slug => new SeenHand(new Hand(slug), 0, null)))
            .ToList();
    }

    /// <summary>An estate as the History tab and the settings window name it: its nickname, else its
    /// label (an estate the ledger does not hold reads its label).</summary>
    public static Func<EstateKey, string> EstateNamer(IReadOnlyList<EstateRecord> estates)
    {
        var names = new Dictionary<EstateKey, string>();
        foreach (var record in estates)
            names.TryAdd(record.Key, record.DisplayName);
        return key => names.TryGetValue(key, out var name) ? name : key.DisplayLabel();
    }
}
