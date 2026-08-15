using BalambGarden.Engine.Sensing;

namespace BalambGarden.Engine.Census;

/// <summary>
/// Pot identity by map diff across ONE chain action - the indoor twin of the outdoor
/// diff join, and the only thing that can separate two pots of the same species.
///
/// <para>Why it exists: <see cref="PotBind.UniqueSpeciesKey"/> reads a static map and
/// asks which entry holds this species. Two Allagan Melons in one room are two identical
/// entries, and no amount of staring at them makes one of them the pot you just touched.
/// The receipts (08-15) say what does: PLANTING makes a pot's map entry appear, HARVESTING
/// clears it, and WATERING writes nothing at all (a freshly watered melon and its dry twin
/// have byte-identical 48-byte entries). So one plant-or-harvest on one known pot, read
/// before and after, changes exactly one entry - and that entry IS the pot.</para>
///
/// <para>Exactly one, or nothing. Zero changed entries means the map never reflected the
/// action; two or more means something else moved at the same moment. Neither is evidence
/// about which pot was touched, so neither binds - the caller says so plainly and the pot
/// stays honestly unbound until its own turn.</para>
/// </summary>
public static class PotDiff
{
    /// <summary>The one key that changed across the action, or null when the count is
    /// anything other than exactly one.</summary>
    public static int? Join(
        IReadOnlyDictionary<int, PotReading> before,
        IReadOnlyDictionary<int, PotReading> after)
    {
        var changed = ChangedKeys(before, after);
        return changed.Count == 1 ? changed[0] : null;
    }

    /// <summary>Every map key that appeared, vanished, or reads differently now, in key
    /// order. Callers need the count as well as the key: "the map changed in 3 places" is
    /// a different sentence to the player than "the map never changed", and both are
    /// refusals rather than binds.</summary>
    public static IReadOnlyList<int> ChangedKeys(
        IReadOnlyDictionary<int, PotReading> before,
        IReadOnlyDictionary<int, PotReading> after)
    {
        var keys = new HashSet<int>(before.Keys);
        keys.UnionWith(after.Keys);

        var changed = new List<int>();
        foreach (var key in keys)
        {
            var had = before.TryGetValue(key, out var was);
            var has = after.TryGetValue(key, out var now);
            // PotReading is a record: value equality is the whole comparison, so a species
            // change, a stage tick, or a pigment byte all count as "this entry changed".
            if (had != has || (had && has && !Equals(was, now)))
                changed.Add(key);
        }

        changed.Sort();
        return changed;
    }
}
