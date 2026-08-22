using BalambGarden.Engine.Census;

namespace BalambGarden.Engine.Ledger;

/// <summary>What a migration pass did, in words the log can print. Pure data - the Engine
/// owns no logger, so the plugin decides where these lines go.</summary>
public sealed record MigrationReport(
    int MergedRecords, IReadOnlyList<string> Notes, IReadOnlyList<string> Warnings)
{
    public bool Changed => MergedRecords > 0;
}

/// <summary>Repairs ledgers written before estate identity was the physical plot.
///
/// The bug (live receipt, 08-15): EstateSensor keyed on ClientState.TerritoryType, so a
/// house INTERIOR (641 -> 649 Shirogane, 340 -> 344 Lavender Beds in Drift's own file) minted
/// a SECOND estate record for the same ward/plot - the dashboard showed one physical plot
/// as two rows. Ward and plot always read correctly indoors; only the territory split.
///
/// The repair is receipt-bound, not clever: an interior record can only be normalized when
/// the ledger already holds exactly ONE exterior record for that (ward, plot) - that record
/// IS the receipt for which district the house is in. Interior territory ids are shared
/// TEMPLATES across districts, so they can never supply that answer themselves. Anything
/// ambiguous is kept as-is and reported loudly; this pass never drops a receipt to make a
/// roster look tidy.</summary>
public static class LedgerMigration
{
    public static MigrationReport NormalizeEstates(LedgerStore store)
    {
        var notes = new List<string>();
        var warnings = new List<string>();
        var merged = 0;

        foreach (var group in store.Estates.GroupBy(e => (e.Key.Ward, e.Key.Plot)).ToList())
        {
            // Apartments are their own estates and have no plot at all (negative Plot
            // sentinel). Nothing about them was ever split, and their Room is a real
            // apartment number, so this pass has no business touching them.
            if (group.Key.Plot < 0)
                continue;

            // Only Room == 0 is the bug's fingerprint: the old sensor filed a house's MAIN
            // FLOOR under the interior territory id. Room > 0 is a private room, which is
            // now an estate in its own right (08-15 ruling) - folding one into its house
            // would be a new bug wearing the old repair's clothes.
            var interiors = group.Where(e => e.Key.Room == 0).ToList();
            if (interiors.Count == 0)
                continue;   // nothing split here; two districts sharing a ward/plot is normal

            var exteriors = group.Where(e => e.Key.Room < 0).ToList();
            var label = interiors[0].Key.DisplayLabel();

            if (exteriors.Count == 0)
            {
                warnings.Add($"{label}: {interiors.Count} interior record(s) with no exterior "
                    + "visit to name the district - kept as-is (stand in the yard once and "
                    + "the next load can merge them)");
                continue;
            }

            if (exteriors.Count > 1)
            {
                warnings.Add($"{label}: {exteriors.Count} exterior records (districts "
                    + $"{string.Join(", ", exteriors.Select(e => e.Key.TerritoryId))}) - cannot "
                    + "tell which one the interior record(s) belong to, kept as-is");
                continue;
            }

            var canonical = exteriors[0];
            foreach (var interior in interiors)
            {
                if (Blocker(store, interior, canonical) is { } why)
                {
                    warnings.Add($"{label}: kept the split record (territory "
                        + $"{interior.Key.TerritoryId}, room {interior.Key.Room}) - {why}");
                    continue;
                }

                canonical = MergeRecord(store, interior, canonical);
                MergeBindings(store, interior.Key, canonical.Key, notes);
                MergeBeds(store, interior.Key, canonical.Key, notes);
                merged++;
            }
        }

        if (merged > 0)
            notes.Insert(0, $"merged {merged} split estate record(s) into their physical plot");

        return new MigrationReport(merged, notes, warnings);
    }

    /// <summary>The fail-closed gate: anything that cannot be merged without choosing which
    /// receipt to believe blocks the whole record, and the split survives to be reported.</summary>
    private static string? Blocker(LedgerStore store, EstateRecord interior, EstateRecord canonical)
    {
        if (interior.Nickname.Length > 0 && canonical.Nickname.Length > 0
            && interior.Nickname != canonical.Nickname)
            return $"two different nicknames ('{canonical.Nickname}' vs '{interior.Nickname}')";

        foreach (var (from, to) in BindingMoves(store, interior.Key, canonical.Key))
        {
            if (store.Bindings.TryGetValue(to, out var there)
                && there != store.Bindings[from])
                return $"binding {from} -> {store.Bindings[from]} conflicts with {to} -> {there}";
        }

        return null;
    }

