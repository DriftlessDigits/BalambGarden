using BalambGarden.Engine.Sensing;

namespace BalambGarden.Engine.Sync;

/// <summary>The patch at sow (v0.4.1, Sow Capture design, ratified 2026-11-07): what all eight
/// beds of a patch held at the instant one of them was sown, taken from the map read the sow
/// already had in hand. The community rule says the cross is decided at the sow, by one neighbor,
/// so the sow row is where the evidence belongs.
///
/// <para>RAW, BY SLOT, NO ADJACENCY. Each bed is copied exactly as the map gave it - occupied,
/// species number, stage - because two questions are still open (whether a flower reads species
/// 0, whether a seedling counts as a neighbor) and the raw copy keeps both answerable. Which bed
/// touches which is the layout's question, applied at read time, never baked into a row. The sown
/// bed's own entry is kept raw as well; readers ignore it for neighbors.</para>
///
/// <para>THREE STATES: no read (or an empty one) is ABSENT - the sow is still a fact, its patch
/// simply was not heard - never an empty list a reader could mistake for an empty patch.</para></summary>
public static class PatchAtSow
{
    public static IReadOnlyList<SyncPatchBed>? From(IReadOnlyList<BedReading>? beds)
        => beds is not { Count: > 0 }
            ? null
            : beds.OrderBy(b => b.Slot)
                .Select(b => new SyncPatchBed(b.Slot, b.Occupied, b.SpeciesIndex, b.Stage))
                .ToList();
}
