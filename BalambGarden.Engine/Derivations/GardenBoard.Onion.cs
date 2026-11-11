using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

public static partial class GardenBoard
{
    /// <summary>The household's goal crop, Thavnairian Onion (species index 50 in
    /// <c>Data/SpeciesIndex.json</c>): the end of the onion pipeline. Its harvests wear the gold mark
    /// on the History tab and its Yields row the same (rethink slice 12, spec ruling 14). Onion Prince
    /// and Wild Onion Set are other crops and take no mark.</summary>
    public const ushort Onion = 50;

    /// <summary>Whether <paramref name="species"/> is the goal crop.</summary>
    public static bool IsOnion(ushort species) => species == Onion;

    /// <summary>Whether a History row wears the gold mark: a harvest of the goal crop, from a bed or a
    /// pot, by any hand, its yield heard or not. A sowing takes no mark.</summary>
    public static bool IsOnionHarvest(HistoryEntry row) => row.Kind == HistoryKind.Harvest && IsOnion(row.Species);
}
