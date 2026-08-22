using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>What one cell of the patch strip is saying. Kept coarse on purpose: the strip
/// is a summary layer, and the bed grid underneath is still the reading that carries the
/// claim. <see cref="Unclaimed"/> is "the ledger has nothing for this slot", which is a
/// different silence from <see cref="Unknown"/> ("claimed, but nothing identified").</summary>
public enum CellFill { Unclaimed, Unknown, Growing, Ripe }

/// <summary>One bed of a patch as the strip sees it. Stage is the raw sighting (0-4) so a
/// caller can shade a ramp; it is 0 for a slot with no reading at all, and <see cref="Fill"/>
/// is what says whether that 0 means anything.</summary>
public sealed record StripCell(int Slot, CellFill Fill, byte Stage, WaterState Water);

/// <summary>
/// The patch strip: one cell per bed slot, in slot order, whether or not the ledger has
/// anything for that slot. Pure shaping of what <see cref="Rollups"/> already counts -
/// the strip shows the same census spread out instead of summed, so the two surfaces can
/// never disagree.
/// </summary>
public static class PatchStrip
{
    /// <summary>Every outdoor patch is eight beds. A patch strip is always eight cells
    /// wide, so an empty slot reads as a hole rather than as a shorter patch.</summary>
    public const int Slots = 8;

    /// <summary>Cells for one patch's claimed beds. The caller filters to the patch; this
    /// only lays them out by slot. Water follows the same rule as everywhere else: a pot
    /// is NotApplicable (pot wilt is observed off the live map, never clocked), a bed
    /// whose crop we cannot identify is Unknown, and an unclaimed slot has no water claim
    /// to make at all.</summary>
    public static IReadOnlyList<StripCell> ForPatch(
        IReadOnlyList<ClaimedBed> beds, DomainTables tables, IWiltSource wilt,
        DateTimeOffset now, int slots = Slots)
    {
        var cells = new List<StripCell>(slots);
        for (var slot = 0; slot < slots; slot++)
        {
            var bed = beds.FirstOrDefault(b => b.BedSlot == slot);
            if (bed is null)
            {
                cells.Add(new StripCell(slot, CellFill.Unclaimed, 0, WaterState.NotApplicable));
                continue;
            }

            var latest = bed.Latest;
            var crop = latest is null ? null : tables.CropBySpeciesIndex(latest.SpeciesIndex);
            var water = bed.IsPot ? WaterState.NotApplicable
                : crop is null ? WaterState.Unknown
                : wilt.StateFor(bed, crop, now);

            var fill = latest is null ? CellFill.Unknown
                : latest.Stage >= 4 ? CellFill.Ripe
                : CellFill.Growing;

            cells.Add(new StripCell(slot, fill, latest?.Stage ?? 0, water));
        }

        return cells;
    }

    /// <summary>Cells for an estate's pots: one per pot in map-key order (Slot carries the
    /// map key - the one number a pot has), no Unclaimed holes because a pot row only
    /// exists once recorded. Pot wilt is OBSERVED, never predicted: the live map's b4=1
    /// (the wiltingKeys set) is the only water claim a pot cell can make - Danger, because
    /// the game itself says water now. Everything else is NotApplicable.</summary>
    public static IReadOnlyList<StripCell> ForPots(
        IReadOnlyList<ClaimedBed> pots, IReadOnlySet<int> wiltingKeys)
    {
        return pots
            .OrderBy(p => p.MapKey)
            .Select(pot =>
            {
                var latest = pot.Latest;
                var fill = latest is null ? CellFill.Unknown
                    : latest.Stage >= 4 ? CellFill.Ripe
                    : CellFill.Growing;
                var water = wiltingKeys.Contains(pot.MapKey)
                    ? WaterState.Danger
                    : WaterState.NotApplicable;
                return new StripCell(pot.MapKey, fill, latest?.Stage ?? 0, water);
            })
            .ToList();
    }
}
