using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

public class CensusEngineTests
{
    private static readonly EstateKey Chelsea = new(340, 11, 32);
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-13T19:00:00Z");

    private static ReceiptEvent Tend(int slot, byte stage = 1) =>
        new(Chelsea, PatchOrdinal: 0, BedSlot: slot, ReceiptVerb.Tend,
            SpeciesIndex: 0x41, Stage: stage, At: T0);

    [Fact] // claim-on-action: a completed tend on a bound patch claims the bed
    public void TendReceiptClaimsAndObserves()
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Chelsea, 0, mapKey: 110);

        var bed = engine.OnReceipt(Tend(slot: 3));

        Assert.NotNull(bed);
        Assert.Equal(110, bed!.MapKey);
        Assert.Equal(3, bed.BedSlot);
        Assert.Equal(T0, bed.LastTended);
        var obs = Assert.Single(bed.Ring);
        Assert.Equal(ObservationSource.TendReceipt, obs.Source);
    }

    [Fact] // no binding = no claim: a receipt can't attach to a patch we can't identify
    public void UnboundPatchReceiptDoesNotClaim()
    {
        var engine = new CensusEngine(new LedgerStore());
        Assert.Null(engine.OnReceipt(Tend(3)));
        Assert.Empty(engine.LedgerBeds);
    }

    [Fact] // re-binding overwrites: mismatch triggers re-bind, never silent trust
    public void RebindOverwrites()
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Chelsea, 0, 110);
        engine.Bind(Chelsea, 0, 116);
        Assert.Equal(116, engine.BoundKey(Chelsea, 0));
    }

    [Fact]
    public void AbandonRemoves()
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Chelsea, 0, 110);
        var bed = engine.OnReceipt(Tend(3))!;
        engine.Abandon(bed);
        Assert.Empty(engine.LedgerBeds);
    }

    [Fact] // same bed, second receipt: one record, two observations - never duplicates
    public void SecondReceiptOnSameBedDoesNotDuplicate()
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Chelsea, 0, 110);
        engine.OnReceipt(Tend(3));
        engine.OnReceipt(Tend(3, stage: 2));
        Assert.Single(engine.LedgerBeds);
    }

    [Fact] // sightings feed claimed beds; unclaimed ward data stays ephemeral
    public void MapSightingObservesClaimedOnly()
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Chelsea, 0, 110);
        engine.OnReceipt(Tend(3));   // claims slot 3 only

        var readings = Enumerable.Range(0, 8)
            .Select(i => new BalambGarden.Engine.Sensing.BedReading(
                i, (ushort)(i % 2 == 0 ? 0x41 : 0x11), 2, 0, true))
            .ToList();

        var count = engine.OnMapSighting(Chelsea, mapKey: 110, readings, T0.AddDays(1));

        Assert.Equal(1, count);   // only the claimed bed
        var bed = Assert.Single(engine.LedgerBeds);
        Assert.Equal(2, bed.Ring.Count);
        Assert.Equal(ObservationSource.MapSighting, bed.Latest!.Source);
        Assert.Equal(2, bed.Latest.Stage);
    }

    [Fact] // sighting for a different key does not touch this bed
    public void MapSightingWrongKeyIgnored()
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Chelsea, 0, 110);
        engine.OnReceipt(Tend(3));
        var readings = new List<BalambGarden.Engine.Sensing.BedReading>
            { new(3, 0x41, 3, 0, true) };
        Assert.Equal(0, engine.OnMapSighting(Chelsea, mapKey: 116, readings, T0.AddDays(1)));
    }

    [Fact]
    public void SightingCreatesRowsForABoundOutdoorKey()
    {
        var store = new LedgerStore();
        var engine = new CensusEngine(store);
        var estate = new EstateKey(340, 11, 32);
        engine.Bind(estate, patchOrdinal: 1, mapKey: 116);

        var landed = engine.OnMapSighting(estate, 116,
            [new BedReading(0, 0x41, 4, 0, true), new BedReading(1, 0x11, 4, 0, true)],
            DateTimeOffset.UtcNow, mayRecord: true);

        Assert.Equal(2, landed);
        Assert.Equal(2, store.Beds.Count);
        Assert.All(store.Beds, b => Assert.Equal(1, b.PatchOrdinal));
        Assert.Equal(4, store.Beds[0].Latest!.Stage);
    }

    [Fact]
    public void SightingNeverCreatesRowsForAnUnboundKey()   // ward-visible neighbor data stays ephemeral
    {
        var store = new LedgerStore();
        var engine = new CensusEngine(store);
        var landed = engine.OnMapSighting(new EstateKey(340, 11, 32), 62,
            [new BedReading(0, 0x41, 4, 0, true)], DateTimeOffset.UtcNow, mayRecord: true);
        Assert.Equal(0, landed);
        Assert.Empty(store.Beds);
    }

    [Fact]
    public void SightingWithoutRecordRightsOnlyUpdatesExistingRows()
    {
        var store = new LedgerStore();
        var engine = new CensusEngine(store);
        var estate = new EstateKey(340, 11, 32);
        engine.Bind(estate, 0, 110);
        engine.OnMapSighting(estate, 110,
            [new BedReading(0, 0x41, 2, 0, true)], DateTimeOffset.UtcNow, mayRecord: false);
        Assert.Empty(store.Beds);
    }

    [Fact]
    public void PotSightingBindsAndCreatesItsOwnRow()   // indoor map is house-scoped (08-13); idx==key (08-15)
    {
        var store = new LedgerStore();
        var engine = new CensusEngine(store);
        var estate = EstateKey.Apartment(979, 7, 0, 29);
        var landed = engine.OnMapSighting(estate, 0,
            [new BedReading(0, 44, 2, 0, true)], DateTimeOffset.UtcNow, isPot: true, mayRecord: true);
        Assert.Equal(1, landed);
        Assert.Equal(0, engine.BoundKey(estate, 0, isPot: true));
        var bed = Assert.Single(store.Beds);
        Assert.True(bed.IsPot);
    }

    [Fact]
    public void EmptyBedsCreateNoRows()   // a bare bed has nothing to record
    {
        var store = new LedgerStore();
        var engine = new CensusEngine(store);
        var estate = new EstateKey(340, 11, 32);
        engine.Bind(estate, 0, 110);
        engine.OnMapSighting(estate, 110,
            [new BedReading(0, 0, 0, 0, false)], DateTimeOffset.UtcNow, mayRecord: true);
        Assert.Empty(store.Beds);
    }

    [Fact] // pot-gate (08-16): when the furniture vector disowns a key, its row and binding die
    public void PrunePhantomPotsRemovesRowsAndBindings()
    {
        var store = new LedgerStore();
        var engine = new CensusEngine(store);
        var estate = new EstateKey(344, 24, 41);
        foreach (var key in new[] { 126, 127, 128, 129, 117, 194 })   // 4 real + 2 phantoms
            engine.OnMapSighting(estate, key, [new BedReading(0, 44, 3, 0, true)],
                T0, isPot: true, mayRecord: true);

        var pruned = engine.PrunePhantomPots(estate, [117, 194]);

        Assert.Equal(2, pruned);
        Assert.Equal(4, store.Beds.Count);
        Assert.All(store.Beds, b => Assert.Contains(b.MapKey, new[] { 126, 127, 128, 129 }));
        Assert.Null(engine.BoundKey(estate, 117, isPot: true));
        Assert.NotNull(engine.BoundKey(estate, 126, isPot: true));
    }

    [Fact] // prune speaks the pot namespace only - outdoor twins of the number are untouched
    public void PrunePhantomPotsLeavesOutdoorAndOtherEstatesAlone()
    {
        var store = new LedgerStore();
        var engine = new CensusEngine(store);
        var estate = new EstateKey(344, 24, 41);
        var elsewhere = new EstateKey(649, 4, 52);
        engine.Bind(estate, 0, mapKey: 117);   // outdoor patch that happens to share the number
        engine.OnReceipt(new ReceiptEvent(estate, 0, 0, ReceiptVerb.Tend, 0x41, 2, T0));
        engine.OnMapSighting(elsewhere, 117, [new BedReading(0, 44, 3, 0, true)],
            T0, isPot: true, mayRecord: true);

        var pruned = engine.PrunePhantomPots(estate, [117]);

        Assert.Equal(0, pruned);
        Assert.Equal(2, store.Beds.Count);
        Assert.Equal(117, engine.BoundKey(estate, 0));
        Assert.NotNull(engine.BoundKey(elsewhere, 117, isPot: true));
    }

    [Fact]
    public void ReceiptAlwaysCreatesTheRow()   // the ClaimOnAction=false path is gone
    {
        var store = new LedgerStore();
        var engine = new CensusEngine(store);
        var estate = new EstateKey(340, 11, 32);
        engine.Bind(estate, 0, 110);
        var bed = engine.OnReceipt(new ReceiptEvent(estate, 0, 0, ReceiptVerb.Tend, 0x41, 2, DateTimeOffset.UtcNow));
        Assert.NotNull(bed);
    }

    // ---------------------------------------------------------------- reconcile
    // 2026-08-18 ruling: the game wins on content mismatch. A live read that contradicts
    // the ledger's idea of what is growing means the row's tenancy changed while nobody
    // was watching (a housemate harvested or replanted) - the old ring and tend clock
    // describe a plant that is gone, so they rebase rather than argue.

    private static ClaimedBed TendedBed(CensusEngine engine, byte stage = 2)
    {
        engine.Bind(Chelsea, 0, 110);
        return engine.OnReceipt(Tend(slot: 3, stage))!;
    }

    [Fact] // different species live = new tenancy: ring and tend clock rebase, row survives
    public void SightingWithDifferentSpeciesRebasesTheRow()
    {
        var engine = new CensusEngine(new LedgerStore());
        var bed = TendedBed(engine);

        engine.OnMapSighting(Chelsea, 110,
            [new BedReading(3, 0x30, 1, 0, true)], T0.AddDays(1), mayRecord: true);

        Assert.Single(engine.LedgerBeds);
        var obs = Assert.Single(bed.Ring);           // old observations left with the old plant
        Assert.Equal(0x30, obs.SpeciesIndex);
        Assert.Null(bed.LastTended);                 // our watering receipt watered the OLD plant
    }

    [Fact] // stage went BACKWARD on the same species = replanted same crop: also a new tenancy
    public void SightingWithRegressedStageRebasesTheRow()
    {
        var engine = new CensusEngine(new LedgerStore());
        var bed = TendedBed(engine, stage: 3);

        engine.OnMapSighting(Chelsea, 110,
            [new BedReading(3, 0x41, 1, 0, true)], T0.AddDays(1), mayRecord: true);

        var obs = Assert.Single(bed.Ring);
        Assert.Equal(1, obs.Stage);
        Assert.Null(bed.LastTended);
    }

    [Fact] // same species, stage moved forward: normal life - ring grows, anchors survive
    public void SightingThatAgreesKeepsRingAndTendClock()
    {
        var engine = new CensusEngine(new LedgerStore());
        var bed = TendedBed(engine, stage: 2);

        engine.OnMapSighting(Chelsea, 110,
            [new BedReading(3, 0x41, 3, 0, true)], T0.AddDays(1), mayRecord: true);

        Assert.Equal(2, bed.Ring.Count);
        Assert.Equal(T0, bed.LastTended);
    }

    [Fact] // reads empty on a row with contents: the plant is gone; the row empties, stays
    public void SightingThatReadsEmptyEmptiesTheRow()
    {
        var engine = new CensusEngine(new LedgerStore());
        var bed = TendedBed(engine);

        var landed = engine.OnMapSighting(Chelsea, 110,
            [new BedReading(3, 0, 0, 0, Occupied: false)], T0.AddDays(1), mayRecord: true);

        Assert.Single(engine.LedgerBeds);            // the bed is still ours...
        Assert.Empty(bed.Ring);                      // ...but nothing grows in it
        Assert.Null(bed.LastTended);
        Assert.Equal(1, landed);                     // the rebase is a change worth saving
    }

    [Fact] // an empty read against an already-empty row is silence, not a change
    public void EmptyReadOnEmptyRowLandsNothing()
    {
        var engine = new CensusEngine(new LedgerStore());
        var bed = TendedBed(engine);
        engine.OnMapSighting(Chelsea, 110,
            [new BedReading(3, 0, 0, 0, Occupied: false)], T0.AddDays(1), mayRecord: true);

        var landed = engine.OnMapSighting(Chelsea, 110,
            [new BedReading(3, 0, 0, 0, Occupied: false)], T0.AddDays(2), mayRecord: true);

        Assert.Equal(0, landed);
        Assert.Empty(bed.Ring);
    }

    [Fact] // rebase is destructive, so it gates on mayRecord; unvouched sightings stay
    public void ReconcileRequiresMayRecord()      // additive-only (existing rows still observe)
    {
        var engine = new CensusEngine(new LedgerStore());
        var bed = TendedBed(engine);

        engine.OnMapSighting(Chelsea, 110,
            [new BedReading(3, 0, 0, 0, Occupied: false)], T0.AddDays(1));
        engine.OnMapSighting(Chelsea, 110,
            [new BedReading(3, 0x30, 1, 0, true)], T0.AddDays(1));

        Assert.Equal(2, bed.Ring.Count);             // observed, never rebased
        Assert.Equal(T0, bed.LastTended);            // the tend clock survived
    }

    [Fact] // a harvested pot's map entry VANISHES (08-15): absent from a settled read = emptied
    public void AbsentPotKeyRebasesItsRow()
    {
        var engine = new CensusEngine(new LedgerStore());
        var estate = new EstateKey(344, 24, 41);
        foreach (var key in new[] { 126, 127 })
            engine.OnMapSighting(estate, key, [new BedReading(0, 44, 3, 0, true)],
                T0, isPot: true, mayRecord: true);

        var rebased = engine.ReconcileAbsentPots(estate, presentKeys: [127]);

        Assert.Equal(1, rebased);
        Assert.Equal(2, engine.LedgerBeds.Count);    // both rows survive
        Assert.Empty(engine.LedgerBeds.First(b => b.MapKey == 126).Ring);
        Assert.NotEmpty(engine.LedgerBeds.First(b => b.MapKey == 127).Ring);
    }

    [Fact] // an already-empty row is already right - absence is not news twice
    public void AbsentPotReconcileIsIdempotent()
    {
        var engine = new CensusEngine(new LedgerStore());
        var estate = new EstateKey(344, 24, 41);
        engine.OnMapSighting(estate, 126, [new BedReading(0, 44, 3, 0, true)],
            T0, isPot: true, mayRecord: true);
        engine.ReconcileAbsentPots(estate, presentKeys: []);

        Assert.Equal(0, engine.ReconcileAbsentPots(estate, presentKeys: []));
    }

    [Fact] // unknown species live (0) is a shrug, not a contradiction - never rebases
    public void UnknownLiveSpeciesDoesNotRebase()
    {
        var engine = new CensusEngine(new LedgerStore());
        var bed = TendedBed(engine, stage: 2);

        engine.OnMapSighting(Chelsea, 110,
            [new BedReading(3, 0, 2, 0, true)], T0.AddDays(1), mayRecord: true);

        Assert.Equal(2, bed.Ring.Count);
        Assert.Equal(T0, bed.LastTended);
    }
}
