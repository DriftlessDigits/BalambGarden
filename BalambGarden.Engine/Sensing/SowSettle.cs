namespace BalambGarden.Engine.Sensing;

/// <summary>Beds whose own chain sow the map has not shown yet. The chain re-reads the map the
/// moment the player's Yes lands, a beat before the game puts the seed in (field-fire 2026-11-07),
/// so for that beat the bed reads empty while the record already holds the new plant. A watched bed
/// waits for a read that shows it occupied; until then its empty read is the game catching up, not
/// evidence. The hand sow needs none of this - its line is only heard once the map names the seed.
///
/// <para>Display only: nothing here writes the ledger. The limit matches the hand sow's wait for the
/// map to name a seed; past it, an empty bed is news again.</para></summary>
public sealed class SowSettle
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>One bed that stopped waiting: <paramref name="Shown"/> false means the limit ran out.</summary>
    public sealed record Settled(int MapKey, int Slot, TimeSpan After, bool Shown);

    private readonly List<(int MapKey, int Slot, DateTimeOffset SownAt)> waiting = [];

    public bool Any => waiting.Count > 0;

    public void Watch(int mapKey, int slot, DateTimeOffset sownAt)
    {
        waiting.RemoveAll(w => w.MapKey == mapKey && w.Slot == slot);
        waiting.Add((mapKey, slot, sownAt));
    }

    public bool Waiting(int mapKey, int slot) => waiting.Any(w => w.MapKey == mapKey && w.Slot == slot);

    /// <summary>Checks every waiting bed against a fresh read: a bed the read shows occupied, or one
    /// past the limit, stops waiting and comes back for the log line.</summary>
    public IReadOnlyList<Settled> Settle(IReadOnlyDictionary<int, IReadOnlyList<BedReading>> read, DateTimeOffset now)
    {
        var done = new List<Settled>();
        foreach (var w in waiting.ToList())
        {
            var shown = read.TryGetValue(w.MapKey, out var beds)
                        && beds.FirstOrDefault(b => b.Slot == w.Slot) is { Occupied: true };
            if (!shown && now - w.SownAt < Limit)
                continue;
            waiting.Remove(w);
            done.Add(new Settled(w.MapKey, w.Slot, now - w.SownAt, shown));
        }
        return done;
    }
}
