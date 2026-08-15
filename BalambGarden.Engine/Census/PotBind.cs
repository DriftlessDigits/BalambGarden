using BalambGarden.Engine.Sensing;

namespace BalambGarden.Engine.Census;

/// <summary>Indoor pot binding by species uniqueness (the 08-13 sunflower receipt
/// pattern): a pot receipt plus exactly one indoor key showing that species is a
/// bind; two pots of the same species stay honestly unbound.</summary>
public static class PotBind
{
    public static int? UniqueSpeciesKey(
        ushort speciesIndex, IReadOnlyDictionary<int, PotReading> indoorMap)
    {
        var matches = indoorMap
            .Where(kv => kv.Value.Occupied && kv.Value.SpeciesIndex == speciesIndex)
            .Select(kv => kv.Key)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
