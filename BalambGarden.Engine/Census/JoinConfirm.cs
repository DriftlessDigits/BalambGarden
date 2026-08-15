using BalambGarden.Engine.Sensing;

namespace BalambGarden.Engine.Census;

/// <summary>The receipt half of the join: a shortlist proposes key sequences, a
/// completed interaction names (patch, bed, plant), and the one candidate whose map
/// data agrees is the binding. Anything short of exactly one survivor binds nothing.</summary>
public static class JoinConfirm
{
    public static IReadOnlyList<int>? Confirm(
        IReadOnlyList<IReadOnlyList<int>> candidates,
        int patchOrdinal, int bedSlot, ushort speciesIndex,
        Func<int, IReadOnlyList<BedReading>?> mapByKey)
    {
        var survivors = candidates.Where(c =>
        {
            if (patchOrdinal >= c.Count)
                return false;
            var beds = mapByKey(c[patchOrdinal]);
            if (beds is null || bedSlot >= beds.Count)
                return false;
            var reading = beds[bedSlot];
            return reading.Occupied && reading.SpeciesIndex == speciesIndex;
        }).ToList();

        return survivors.Count == 1 ? survivors[0] : null;
    }
}
