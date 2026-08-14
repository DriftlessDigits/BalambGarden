using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
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

    [Fact] // checkbox off: no new claims, receipt goes nowhere
    public void ClaimOnActionOffDoesNotClaim()
    {
        var engine = new CensusEngine(new LedgerStore()) { ClaimOnAction = false };
        engine.Bind(Chelsea, 0, 110);
        Assert.Null(engine.OnReceipt(Tend(3)));
        Assert.Empty(engine.LedgerBeds);
    }

    [Fact] // checkbox off but bed ALREADY claimed: observation still lands
    public void AlreadyClaimedBedStillObservesWithCheckboxOff()
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Chelsea, 0, 110);
        engine.OnReceipt(Tend(3));

        engine.ClaimOnAction = false;
        var bed = engine.OnReceipt(Tend(3, stage: 2));
        Assert.NotNull(bed);
        Assert.Equal(2, bed!.Ring.Count);
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
}
