using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

public static partial class GardenBoard
{
    /// <summary>The household's goal crop, Thavnairian Onion (species index 50 in
    /// <c>Data/SpeciesIndex.json</c>): the end of the onion pipeline. Its harvests wear the onion mark
    /// on the History tab and its Yields row the same (rethink slice 12, spec ruling 14). Onion Prince
    /// and Wild Onion Set are other crops and take no mark.</summary>
    public const ushort GoalCrop = 50;

    /// <summary>Whether <paramref name="species"/> is the goal crop.</summary>
    public static bool IsGoalCrop(ushort species) => species == GoalCrop;

    /// <summary>Whether a History row wears the onion mark: a harvest of the goal crop, from a bed or a
    /// pot, by any hand, its yield heard or not. A sowing takes no mark.</summary>
    public static bool IsGoalCropHarvest(HistoryEntry row) => row.Kind == HistoryKind.Harvest && IsGoalCrop(row.Species);
}
