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
    private readonly Dictionary<string, ushort> indexByName;
    private readonly Dictionary<uint, List<(uint, uint)>> pairsByResult;
    private readonly List<Soil> soils;
    private readonly List<string> nameCollisions = [];

    private DomainTables(
        Dictionary<uint, Crop> crops,
        Dictionary<ushort, uint> seedIdByIndex,
        Dictionary<ushort, string> nameByIndex,
        Dictionary<uint, List<(uint, uint)>> pairsByResult,
        List<Soil> soils)
    {
        this.cropsBySeedId = crops;
        this.seedIdByIndex = seedIdByIndex;
        this.nameByIndex = nameByIndex;
        this.pairsByResult = pairsByResult;
        this.soils = soils;
        indexBySeedId = seedIdByIndex.ToDictionary(kv => kv.Value, kv => kv.Key);

        indexByName = BuildNameIndex(nameByIndex, nameCollisions);
    }

    /// <summary>
    /// Name -> index, failing SOFT on duplicates. A ToDictionary here would throw, which
    /// turns a data-file typo (or a genuine game re-use of a display name) into a plugin
    /// that will not start at all - a table nobody can load is worse than a table with one
    /// ambiguous name in it. Lowest index wins, deterministically, and every collision is
    /// REPORTED (<see cref="SpeciesNameCollisions"/>) rather than swallowed.
    /// </summary>
    internal static Dictionary<string, ushort> BuildNameIndex(
        IReadOnlyDictionary<ushort, string> nameByIndex, List<string> collisions)
    {
        var index = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        foreach (var (species, name) in nameByIndex.OrderBy(kv => kv.Key))
        {
            if (index.TryGetValue(name, out var first))
            {
                collisions.Add($"'{name}' listed for species {first} and {species}; {first} wins");
                continue;
            }
            index[name] = species;
        }
        return index;
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
        // Every id the game can put in a bed is LISTED here, including ones we know
        // nothing about (id 108, seen in-game 08-13, is newer than the Lotlab snapshot):
        // a null seedId means "this species exists, we have no join for it", and the
        // lookups below answer null rather than inventing seed 0.
        //
        // The 08-15 name-variant gap CLOSED 08-16: bench evidence arrived. Color is pot
        // PIGMENT (b2 high nibble), not species - so species names here are the colorless
        // base plant ("Lupins"), Talk speaks colored ITEM names ("Blue Lupins"), and
        // SpeciesIndexByName strips one leading color word when the remainder is a real
        // species. Not an alias table: it can only ever land on a name already listed.
        foreach (var prop in ReadJson("Data.SpeciesIndex.json").RootElement.EnumerateObject())
        {
            var index = ushort.Parse(prop.Name);
            if (prop.Value.GetProperty("seedId") is { ValueKind: JsonValueKind.Number } seed)
                seedIdByIndex[index] = seed.GetUInt32();
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

        // Soils arrive already ordered by ItemId from the generator; the sort keeps the
        // table's order a property of this code, not of whatever the file happens to hold.
        var soils = new List<Soil>();
        foreach (var el in ReadJson("Data.Soils.json").RootElement.EnumerateArray())
        {
            soils.Add(new Soil(
                el.GetProperty("itemId").GetUInt32(),
                el.GetProperty("name").GetString()!,
                el.GetProperty("grade").GetInt32()));
        }

        soils.Sort((a, b) => a.ItemId.CompareTo(b.ItemId));

        return new DomainTables(crops, seedIdByIndex, nameByIndex, pairs, soils);
    }

    private static JsonDocument ReadJson(string logicalName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource missing: {logicalName}");
        return JsonDocument.Parse(stream);
    }

    public Crop? CropBySeedId(uint seedId) => cropsBySeedId.GetValueOrDefault(seedId);

    /// <summary>Every crop the table knows, name-ordered so a picker built from it is
    /// stable between sessions. Note the tail this does NOT contain: flowerpot flowers
    /// have no clocks and never made it into Crops.json (08-15 finding).</summary>
    public IReadOnlyList<Crop> Crops
        => cropsBySeedId.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

    public uint? SeedIdBySpeciesIndex(ushort index)
        => seedIdByIndex.TryGetValue(index, out var s) ? s : null;

    public ushort? SpeciesIndexBySeedId(uint seedId)
        => indexBySeedId.TryGetValue(seedId, out var i) ? i : null;

    public Crop? CropBySpeciesIndex(ushort index)
        => SeedIdBySpeciesIndex(index) is { } seed ? CropBySeedId(seed) : null;

    /// <summary>Honest fallback: unknown ids display as unknown, never guessed.</summary>
    public string SpeciesName(ushort index)
        => nameByIndex.GetValueOrDefault(index) ?? $"Unknown (0x{index:X2})";

    /// <summary>The whole flowerpot flower line grows in one day (community table,
    /// 2026-08-16; our own anchored pot plantings receipt the claim as they ripen).</summary>
    public const int FlowerGrowHours = 24;

    /// <summary>Grow hours for any species the tables can clock. A crop row keeps its own
    /// hours; a NAMED species with no crop row is a flowerpot flower (the crop table is
    /// outdoor data - flowers are a separate game system) and takes the flower line's
    /// shared 24h. An unnamed species gets null: no clock for a plant we cannot name.</summary>
    public int? GrowHours(ushort index)
    {
        if (CropBySpeciesIndex(index) is { } crop)
            return crop.GrowHours;
        return nameByIndex.ContainsKey(index) ? FlowerGrowHours : null;
    }

    /// <summary>Species whose display name is shared with an earlier index. Empty today;
    /// non-empty means <see cref="SpeciesIndexByName"/> can only answer for the first of
    /// them, and whoever loaded the tables should say so rather than pretend otherwise.</summary>
    public IReadOnlyList<string> SpeciesNameCollisions => nameCollisions;

    /// <summary>The game's pigment color vocabulary - the words a harvested flower's item
    /// name can lead with. Only used to STRIP: a match still requires the remainder to be
    /// a listed species, so this list can only reveal names, never invent them.</summary>
    private static readonly string[] PigmentWords =
        ["Red", "Orange", "Yellow", "Blue", "Purple", "White", "Pink", "Green", "Black", "Rainbow"];

    /// <summary>Talk names receipted in the field that differ from the species-table name
    /// (Talk speaks the harvest ITEM name). Receipts-only: one entry per proven string,
    /// never a pattern - and each maps to a LISTED species name, so an alias can reveal
    /// an index but never invent one.</summary>
    private static readonly Dictionary<string, string> TalkAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Drift's yard beds 08-16 13:46 (4x unknown-species warnings) + Gardener's /xllog.
        ["Royal Kukuru Bean"] = "Royal Kukuru",
    };

    /// <summary>Receipt joins: dialogue names a plant, the map speaks species indices.
    /// Exact match first; then the receipted alias table; else one leading pigment word
    /// strips ("Blue Lupins" -> Lupins, 08-16: color is pigment, not species).</summary>
    public ushort? SpeciesIndexByName(string name)
    {
        var trimmed = name.Trim();
        if (indexByName.TryGetValue(trimmed, out var i))
            return i;

        if (TalkAliases.TryGetValue(trimmed, out var canonical)
            && indexByName.TryGetValue(canonical, out var aliased))
            return aliased;

        foreach (var color in PigmentWords)
            if (trimmed.StartsWith(color + " ", StringComparison.OrdinalIgnoreCase)
                && indexByName.TryGetValue(trimmed[(color.Length + 1)..], out var stripped))
                return stripped;
        return null;
    }

    /// <summary>Every topsoil, ascending by ItemId (Grade 1-3 x La Noscean/Shroud/Thanalan).</summary>
    public IReadOnlyList<Soil> Soils => soils;

    /// <summary>Null for anything that is not a topsoil - inventory hands us arbitrary ids.</summary>
    public Soil? SoilByItemId(uint itemId) => soils.FirstOrDefault(s => s.ItemId == itemId);

    public IReadOnlyList<(uint ParentA, uint ParentB)> PairsForResult(uint resultSeedId)
        => pairsByResult.GetValueOrDefault(resultSeedId) ?? [];

    /// <summary>
    /// Order-insensitive cross lookup: every result A x B can produce. Parent pairs are
    /// genuinely multi-result (Royal Kukuru x Curiel Root yields both Apricot and
    /// Thavnairian Onion), so this returns all of them, sorted ascending by result seedId
    /// for determinism.
    /// </summary>
    public IReadOnlyList<uint> CrossResults(uint parentA, uint parentB)
    {
        var results = new List<uint>();
        foreach (var (result, list) in pairsByResult)
        {
            foreach (var (a, b) in list)
            {
                if ((a == parentA && b == parentB) || (a == parentB && b == parentA))
                {
                    results.Add(result);
                    break;
                }
            }
        }

        results.Sort();
        return results;
    }
}
