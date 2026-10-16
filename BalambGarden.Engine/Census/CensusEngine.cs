using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Census;

/// <summary>A sighted state CHANGE on a claimed row (piece 6, schema ruling 12: observations
/// sync on change only). Stage = a climb; Ripe = reaching 4, the payoff signal. Emitted from
/// the one place prev-vs-reading already meet; the pump decides whether it becomes a logged
/// receipt (it may lack a patch id or house id to mint the wire identity with).</summary>
/// <para><see cref="Ordinal"/> is the row's own patch ordinal (the pot's map key when
/// IsPot) - carried so the pump routes the change without re-deriving what this engine
/// already knew (QA pass 2026-08-29).</para>
public sealed record SightingChange(int Ordinal, int Slot, Sync.SyncKind Kind, ushort Species, byte Stage);

/// <summary>The join + census brain. Receipts and sightings are the only write paths (spec:
/// Permission Architecture, 2026-08-15): a row exists because the game showed us something at
/// an estate we are rostered for. No Claim() method exists.
///
/// <para>Every path that can reset a row's planting takes an optional <c>rebases</c> sink
/// (ruled 2026-10-14: rebase is reasoned and logged). The Engine owns no logger; the pump logs
/// each event after its save and appends it to the trail.</para></summary>
public sealed class CensusEngine(LedgerStore ledger)
{
    public IReadOnlyList<ClaimedBed> LedgerBeds => ledger.Beds;

    public void Bind(EstateKey estate, int patchOrdinal, int mapKey, bool isPot = false)
        => ledger.Bindings[estate.BindingKey(patchOrdinal, isPot)] = mapKey;

    public int? BoundKey(EstateKey estate, int patchOrdinal, bool isPot = false)
        => ledger.Bindings.TryGetValue(estate.BindingKey(patchOrdinal, isPot), out var k) ? k : null;

    /// <summary>An own act on this install (chain or chat). Harvest = the emptying witness
    /// (ruled 2026-08-28): the game's own line said the crop left. Plant = a new planting at the
    /// receipt's instant. Tend / PotWater = the water clock. Fertilize proves the row and
    /// contributes nothing (a feed is not a reading). Own hands are never fenced and never
    /// "older" - see <see cref="ClaimedBed"/>.</summary>
    public ClaimedBed? OnReceipt(ReceiptEvent e, ICollection<RebaseEvent>? rebases = null)
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

        switch (e.Verb)
        {
            case ReceiptVerb.Harvest:
                Push(rebases, bed.Rebase(RebaseReason.HarvestWitnessed, cut: e.At, by: Hand.Own));
                break;
            case ReceiptVerb.Plant:
                Push(rebases, bed.Plant(e.At, e.SpeciesIndex, e.Stage, Hand.Own, out _));
                break;
            case ReceiptVerb.Tend:
            case ReceiptVerb.PotWater:
                bed.Tend(e.At, Hand.Own, e.SpeciesIndex);
                break;
        }
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
        bool isPot = false, bool mayRecord = false, ICollection<SightingChange>? changes = null,
        ICollection<RebaseEvent>? rebases = null)
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
            // read against a row with a planting means the plant left while nobody watched -
            // the row empties and stays. Rebase is destructive, so it needs the same vouch
            // as creation; an empty slot with no row is still nothing to record.
            if (!reading.Occupied)
            {
                if (mayRecord && bed is { HasPlanting: true })
                {
                    Push(rebases, bed.Rebase(RebaseReason.ReadsEmpty, cut: at));
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
            else if (mayRecord && bed.Contradicts(reading.SpeciesIndex, reading.Stage))
            {
                // New planting. The cut is the OLD plant's newest evidence - our last look at it -
                // so a watering newer than that look survives (Q1: keep the watering) unless it
                // named the old species (the apple->fig scene).
                Push(rebases, bed.Rebase(RebaseReason.NewPlantingSighted,
                    cut: bed.NewestEvidenceAt ?? at, newSpecies: reading.SpeciesIndex));
            }

            // The change reads BEFORE the sight, against what the ledger believed walking
            // in. A rebase above already nulled Latest, so a replant emits nothing - its
            // story restarts with its own plant receipt. A reading of stage 0 is unknown and
            // never a change; a climb FROM a stage-0 belief (a plant receipt's honest
            // unknown) does emit - first real knowledge travels (the 08-28 more-data
            // amendment: richer receipts serve the fresh-install replay). A brand-new row
            // has no belief at all and emits nothing.
            if (mayRecord && changes is not null && bed.Latest is { } prev
                && prev.Stage != reading.Stage && reading.Stage > 0)
            {
                changes.Add(new SightingChange(
                    bed.PatchOrdinal, reading.Slot,
                    reading.Stage == GardenStage.Ripe ? Sync.SyncKind.Ripe : Sync.SyncKind.Stage,
                    reading.SpeciesIndex, reading.Stage));
            }

            bed.Sight(at, reading.SpeciesIndex, reading.Stage, ObservationSource.MapSighting);
            count++;
        }
        return count;
    }

    public void Abandon(ClaimedBed bed) => ledger.Beds.Remove(bed);

    /// <summary>A pot's map entry vanishing is what a HARVEST looks like (08-15 receipt),
    /// so a recorded pot with a planting whose key is missing from a settled read has been
    /// emptied behind our back - it rebases like any other content mismatch (2026-08-18).
    /// The CALLER vouches the read is settled (SawHousingObjects) and roster-covered;
    /// this only compares. Rows already empty are already right. Returns rebased count.</summary>
    public int ReconcileAbsentPots(
        EstateKey estate, IReadOnlyCollection<int> presentKeys, DateTimeOffset at,
        ICollection<RebaseEvent>? rebases = null)
    {
        var count = 0;
        foreach (var bed in ledger.Beds.Where(b =>
            b.Estate == estate && b.IsPot && b.HasPlanting
            && !presentKeys.Contains(b.MapKey)))
        {
            Push(rebases, bed.Rebase(RebaseReason.PotAbsent, cut: at));
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

    private static void Push(ICollection<RebaseEvent>? sink, RebaseEvent? ev)
    {
        if (ev is not null)
            sink?.Add(ev);
    }
}