    private static EstateRecord MergeRecord(
        LedgerStore store, EstateRecord interior, EstateRecord canonical)
    {
        var merged = new EstateRecord
        {
            Key = canonical.Key,
            Nickname = canonical.Nickname.Length > 0 ? canonical.Nickname : interior.Nickname,
            FirstSeen = canonical.FirstSeen <= interior.FirstSeen
                ? canonical.FirstSeen : interior.FirstSeen,
            LastVisited = canonical.LastVisited >= interior.LastVisited
                ? canonical.LastVisited : interior.LastVisited,
        };
        store.Estates[store.Estates.IndexOf(canonical)] = merged;
        store.Estates.Remove(interior);
        return merged;
    }

    /// <summary>Every binding written under an interior estate key is a POT binding: the
    /// outdoor join needs the outdoor map and the patch objects, neither of which exists
    /// indoors, so nothing else could have produced one. Pots keep their own namespace on
    /// the canonical key because the two DataMaps can hand out the same map key.</summary>
    private static IEnumerable<(string From, string To)> BindingMoves(
        LedgerStore store, EstateKey interior, EstateKey canonical)
    {
        var prefix = interior.BindingKey(0)[..(interior.BindingKey(0).IndexOf('#') + 1)];
        foreach (var key in store.Bindings.Keys.Where(k => k.StartsWith(prefix)).OrderBy(k => k))
        {
            var suffix = key[prefix.Length..];
            if (suffix.StartsWith("pot"))
                suffix = suffix[3..];
            if (!int.TryParse(suffix, out var ordinal))
                continue;
            yield return (key, canonical.BindingKey(ordinal, isPot: true));
        }
    }

    private static void MergeBindings(
        LedgerStore store, EstateKey interior, EstateKey canonical, List<string> notes)
    {
        foreach (var (from, to) in BindingMoves(store, interior, canonical).ToList())
        {
            var value = store.Bindings[from];
            store.Bindings.Remove(from);
            store.Bindings[to] = value;
            notes.Add($"binding {from} -> {to}");
        }
    }

    private static void MergeBeds(
        LedgerStore store, EstateKey interior, EstateKey canonical, List<string> notes)
    {
        foreach (var bed in store.Beds.Where(b => b.Estate == interior).ToList())
        {
            var twin = store.Beds.FirstOrDefault(b =>
                b.Estate == canonical && b.IsPot == bed.IsPot
                && b.PatchOrdinal == bed.PatchOrdinal && b.BedSlot == bed.BedSlot);

            if (twin is null)
            {
                store.Beds[store.Beds.IndexOf(bed)] = Rekey(bed, canonical);
                continue;
            }

            // Same physical bed, two records. Union the observations rather than pick a
            // winner - the ring caps itself at the newest RingCapacity either way.
            store.Beds[store.Beds.IndexOf(twin)] = Combine(twin, bed, canonical);
            store.Beds.Remove(bed);
            notes.Add($"merged duplicate bed {canonical.DisplayLabel()} "
                + $"patch {bed.PatchOrdinal} slot {bed.BedSlot}");
        }
    }

    private static ClaimedBed Rekey(ClaimedBed bed, EstateKey canonical)
    {
        var moved = new ClaimedBed
        {
            Estate = canonical, MapKey = bed.MapKey, PatchOrdinal = bed.PatchOrdinal,
            BedSlot = bed.BedSlot, IsPot = bed.IsPot, FirstRecorded = bed.FirstRecorded,
            LastTended = bed.LastTended,
        };
        foreach (var o in bed.Ring)
            moved.Observe(o);
        return moved;
    }

    private static ClaimedBed Combine(ClaimedBed keep, ClaimedBed other, EstateKey canonical)
    {
        var combined = new ClaimedBed
        {
            Estate = canonical, MapKey = keep.MapKey, PatchOrdinal = keep.PatchOrdinal,
            BedSlot = keep.BedSlot, IsPot = keep.IsPot,
            FirstRecorded = keep.FirstRecorded <= other.FirstRecorded ? keep.FirstRecorded : other.FirstRecorded,
            LastTended = Later(keep.LastTended, other.LastTended),
        };
        foreach (var o in keep.Ring.Concat(other.Ring))
            combined.Observe(o);
        return combined;
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null ? a : a > b ? a : b;
}
