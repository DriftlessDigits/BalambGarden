using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Census;

/// <summary>The join + census brain. Receipts and sightings are the only write paths (spec:
/// Permission Architecture, 2026-08-15): a row exists because the game showed us something at
/// an estate we are rostered for. No Claim() method exists.</summary>
public sealed class CensusEngine(LedgerStore ledger)
{
    public IReadOnlyList<ClaimedBed> LedgerBeds => ledger.Beds;

    public void Bind(EstateKey estate, int patchOrdinal, int mapKey, bool isPot = false)
        => ledger.Bindings[estate.BindingKey(patchOrdinal, isPot)] = mapKey;

    public int? BoundKey(EstateKey estate, int patchOrdinal, bool isPot = false)
        => ledger.Bindings.TryGetValue(estate.BindingKey(patchOrdinal, isPot), out var k) ? k : null;

    public ClaimedBed? OnReceipt(ReceiptEvent e)
    {
        if (BoundKey(e.Estate, e.PatchOrdinal, e.IsPot) is not { } mapKey)
            return null;

        var bed = ledger.Beds.FirstOrDefault(b =>
            b.Estate == e.Estate && b.IsPot == e.IsPot
            && b.PatchOrdinal == e.PatchOrdinal && b.BedSlot == e.BedSlot);

        if (bed is null)
        {
            bed = new ClaimedBed
            {
                Estate = e.Estate, MapKey = mapKey, PatchOrdinal = e.PatchOrdinal,
                BedSlot = e.BedSlot, IsPot = e.IsPot, FirstRecorded = e.At,
            };
            ledger.Beds.Add(bed);
        }

        bed.Observe(new Observation(e.At, e.SpeciesIndex, e.Stage, SourceFor(e.Verb)));
        if (e.Verb is ReceiptVerb.Tend or ReceiptVerb.PotWater)
            bed.LastTended = e.At;
        return bed;
    }

    /// <summary>Which ordinal a bound map key belongs to at this estate - the bindings are the
    /// receipts, this only reads them backward. Null = not ours (ward-visible neighbor data).</summary>
    public int? OrdinalOfKey(EstateKey estate, int mapKey, bool isPot = false)
    {
        for (var ordinal = 0; ordinal < 16; ordinal++)
            if (BoundKey(estate, ordinal, isPot) == mapKey)
                return ordinal;
        return null;
    }

    /// <summary>Map sightings are census records now (spec 2026-08-15: no ceremony gates
    /// tracking). With mayRecord - the caller vouching the estate is roster-covered - a
    /// sighting CREATES rows: any occupied bed of a receipt-bound outdoor key, and any
    /// occupied pot (the indoor map is house-scoped, 08-13, and furniture idx == key, 08-15,
    /// so a pot sighting carries its own identity and binds on sight). An unbound outdoor
    /// key stays ephemeral regardless - that is the neighbors' garden passing by.</summary>
    public int OnMapSighting(
        EstateKey estate, int mapKey, IReadOnlyList<Sensing.BedReading> beds, DateTimeOffset at,
        bool isPot = false, bool mayRecord = false)
    {
        if (mayRecord && isPot && BoundKey(estate, mapKey, isPot: true) is null)
            Bind(estate, mapKey, mapKey, isPot: true);

        var ordinal = isPot ? mapKey : OrdinalOfKey(estate, mapKey);

        var count = 0;
        foreach (var reading in beds)
        {
            var bed = ledger.Beds.FirstOrDefault(b =>
                b.Estate == estate && b.IsPot == isPot
                && b.MapKey == mapKey && b.BedSlot == reading.Slot);

            // Reconcile (2026-08-18 ruling: the game wins on content mismatch). An empty
            // read against a row with contents means the plant left while nobody watched -
            // the row empties and stays. Rebase is destructive, so it needs the same vouch
            // as creation; an empty slot with no row is still nothing to record.
            if (!reading.Occupied)
            {
                if (mayRecord && bed is { Latest: not null })
                {
                    bed.Rebase();
                    count++;
                }
                continue;
            }

            if (bed is null)
            {
                if (!mayRecord || ordinal is null)
                    continue;
                bed = new ClaimedBed
                {
                    Estate = estate, MapKey = mapKey, PatchOrdinal = ordinal.Value,
                    BedSlot = reading.Slot, IsPot = isPot, FirstRecorded = at,
                };
                ledger.Beds.Add(bed);
            }
            else if (mayRecord && IsNewTenancy(bed.Latest, reading))
            {
                bed.Rebase();
            }

            bed.Observe(new Observation(at, reading.SpeciesIndex, reading.Stage, ObservationSource.MapSighting));
            count++;
        }
        return count;
    }

    /// <summary>A live reading that contradicts the row's latest contents: a different
    /// species, or the same species at an EARLIER stage (stages only ever climb, so a
    /// regression is a replant, not a re-read). Species 0 on either side is "unknown",
    /// which is a shrug and never a contradiction; stage 0 likewise stays out of the
    /// regression test - only two positive stages can disagree about direction.</summary>
    private static bool IsNewTenancy(Observation? prev, Sensing.BedReading reading)
        => prev is not null
           && prev.SpeciesIndex != 0 && reading.SpeciesIndex != 0
           && (prev.SpeciesIndex != reading.SpeciesIndex
               || (prev.Stage >= 1 && reading.Stage >= 1 && reading.Stage < prev.Stage));

    public void Abandon(ClaimedBed bed) => ledger.Beds.Remove(bed);

    /// <summary>A pot's map entry vanishing is what a HARVEST looks like (08-15 receipt),
    /// so a recorded pot with contents whose key is missing from a settled read has been
    /// emptied behind our back - it rebases like any other content mismatch (2026-08-18).
    /// The CALLER vouches the read is settled (SawHousingObjects) and roster-covered;
    /// this only compares. Rows already empty are already right. Returns rebased count.</summary>
    public int ReconcileAbsentPots(EstateKey estate, IReadOnlyCollection<int> presentKeys)
    {
        var count = 0;
        foreach (var bed in ledger.Beds.Where(b =>
            b.Estate == estate && b.IsPot && b.Latest is not null
            && !presentKeys.Contains(b.MapKey)))
        {
            bed.Rebase();
            count++;
        }
        return count;
    }

    /// <summary>The pot-gate prune (08-16 ruling: option A, no seven-click funeral). A key
    /// the furniture vector has disowned - present in the DataMap, not backed by a
    /// flowerpot - loses its pot row and pot binding here. Pot namespace only: an outdoor
    /// patch sharing the number, and every other estate, are not spoken for. Returns how
    /// many rows died; the caller decides whether that is worth a save.</summary>
    public int PrunePhantomPots(EstateKey estate, IReadOnlyCollection<int> phantomKeys)
    {
        var pruned = ledger.Beds.RemoveAll(b =>
            b.Estate == estate && b.IsPot && phantomKeys.Contains(b.MapKey));
        foreach (var key in phantomKeys)
            ledger.Bindings.Remove(estate.BindingKey(key, isPot: true));
        return pruned;
    }

    private static ObservationSource SourceFor(ReceiptVerb verb) => verb switch
    {
        ReceiptVerb.Tend => ObservationSource.TendReceipt,
        ReceiptVerb.Plant => ObservationSource.PlantReceipt,
        ReceiptVerb.Harvest => ObservationSource.HarvestReceipt,
        ReceiptVerb.PotWater => ObservationSource.TendReceipt,
        _ => ObservationSource.MapSighting,
    };
}
