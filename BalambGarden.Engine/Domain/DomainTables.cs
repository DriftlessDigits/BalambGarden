using System.Reflection;
using System.Text.Json;

namespace BalambGarden.Engine.Domain;

/// <summary>Frozen gardening domain data, embedded at build time from Data/*.json.</summary>
public sealed class DomainTables
{
    private readonly Dictionary<uint, Crop> cropsBySeedId;
    private readonly Dictionary<ushort, uint> seedIdByIndex;
    private readonly Dictionary<uint, ushort> indexBySeedId;
    private readonly Dictionary<ushort, string> nameByIndex;
    private readonly Dictionary<uint, List<(uint, uint)>> pairsByResult;

    private DomainTables(
        Dictionary<uint, Crop> crops,
        Dictionary<ushort, uint> seedIdByIndex,
        Dictionary<ushort, string> nameByIndex,
        Dictionary<uint, List<(uint, uint)>> pairsByResult)
    {
        this.cropsBySeedId = crops;
        this.seedIdByIndex = seedIdByIndex;
        this.nameByIndex = nameByIndex;
        this.pairsByResult = pairsByResult;
        indexBySeedId = seedIdByIndex.ToDictionary(kv => kv.Value, kv => kv.Key);
    }

    public static DomainTables Load()
    {
        var crops = new Dictionary<uint, Crop>();
        foreach (var el in ReadJson("Data.Crops.json").RootElement.EnumerateArray())
        {
            var crop = new Crop(
                el.GetProperty("name").GetString()!,
                el.GetProperty("growHours").GetInt32(),
                el.GetProperty("wiltHours").GetInt32(),
                el.GetProperty("witherHours").GetInt32(),
                el.GetProperty("itemId").GetUInt32(),
                el.GetProperty("seedId").GetUInt32(),
                el.GetProperty("seedName").GetString() ?? "",
                el.GetProperty("crossable").GetBoolean());
            crops[crop.SeedId] = crop;
        }

        var seedIdByIndex = new Dictionary<ushort, uint>();
        var nameByIndex = new Dictionary<ushort, string>();
        foreach (var prop in ReadJson("Data.SpeciesIndex.json").RootElement.EnumerateObject())
        {
            var index = ushort.Parse(prop.Name);
            seedIdByIndex[index] = prop.Value.GetProperty("seedId").GetUInt32();
            if (prop.Value.GetProperty("name").GetString() is { Length: > 0 } name)
                nameByIndex[index] = name;
        }

        var pairs = new Dictionary<uint, List<(uint, uint)>>();
        foreach (var prop in ReadJson("Data.CrossbreedPairs.json").RootElement.EnumerateObject())
        {
            var result = uint.Parse(prop.Name);
            var list = new List<(uint, uint)>();
            foreach (var pair in prop.Value.EnumerateArray())
                list.Add((pair[0].GetUInt32(), pair[1].GetUInt32()));
            pairs[result] = list;
        }

        return new DomainTables(crops, seedIdByIndex, nameByIndex, pairs);
    }

    private static JsonDocument ReadJson(string logicalName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource missing: {logicalName}");
        return JsonDocument.Parse(stream);
    }

    public Crop? CropBySeedId(uint seedId) => cropsBySeedId.GetValueOrDefault(seedId);

    public uint? SeedIdBySpeciesIndex(ushort index)
        => seedIdByIndex.TryGetValue(index, out var s) ? s : null;

    public ushort? SpeciesIndexBySeedId(uint seedId)
        => indexBySeedId.TryGetValue(seedId, out var i) ? i : null;

    public Crop? CropBySpeciesIndex(ushort index)
        => SeedIdBySpeciesIndex(index) is { } seed ? CropBySeedId(seed) : null;

    /// <summary>Honest fallback: unknown ids display as unknown, never guessed.</summary>
    public string SpeciesName(ushort index)
        => nameByIndex.GetValueOrDefault(index) ?? $"Unknown (0x{index:X2})";

    public IReadOnlyList<(uint ParentA, uint ParentB)> PairsForResult(uint resultSeedId)
        => pairsByResult.GetValueOrDefault(resultSeedId) ?? [];

    /// <summary>Order-insensitive cross lookup: what does A x B produce, if anything?</summary>
    public uint? CrossResult(uint parentA, uint parentB)
    {
        foreach (var (result, list) in pairsByResult)
            foreach (var (a, b) in list)
                if ((a == parentA && b == parentB) || (a == parentB && b == parentA))
                    return result;
        return null;
    }
}
