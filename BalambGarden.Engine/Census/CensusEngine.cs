using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Census;

/// <summary>The join + claim brain. Claim-on-action is the only claim path (spec Frame 3,
/// Sam's 08-13 design): you cannot claim what you cannot touch. No Claim() method exists.</summary>
public sealed class CensusEngine(LedgerStore ledger)
{
    public bool ClaimOnAction { get; set; } = true;

    public IReadOnlyList<ClaimedBed> LedgerBeds => ledger.Beds;

    public void Bind(EstateKey estate, int patchOrdinal, int mapKey)
        => ledger.Bindings[estate.BindingKey(patchOrdinal)] = mapKey;

    public int? BoundKey(EstateKey estate, int patchOrdinal)
        => ledger.Bindings.TryGetValue(estate.BindingKey(patchOrdinal), out var k) ? k : null;

    public ClaimedBed? OnReceipt(ReceiptEvent e)
    {
        if (BoundKey(e.Estate, e.PatchOrdinal) is not { } mapKey)
            return null;

        var bed = ledger.Beds.FirstOrDefault(b =>
            b.Estate == e.Estate && b.PatchOrdinal == e.PatchOrdinal && b.BedSlot == e.BedSlot);

        if (bed is null)
        {
            if (!ClaimOnAction)
                return null;
            bed = new ClaimedBed
            {
                Estate = e.Estate, MapKey = mapKey, PatchOrdinal = e.PatchOrdinal,
                BedSlot = e.BedSlot, IsPot = e.IsPot, ClaimedAt = e.At,
            };
            ledger.Beds.Add(bed);
        }

        bed.Observe(new Observation(e.At, e.SpeciesIndex, e.Stage, SourceFor(e.Verb)));
        if (e.Verb is ReceiptVerb.Tend or ReceiptVerb.PotWater)
            bed.LastTended = e.At;
        return bed;
    }

    /// <summary>Map sightings only ever land on already-claimed beds. Ward-visible
    /// unclaimed data is ephemeral by design (Sam's distance ruling, 08-12).</summary>
    public int OnMapSighting(
        EstateKey estate, int mapKey, IReadOnlyList<Sensing.BedReading> beds, DateTimeOffset at)
    {
        var count = 0;
        foreach (var reading in beds)
        {
            if (!reading.Occupied)
                continue;
            var bed = ledger.Beds.FirstOrDefault(b =>
                b.Estate == estate && b.MapKey == mapKey && b.BedSlot == reading.Slot);
            if (bed is null)
                continue;
            bed.Observe(new Observation(at, reading.SpeciesIndex, reading.Stage, ObservationSource.MapSighting));
            count++;
        }
        return count;
    }

    public void Abandon(ClaimedBed bed) => ledger.Beds.Remove(bed);

    private static ObservationSource SourceFor(ReceiptVerb verb) => verb switch
    {
        ReceiptVerb.Tend => ObservationSource.TendReceipt,
        ReceiptVerb.Plant => ObservationSource.PlantReceipt,
        ReceiptVerb.Harvest => ObservationSource.HarvestReceipt,
        ReceiptVerb.PotWater => ObservationSource.TendReceipt,
        _ => ObservationSource.MapSighting,
    };
}
