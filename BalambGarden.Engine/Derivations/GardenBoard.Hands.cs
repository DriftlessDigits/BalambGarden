using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>A foreign hand as the settings window offers it for a label: its tag, how many of the
/// history's rows are its (<see cref="OnRecord"/>), and its newest row there with the estate named and
/// the instant in the reader's zone (<see cref="Last"/>, null = a tag only the ledger holds).</summary>
public sealed record SeenHand(string Slug, int OnRecord, HistoryRowLine? Last);

public static partial class GardenBoard
{
    /// <summary>The settings window's foreign hands (rethink slice 10, spec ruling 15; on screen "Household hands"): every tag
    /// <paramref name="history"/> or the ledger's <paramref name="beds"/> (planted by, last tended by)
    /// holds, the own tag left out (it reads "you" by rule). Newest act first; tags with nothing in the
    /// history after them, by tag. Estates named as the History tab names them (nickname, else label).</summary>
    public static IReadOnlyList<SeenHand> ForeignHands(IEnumerable<ClaimedBed> beds, GardenHistory history,
        IReadOnlyList<EstateRecord> estates, TimeZoneInfo zone)
    {
        var names = new Dictionary<Census.EstateKey, string>();
        foreach (var record in estates)
            names.TryAdd(record.Key, record.DisplayName);

        var count = new Dictionary<string, int>();
        var newest = new Dictionary<string, HistoryEntry>();
        foreach (var row in history.Entries)
        {
            if (row.IsOwn)
                continue;
            count[row.By] = count.GetValueOrDefault(row.By) + 1;
            if (!newest.TryGetValue(row.By, out var last) || row.At > last.At)
                newest[row.By] = row;
        }

        var ledgerOnly = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var bed in beds)
            foreach (var hand in new[] { bed.PlantedBy, bed.LastTendedBy })
                if (hand is { IsOwn: false } h && !count.ContainsKey(h.Slug))
                    ledgerOnly.Add(h.Slug);

        return newest.Values
            .OrderByDescending(row => row.At)
            .Select(row => new SeenHand(row.By, count[row.By], new HistoryRowLine(row,
                names.TryGetValue(row.Estate, out var name) ? name : row.Estate.DisplayLabel(),
                TimeZoneInfo.ConvertTime(row.At, zone))))
            .Concat(ledgerOnly.Select(slug => new SeenHand(slug, 0, null)))
            .ToList();
    }
}
