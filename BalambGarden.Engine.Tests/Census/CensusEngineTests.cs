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
}
