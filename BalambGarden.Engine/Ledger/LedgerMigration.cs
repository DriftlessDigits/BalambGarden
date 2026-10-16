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

            // Same physical bed, two records. Union the planting evidence rather than pick a
            // winner, then trim at the merged fence - whatever either record knew about a
            // plant that already left goes with it.
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
            WireEntity = bed.WireEntity,
            Latest = bed.Latest,
            PlantedAt = bed.PlantedAt, PlantedBy = bed.PlantedBy, PlantedSpecies = bed.PlantedSpecies,
            LastTended = bed.LastTended, LastTendedBy = bed.LastTendedBy, LastTendedSpecies = bed.LastTendedSpecies,
            PlantingResetAt = bed.PlantingResetAt, PlantingResetReason = bed.PlantingResetReason,
            LegacyRing = bed.LegacyRing,
        };
        foreach (var (stage, seen) in bed.StageSeen)
            moved.StageSeen[stage] = seen;
        return moved;
    }

    /// <summary>Two records of one bed become one: every field takes its newest or widest
    /// value (the hand and species ride with the instant that won), the fence takes the later
    /// reset, and then the merged record is TRIMMED at that fence - a twin's memory of a plant
    /// the other record already saw leave does not come back (pass-2 #9). WireEntity is kept
    /// from whichever side minted one.</summary>
    private static ClaimedBed Combine(ClaimedBed keep, ClaimedBed other, EstateKey canonical)
    {
        var newerPlant = Newer(keep.PlantedAt, other.PlantedAt) == other.PlantedAt && other.PlantedAt is not null ? other : keep;
        var newerTend = Newer(keep.LastTended, other.LastTended) == other.LastTended && other.LastTended is not null ? other : keep;
        var newerReset = Newer(keep.PlantingResetAt, other.PlantingResetAt) == other.PlantingResetAt && other.PlantingResetAt is not null ? other : keep;
        var newerLatest = keep.Latest is null || (other.Latest is { } ol && ol.At > keep.Latest.At) ? other.Latest : keep.Latest;

        var combined = new ClaimedBed
        {
            Estate = canonical, MapKey = keep.MapKey, PatchOrdinal = keep.PatchOrdinal,
            BedSlot = keep.BedSlot, IsPot = keep.IsPot,
            FirstRecorded = keep.FirstRecorded <= other.FirstRecorded ? keep.FirstRecorded : other.FirstRecorded,
            WireEntity = keep.WireEntity ?? other.WireEntity,
            Latest = newerLatest,
            PlantedAt = newerPlant.PlantedAt, PlantedBy = newerPlant.PlantedBy, PlantedSpecies = newerPlant.PlantedSpecies,
            LastTended = newerTend.LastTended, LastTendedBy = newerTend.LastTendedBy, LastTendedSpecies = newerTend.LastTendedSpecies,
            PlantingResetAt = newerReset.PlantingResetAt, PlantingResetReason = newerReset.PlantingResetReason,
        };
        foreach (var (stage, seen) in keep.StageSeen.Concat(other.StageSeen))
            combined.StageSeen[stage] = combined.StageSeen.TryGetValue(stage, out var have)
                ? have.Fold(seen.FirstSeen).Fold(seen.LastSeen) : seen;

        if (combined.PlantingResetAt is { } fence)
            TrimAt(combined, fence);
        return combined;
    }

    /// <summary>Everything at or before the fence leaves (the fence itself stays). The same
    /// cut Rebase makes, applied to a freshly merged record without minting an event.</summary>
    private static void TrimAt(ClaimedBed bed, DateTimeOffset fence)
    {
        if (bed.PlantedAt is { } p && p <= fence) { bed.PlantedAt = null; bed.PlantedBy = null; bed.PlantedSpecies = 0; }
        if (bed.Latest is { } l && l.At <= fence) bed.Latest = null;
        foreach (var (stage, seen) in bed.StageSeen.ToList())
        {
            if (seen.LastSeen <= fence) bed.StageSeen.Remove(stage);
            else if (seen.FirstSeen <= fence) bed.StageSeen[stage] = seen with { FirstSeen = seen.LastSeen };
        }
        if (bed.LastTended is { } t && t <= fence) { bed.LastTended = null; bed.LastTendedBy = null; bed.LastTendedSpecies = 0; }
    }

    private static DateTimeOffset? Newer(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>v2 -> v3 (2026-10-14): folds each row's observation ring into the planting
    /// record and retires the ring. Lossy and honest - a ring held about a minute of per-read
    /// sightings, so what survives is: the last entry as <see cref="ClaimedBed.Latest"/> (when
    /// the v2 file carried none), per-stage first/last from the entries of the current planting
    /// (those after the last HarvestReceipt entry, naming the same species as the last entry),
    /// a PlantReceipt entry among them as the sowing by own hands, and whose hand set the water
    /// clock when the ring still says (a TendReceipt entry at the clock's own instant = own; an
    /// External one, or nothing, = unknown). Runs BEFORE <see cref="NormalizeEstates"/> so the
    /// merge works on records, not rings. Idempotent: a folded row has no ring to fold. Returns
    /// how many rows carried a ring; the version is stamped regardless.</summary>
    public static int FoldLegacyRings(LedgerStore store)
    {
        var folded = 0;
        foreach (var bed in store.Beds)
        {
            if (bed.LegacyRing is not { Count: > 0 } ring)
            {
                bed.LegacyRing = null;
                continue;
            }
            folded++;

            var ordered = ring.OrderBy(o => o.At).ToList();
            var last = ordered[^1];
            bed.Latest ??= last;

            var lastHarvest = ordered.LastOrDefault(o => o.Source == ObservationSource.HarvestReceipt);
            var planting = ordered
                .Where(o => lastHarvest is null || o.At > lastHarvest.At)
                .Where(o => o.SpeciesIndex == 0 || last.SpeciesIndex == 0 || o.SpeciesIndex == last.SpeciesIndex)
                .ToList();

            foreach (var o in planting)
            {
                if (o.Stage is < 1 or > 4)
                    continue;
                bed.StageSeen[o.Stage] = bed.StageSeen.TryGetValue(o.Stage, out var seen)
                    ? seen.Fold(o.At) : new StageBracket(o.At, o.At);
            }

            if (planting.LastOrDefault(o => o.Source == ObservationSource.PlantReceipt) is { } plant)
            {
                bed.PlantedAt = plant.At;
                bed.PlantedBy = Hand.Own;
                bed.PlantedSpecies = plant.SpeciesIndex;
            }

            if (bed.LastTended is { } t && bed.LastTendedBy is null)
            {
                var ownAtClock = ordered.Any(o => o.Source == ObservationSource.TendReceipt && o.At == t);
                bed.LastTendedBy = ownAtClock ? Hand.Own : null;
                if (ownAtClock)
                    bed.LastTendedSpecies = ordered.Last(o => o.Source == ObservationSource.TendReceipt && o.At == t).SpeciesIndex;
            }

            bed.LegacyRing = null;
        }
        store.Version = LedgerStore.CurrentVersion;
        return folded;
    }

    /// <summary>v3 -> v4 (2026-10-15): the fence fields were renamed on disk when "tenancy"
    /// became "planting". Moves any value still under the old names onto the new ones (the new
    /// name wins if both are somehow present), maps the one renamed reason member, and nulls the
    /// legacy seam so it never writes. Idempotent; stamps the version regardless. Returns how
    /// many rows carried a legacy fence.</summary>
    public static int RenameFenceFields(LedgerStore store)
    {
        var moved = 0;
        foreach (var bed in store.Beds)
        {
            if (bed.LegacyFenceAt is { } at)
            {
                moved++;
                if (bed.PlantingResetAt is null)
                {
                    bed.PlantingResetAt = at;
                    bed.PlantingResetReason = ParseLegacyReason(bed.LegacyFenceReason);
                }
            }
            bed.LegacyFenceAt = null;
            bed.LegacyFenceReason = null;
        }
        store.Version = LedgerStore.CurrentVersion;
        return moved;
    }

    private static RebaseReason? ParseLegacyReason(string? text) => text switch
    {
        null => null,
        "NewTenancySighted" => RebaseReason.NewPlantingSighted,
        _ => Enum.TryParse<RebaseReason>(text, out var r) ? r : null,
    };
}
