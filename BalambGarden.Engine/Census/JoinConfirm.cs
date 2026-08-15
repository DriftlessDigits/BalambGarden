using BalambGarden.Engine.Sensing;

namespace BalambGarden.Engine.Census;

/// <summary>The receipt half of the join: a shortlist proposes key sequences, a
/// completed interaction names (patch, bed, plant), and the one candidate whose map
/// data agrees is the binding. Anything short of exactly one survivor binds nothing.</summary>
public static class JoinConfirm
{
    /// <summary>One receipt's worth of evidence - the single-constraint case.</summary>
    public static IReadOnlyList<int>? Confirm(
        IReadOnlyList<IReadOnlyList<int>> candidates,
        int patchOrdinal, int bedSlot, ushort speciesIndex,
        Func<int, IReadOnlyList<BedReading>?> mapByKey)
        => Confirm(candidates, patchOrdinal, [(bedSlot, speciesIndex)], mapByKey);

    /// <summary>Evidence accumulates (08-14 bench: on a small estate one receipt's
    /// slot+species left several ward keys standing, so all eight receipts failed
    /// individually). A candidate survives only if its key at this ordinal shows EVERY
    /// (slot, species) constraint, occupied. Exactly one survivor binds; anything short
    /// of that still binds nothing.</summary>
    public static IReadOnlyList<int>? Confirm(
        IReadOnlyList<IReadOnlyList<int>> candidates,
        int patchOrdinal, IReadOnlyList<(int BedSlot, ushort SpeciesIndex)> constraints,
        Func<int, IReadOnlyList<BedReading>?> mapByKey)
    {
        if (constraints.Count == 0)
            return null;

        var survivors = candidates.Where(c =>
        {
            if (patchOrdinal >= c.Count)
                return false;
            var beds = mapByKey(c[patchOrdinal]);
            if (beds is null)
                return false;
            foreach (var (slot, species) in constraints)
            {
                if (slot >= beds.Count)
                    return false;
                var reading = beds[slot];
                if (!reading.Occupied || reading.SpeciesIndex != species)
                    return false;
            }
            return true;
        }).ToList();

        return survivors.Count == 1 ? survivors[0] : null;
    }
}
