# Balamb Garden Rebuild - Plan A: Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the game-free engine of the Balamb Garden rebuild - domain tables, pure sensor decoders, census join/claim, ledger, and derivations (timers, wilt, rollups, pipeline tips) - fully unit-tested against real capture fixtures.

**Architecture:** New Dalamud-free class library `BalambGarden.Engine` holding spec layers 2-4 plus the pure decode halves of layer 1; the existing plugin project references it. Tests live in `BalambGarden.Engine.Tests` (xUnit) and use byte sequences copied verbatim from the 2026-08-12/13 probe captures as fixtures. Game-facing adapters, chains, and UI are **Plan B** (separate plan, written after this lands).

**Tech Stack:** C# / .NET 9 (`net9.0`), xUnit, System.Text.Json. No Dalamud or ECommons references anywhere in Engine or its tests.

**Spec:** `C:\Obsidian\Book Of Holding\Planet Express\Deliveries\Balamb Garden\Balamb Garden - Rebuild Spec.md` (approved 2026-08-13). The three JSON domain tables live in `Data/` at the repo root; their shapes are documented in Task 2.

## Global Constraints

- Work on the `rebuild` branch (create from `main` in Task 1). Never commit to `main`; merges to `main` are Sam's save points.
- Commit messages: plain, no AI co-authorship line (repo convention).
- `BalambGarden.Engine` and `BalambGarden.Engine.Tests` must never reference Dalamud, ECommons, or the plugin project.
- 0-based ward/plot values are stored raw everywhere; +1 conversion happens only in display helpers, never in storage.
- Unknown species ids are surfaced as unknown, never guessed or mislabeled.
- Every timer/ETA type is a window (min/max), never a single point.
- Observation ring capacity: 8 (named constant). Wilt thresholds: Due at 75% of wiltHours; Overdue past wiltHours; Danger halfway between wiltHours and witherHours (all named constants).
- Stage-fraction model (tunable constant, calibration pending): stage 1 covers [0, 1/3) of growHours, stage 2 [1/3, 2/3), stage 3 [2/3, 1), stage 4 = ripe at >= growHours.
- Build/test commands: `dotnet build BalambGarden.sln -c Debug -p:Platform=x64` and `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj`.
- All engine code uses `DateTimeOffset` (UTC) for timestamps.

---

### Task 1: Branch + solution scaffold

**Files:**
- Create: `BalambGarden.Engine/BalambGarden.Engine.csproj`
- Create: `BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj`
- Create: `BalambGarden.Engine.Tests/SmokeTest.cs`
- Modify: `BalambGarden.sln` (via `dotnet sln add`)
- Modify: `BalambGarden/BalambGarden.csproj` (add ProjectReference)

**Interfaces:**
- Consumes: nothing.
- Produces: the two projects and namespaces `BalambGarden.Engine` / `BalambGarden.Engine.Tests` all later tasks live in.

- [ ] **Step 1: Create the branch**

```bash
cd /d/Dev/Dalamud/BalambGarden
git checkout main && git pull && git checkout -b rebuild
```

- [ ] **Step 2: Create the Engine class library**

`BalambGarden.Engine/BalambGarden.Engine.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>BalambGarden.Engine</RootNamespace>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Create the test project**

`BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\BalambGarden.Engine\BalambGarden.Engine.csproj" />
  </ItemGroup>
</Project>
```

`BalambGarden.Engine.Tests/SmokeTest.cs`:

```csharp
using Xunit;

namespace BalambGarden.Engine.Tests;

public class SmokeTest
{
    [Fact]
    public void TestFrameworkRuns() => Assert.Equal(2, 1 + 1);
}
```

- [ ] **Step 4: Wire solution + plugin reference**

```bash
dotnet sln BalambGarden.sln add BalambGarden.Engine/BalambGarden.Engine.csproj BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj
```

In `BalambGarden/BalambGarden.csproj`, inside the existing `<ItemGroup>` with the ECommons reference, add:

```xml
    <ProjectReference Include="..\BalambGarden.Engine\BalambGarden.Engine.csproj" />
```

- [ ] **Step 5: Verify everything builds and the smoke test passes**

Run: `dotnet build BalambGarden.sln -c Debug -p:Platform=x64` -> Expected: Build succeeded, 0 errors.
Run: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj` -> Expected: 1 passed.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "Scaffold rebuild: Engine classlib + test project, wired into sln and plugin"
```

---

### Task 2: Domain tables (crops, crossbreeds, species index)

**Files:**
- Create: `BalambGarden.Engine/Domain/Crop.cs`
- Create: `BalambGarden.Engine/Domain/DomainTables.cs`
- Modify: `BalambGarden.Engine/BalambGarden.Engine.csproj` (embed JSON)
- Test: `BalambGarden.Engine.Tests/Domain/DomainTablesTests.cs`

**Interfaces:**
- Consumes: `Data/Crops.json` (array of 81 crop objects: `name, growHours, wiltHours, witherHours, itemId, seedId, seedName, crossOnly, crossable, gatherable, sources`), `Data/CrossbreedPairs.json` (object: result seedId string -> array of `[parentSeedA, parentSeedB]` int pairs), `Data/SpeciesIndex.json` (object: gardening-index string -> `{seedId, itemId, name|null}`).
- Produces:
  - `record Crop(string Name, int GrowHours, int WiltHours, int WitherHours, uint ItemId, uint SeedId, string SeedName, bool Crossable)`
  - `class DomainTables` with `static DomainTables Load()` (embedded resources), `Crop? CropBySeedId(uint seedId)`, `ushort? SpeciesIndexBySeedId(uint seedId)`, `string SpeciesName(ushort index)` (falls back to `"Unknown (0xNN)"`), `uint? SeedIdBySpeciesIndex(ushort index)`, `Crop? CropBySpeciesIndex(ushort index)`, `IReadOnlyList<(uint ParentA, uint ParentB)> PairsForResult(uint resultSeedId)`, `uint? CrossResult(uint parentA, uint parentB)` (order-insensitive).

- [ ] **Step 1: Embed the JSON tables in the Engine project**

Add to `BalambGarden.Engine/BalambGarden.Engine.csproj`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="..\Data\Crops.json" LogicalName="Data.Crops.json" />
    <EmbeddedResource Include="..\Data\CrossbreedPairs.json" LogicalName="Data.CrossbreedPairs.json" />
    <EmbeddedResource Include="..\Data\SpeciesIndex.json" LogicalName="Data.SpeciesIndex.json" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

`BalambGarden.Engine.Tests/Domain/DomainTablesTests.cs`:

```csharp
using BalambGarden.Engine.Domain;
using Xunit;

namespace BalambGarden.Engine.Tests.Domain;

public class DomainTablesTests
{
    private static readonly DomainTables T = DomainTables.Load();

    [Fact] // receipts: SpeciesTable.g.cs verified in-game 08-12
    public void KnownSpeciesNamesDecode()
    {
        Assert.Equal("Mirror Apple", T.SpeciesName(0x11));
        Assert.Equal("Old World Fig", T.SpeciesName(0x41));
        Assert.Equal("Krakka Root", T.SpeciesName(0x31));
        Assert.Equal("Royal Kukuru Bean", T.SpeciesName(0x24));
        Assert.Equal("Curiel Root", T.SpeciesName(0x2C));
    }

    [Fact] // 08-13: id 108 exists in-game but is newer than the index snapshot
    public void UnknownSpeciesFallsBackHonestly()
        => Assert.Equal("Unknown (0x6C)", T.SpeciesName(0x6C));

    [Fact]
    public void KrakkaCropTimersLoad()
    {
        var krakka = T.CropBySpeciesIndex(0x31);
        Assert.NotNull(krakka);
        Assert.Equal(72, krakka!.GrowHours);   // 3-day crop
        Assert.Equal(24, krakka.WiltHours);    // fastest wilt tier
        Assert.True(krakka.WitherHours > krakka.WiltHours);
    }

    [Fact] // the Onion pipeline's finisher recipe, verified 54 pairs on 08-12
    public void KukuruCrossCurielMakesThavnairianOnion()
    {
        var kukuru = T.CropBySpeciesIndex(0x24)!;
        var curiel = T.CropBySpeciesIndex(0x2C)!;
        var result = T.CrossResult(kukuru.SeedId, curiel.SeedId);
        Assert.NotNull(result);
        var onion = T.CropBySeedId(result!.Value);
        Assert.NotNull(onion);
        Assert.Contains("Thavnairian Onion", onion!.Name);
    }

    [Fact]
    public void CrossResultIsOrderInsensitive()
    {
        var kukuru = T.CropBySpeciesIndex(0x24)!;
        var curiel = T.CropBySpeciesIndex(0x2C)!;
        Assert.Equal(T.CrossResult(kukuru.SeedId, curiel.SeedId),
                     T.CrossResult(curiel.SeedId, kukuru.SeedId));
    }

    [Fact]
    public void SpeciesIndexRoundTripsThroughSeedId()
    {
        var seedId = T.SeedIdBySpeciesIndex(0x24);
        Assert.NotNull(seedId);
        Assert.Equal((ushort)0x24, T.SpeciesIndexBySeedId(seedId!.Value));
    }
}
```

If the Krakka `GrowHours`/onion-name assertions fail against the real table values, fix the ASSERTION to the table's actual value (the table is receipt-verified domain data; the test documents it) - but only after reading the JSON to confirm.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj` -> Expected: FAIL (namespace `BalambGarden.Engine.Domain` does not exist).

- [ ] **Step 4: Implement**

`BalambGarden.Engine/Domain/Crop.cs`:

```csharp
namespace BalambGarden.Engine.Domain;

public sealed record Crop(
    string Name,
    int GrowHours,
    int WiltHours,
    int WitherHours,
    uint ItemId,
    uint SeedId,
    string SeedName,
    bool Crossable);
```

`BalambGarden.Engine/Domain/DomainTables.cs`:

```csharp
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
```

Note: `SpeciesIndex.json` currently has `name: null` for indices 100-107. Task 3 fixes the data; this loader already tolerates it.

- [ ] **Step 5: Run tests**

Run: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj` -> Expected: all pass (except possibly the two data-value assertions - resolve per Step 2's note by reading `Data/Crops.json`).

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "Engine: domain tables loader (crops, crossbreed pairs, species index) with honest unknown fallback"
```

---

### Task 3: Species index tail names (data fix)

**Files:**
- Modify: `tools/build-domain-data.mjs`
- Modify: `Data/SpeciesIndex.json` (regenerated)
- Test: extend `BalambGarden.Engine.Tests/Domain/DomainTablesTests.cs`

**Interfaces:**
- Consumes: `DomainTables` from Task 2.
- Produces: names for indices 100-107 in the shipped JSON (xivapi-verified 2026-08-13): 100 = `Red Morning Glories`, 102 = `Red Lupins`, 103 = `Garden Sunflower`, 107 = `Red Tea Flowers`.

- [ ] **Step 1: Write the failing test**

Add to `DomainTablesTests`:

```csharp
    [Fact] // xivapi-verified 2026-08-13; 103 receipt-bound in-game (sunflower pot, key=129)
    public void IndoorTailSpeciesAreNamed()
    {
        Assert.Equal("Red Morning Glories", T.SpeciesName(100));
        Assert.Equal("Red Lupins", T.SpeciesName(102));
        Assert.Equal("Garden Sunflower", T.SpeciesName(103));
        Assert.Equal("Red Tea Flowers", T.SpeciesName(107));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj --filter IndoorTailSpeciesAreNamed` -> Expected: FAIL (names are null -> "Unknown (0x64)" etc.).

- [ ] **Step 3: Patch the generator and regenerate**

In `tools/build-domain-data.mjs`, find where the species index entries are written (the object keyed by gardening index with `{seedId, itemId, name}`) and add a name-override map applied when the source name is null:

```js
// Indoor/deco tail: names absent from the Lotlab snapshot, verified via xivapi 2026-08-13.
const SPECIES_NAME_OVERRIDES = {
  100: 'Red Morning Glories',
  102: 'Red Lupins',
  103: 'Garden Sunflower',
  107: 'Red Tea Flowers',
};
```

Apply at the point each species entry is emitted: `name: entry.name ?? SPECIES_NAME_OVERRIDES[index] ?? null`.

Run: `node tools/build-domain-data.mjs`
Then confirm the diff touches ONLY the four name fields: `git diff Data/SpeciesIndex.json` (migrations must be diffable - if the generator reorders keys or reformats, fix the generator, do not hand-edit the JSON).

- [ ] **Step 4: Run tests**

Run: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj` -> Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Domain data: name the indoor species tail (100/102/103/107, xivapi-verified)"
```

---

### Task 4: Outdoor DataMap decode

**Files:**
- Create: `BalambGarden.Engine/Sensing/BedReading.cs`
- Create: `BalambGarden.Engine/Sensing/MapFormat.cs`
- Test: `BalambGarden.Engine.Tests/Sensing/OutdoorMapTests.cs`

**Interfaces:**
- Consumes: raw 48-byte outdoor map entries (from the game adapter in Plan B; from hex fixtures in tests).
- Produces:
  - `record BedReading(int Slot, ushort SpeciesIndex, byte Stage, byte Extra, bool Occupied)` - `Extra` = raw byte 3 (indoor pigment suspect; preserved, never interpreted).
  - `static class MapFormat` with `IReadOnlyList<BedReading> DecodeOutdoorEntry(ReadOnlySpan<byte> bytes)` (expects exactly 48 bytes, 8 slots x 6-byte stride: `[species u16 LE][stage][b3][b4][b5]`; slot occupied when species != 0) and `bool LooksEmpty(ReadOnlySpan<byte> bytes)` (all species zero).

- [ ] **Step 1: Write the failing tests** (fixture bytes verbatim from `captures/2026-08-13-chelsea-fc-probe.log`)

`BalambGarden.Engine.Tests/Sensing/OutdoorMapTests.cs`:

```csharp
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Sensing;

public class OutdoorMapTests
{
    private static byte[] Bytes(string hex)
        => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
              .Select(h => Convert.ToByte(h, 16)).ToArray();

    // key=110 (Chelsea 1st Patch, 08-13 19:56 dump): fresh Fig x Mirror replant, stage 1
    private const string Key110 =
        "41 00 01 00 00 10 11 00 01 00 00 51 41 00 01 00 00 00 11 00 01 00 00 00 " +
        "41 00 01 00 00 A7 11 00 01 00 00 69 41 00 01 00 00 00 11 00 01 00 00 00";

    // key=1150 (FC-ward neighbor): 4/8 occupied, alternating empty slots
    private const string Key1150 =
        "1D 00 04 00 00 00 00 00 00 00 00 00 1D 00 04 00 00 00 00 00 00 00 00 00 " +
        "1D 00 04 00 00 02 00 00 00 00 00 00 1D 00 04 00 00 CD 00 00 00 00 00 00";

    // empty entry (key=402): junk columns 02/CD present even with no plants
    private const string KeyEmpty =
        "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 02 00 00 00 00 00 00 00 00 00 00 00 CD 00 00 00 00 00 00";

    [Fact]
    public void ChelseaFirstPatchDecodesFigMirrorAlternation()
    {
        var beds = MapFormat.DecodeOutdoorEntry(Bytes(Key110));
        Assert.Equal(8, beds.Count);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(i, beds[i].Slot);
            Assert.True(beds[i].Occupied);
            Assert.Equal(i % 2 == 0 ? 0x41 : 0x11, beds[i].SpeciesIndex); // Fig / Mirror
            Assert.Equal(1, beds[i].Stage);
        }
    }

    [Fact]
    public void PartialOccupancyDecodes()
    {
        var beds = MapFormat.DecodeOutdoorEntry(Bytes(Key1150));
        Assert.Equal(4, beds.Count(b => b.Occupied));
        Assert.All(beds.Where(b => b.Occupied), b =>
        {
            Assert.Equal(0x1D, b.SpeciesIndex);
            Assert.Equal(4, b.Stage); // ripe
        });
        Assert.All(beds.Where(b => !b.Occupied), b => Assert.Equal(0, b.SpeciesIndex));
    }

    [Fact] // junk columns (02/CD) must not fake occupancy
    public void EmptyEntryReadsEmptyDespiteJunkColumns()
    {
        Assert.True(MapFormat.LooksEmpty(Bytes(KeyEmpty)));
        Assert.All(MapFormat.DecodeOutdoorEntry(Bytes(KeyEmpty)), b => Assert.False(b.Occupied));
    }

    [Fact]
    public void WrongLengthThrows()
        => Assert.Throws<ArgumentException>(() => MapFormat.DecodeOutdoorEntry(new byte[47]));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj --filter OutdoorMapTests` -> Expected: FAIL (MapFormat missing).

- [ ] **Step 3: Implement**

`BalambGarden.Engine/Sensing/BedReading.cs`:

```csharp
namespace BalambGarden.Engine.Sensing;

/// <summary>One decoded map slot. Extra = raw byte 3, preserved un-interpreted
/// (indoor pigment suspect, hypothesis unbound as of 2026-08-13).</summary>
public sealed record BedReading(int Slot, ushort SpeciesIndex, byte Stage, byte Extra, bool Occupied);
```

`BalambGarden.Engine/Sensing/MapFormat.cs`:

```csharp
namespace BalambGarden.Engine.Sensing;

/// <summary>Pure decoders for the gardening DataMap entry formats (receipt-verified 08-12/08-13).
/// Outdoor: 48 bytes = 8 beds x 6-byte stride [species u16 LE][stage][b3][b4][b5].
/// Bytes 4-5 carry allocator junk (02/CD columns) - never read them.</summary>
public static class MapFormat
{
    public const int OutdoorEntrySize = 48;
    public const int Stride = 6;
    public const int BedsPerPatch = 8;

    public static IReadOnlyList<BedReading> DecodeOutdoorEntry(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != OutdoorEntrySize)
            throw new ArgumentException($"Outdoor entry must be {OutdoorEntrySize} bytes, got {bytes.Length}");

        var beds = new List<BedReading>(BedsPerPatch);
        for (var slot = 0; slot < BedsPerPatch; slot++)
        {
            var off = slot * Stride;
            var species = (ushort)(bytes[off] | (bytes[off + 1] << 8));
            beds.Add(new BedReading(slot, species, bytes[off + 2], bytes[off + 3], species != 0));
        }
        return beds;
    }

    public static bool LooksEmpty(ReadOnlySpan<byte> bytes)
        => DecodeOutdoorEntry(bytes).All(b => !b.Occupied);
}
```

- [ ] **Step 4: Run tests** -> Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Engine: outdoor DataMap decoder with capture-fixture tests"
```

---

### Task 5: Indoor DataMap decode

**Files:**
- Create: `BalambGarden.Engine/Sensing/PotReading.cs`
- Modify: `BalambGarden.Engine/Sensing/MapFormat.cs`
- Test: `BalambGarden.Engine.Tests/Sensing/IndoorMapTests.cs`

**Interfaces:**
- Consumes: raw 48-byte indoor entries.
- Produces: `record PotReading(ushort SpeciesIndex, byte Stage, byte Extra, bool Occupied, bool Recognized)` and `MapFormat.DecodeIndoorEntry(ReadOnlySpan<byte> bytes, Func<ushort, bool> knownSpecies)` -> `PotReading?` (null when the entry does not parse as a single-plant pot: multi-slot furniture, unknown layouts). `Recognized=false` when the species id is not in the index (display as unknown, still tracked).

- [ ] **Step 1: Write the failing tests** (fixtures verbatim from the 19:12 indoor dump)

`BalambGarden.Engine.Tests/Sensing/IndoorMapTests.cs`:

```csharp
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Sensing;

public class IndoorMapTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static bool Known(ushort id) => T.SeedIdBySpeciesIndex(id) is not null;

    private static byte[] Bytes(string hex)
        => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
              .Select(h => Convert.ToByte(h, 16)).ToArray();

    // key=129: Garden Sunflower planted ~19:10, dumped 19:12 (receipt bind 08-13)
    private const string Sunflower =
        "67 00 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 7F 00 00 00 00 00 01 00 00 00 00 00 44 00 00 00 00 00 00";

    // key=117: Red Tea Flowers, stage 4, extra byte 01 (pigment suspect)
    private const string TeaFlowers =
        "6B 00 04 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 7F 00 00 00 00 00 01 00 00 00 00 00 44 00 00 00 00 00 00";

    // key=165: five-sub-entry furniture (NOT a pot) - must not decode as one
    private const string MultiSlot =
        "02 00 03 00 00 00 03 00 03 00 00 00 5C 01 01 00 00 00 5C 01 01 00 00 00 " +
        "5C 01 01 00 00 7F 00 00 00 00 00 01 00 00 00 00 00 44 00 00 00 00 00 00";

    [Fact]
    public void SunflowerPotDecodes()
    {
        var pot = MapFormat.DecodeIndoorEntry(Bytes(Sunflower), Known);
        Assert.NotNull(pot);
        Assert.Equal(0x67, pot!.SpeciesIndex);
        Assert.Equal(1, pot.Stage);
        Assert.True(pot.Occupied);
        Assert.True(pot.Recognized);
    }

    [Fact] // extra byte preserved raw - pigment is a HYPOTHESIS, never interpreted here
    public void ExtraBytePreservedNotInterpreted()
    {
        var pot = MapFormat.DecodeIndoorEntry(Bytes(TeaFlowers), Known)!;
        Assert.Equal(0x6B, pot.SpeciesIndex);
        Assert.Equal(4, pot.Stage);
        Assert.Equal(0x01, pot.Extra);
    }

    [Fact] // multi-slot furniture must be rejected, not misread as a pot
    public void MultiSlotFurnitureIsNotAPot()
        => Assert.Null(MapFormat.DecodeIndoorEntry(Bytes(MultiSlot), Known));

    [Fact] // id 0x6C exists in-game but not in the index: tracked, flagged unrecognized
    public void NewerThanIndexSpeciesIsTrackedButUnrecognized()
    {
        var hex = TeaFlowers.Replace("6B 00 04 01", "6C 00 04 08");
        var pot = MapFormat.DecodeIndoorEntry(Bytes(hex), Known)!;
        Assert.Equal(0x6C, pot.SpeciesIndex);
        Assert.True(pot.Occupied);
        Assert.False(pot.Recognized);
    }
}
```

- [ ] **Step 2: Run to verify failure** -> Expected: FAIL (`DecodeIndoorEntry` missing).

- [ ] **Step 3: Implement**

`BalambGarden.Engine/Sensing/PotReading.cs`:

```csharp
namespace BalambGarden.Engine.Sensing;

/// <summary>One decoded indoor pot. Recognized=false -> species newer than our index:
/// track it, display "Unknown (0xNN)", never guess.</summary>
public sealed record PotReading(ushort SpeciesIndex, byte Stage, byte Extra, bool Occupied, bool Recognized);
```

Add to `MapFormat`:

```csharp
    /// <summary>Indoor entries share the 48-byte block but a pot uses only sub-entry 0.
    /// Entries with data in sub-entries 1+ are other furniture (aquariums etc.) - rejected.
    /// Trailing 7F/01/44 columns are the indoor junk pattern - never read.</summary>
    public static PotReading? DecodeIndoorEntry(ReadOnlySpan<byte> bytes, Func<ushort, bool> knownSpecies)
    {
        if (bytes.Length != OutdoorEntrySize)
            throw new ArgumentException($"Indoor entry must be {OutdoorEntrySize} bytes, got {bytes.Length}");

        // Sub-entries 1..3 must be empty for a single-plant pot (offsets 6/12/18; junk lives past 28).
        for (var sub = 1; sub <= 3; sub++)
        {
            var off = sub * Stride;
            if ((ushort)(bytes[off] | (bytes[off + 1] << 8)) != 0)
                return null;
        }

        var species = (ushort)(bytes[0] | (bytes[1] << 8));
        if (species == 0)
            return new PotReading(0, 0, 0, Occupied: false, Recognized: false);

        return new PotReading(species, bytes[2], bytes[3], Occupied: true, Recognized: knownSpecies(species));
    }
```

- [ ] **Step 4: Run tests** -> Expected: all pass. (If `MultiSlot` slips through because sub-entry 4 holds the fifth `5C 01`, extend the emptiness check to sub-entry 4, respecting that indoor junk starts at offset 29 - byte 28 is data space. Verify against the fixture, then adjust the loop bound.)

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Engine: indoor pot decoder - single-slot rule, raw extra byte, unrecognized-species honesty"
```

---

### Task 6: GimmickId decode + diff-pattern join shortlist

**Files:**
- Create: `BalambGarden.Engine/Sensing/GimmickId.cs`
- Create: `BalambGarden.Engine/Census/JoinShortlist.cs`
- Test: `BalambGarden.Engine.Tests/Census/JoinTests.cs`

**Interfaces:**
- Consumes: raw `uint` bed GimmickIds; lists of map keys present in a ward.
- Produces:
  - `record struct BedGimmick(byte BedIndex, byte PatchOrdinal, ushort PatchId)` + `static BedGimmick GimmickId.Decode(uint raw)` (layout `[bed byte3][ordinal byte2][patch-id u16 low]`, verified 08-12).
  - `static class JoinShortlist` with `IReadOnlyList<IReadOnlyList<int>> Candidates(IReadOnlyList<ushort> patchIdsInOrdinalOrder, IReadOnlyList<int> wardKeys)`: every strictly-increasing key sequence whose pairwise diffs equal the patch-ids' pairwise diffs. A shortlist PROPOSES; only a receipt binds (Census layer, Task 8).

- [ ] **Step 1: Write the failing tests**

`BalambGarden.Engine.Tests/Census/JoinTests.cs`:

```csharp
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

public class JoinTests
{
    [Fact] // capture 08-13: 0x05013927 = bed 5, ordinal 1, patch-id 0x3927 (FC estate)
    public void GimmickDecodesFcBed()
    {
        var g = GimmickId.Decode(0x05013927);
        Assert.Equal(5, g.BedIndex);
        Assert.Equal(1, g.PatchOrdinal);
        Assert.Equal(0x3927, g.PatchId);
    }

    [Fact] // capture 08-13: 0x0200200A = bed 2, ordinal 0, patch-id 0x200A (Chelsea)
    public void GimmickDecodesChelseaBed()
    {
        var g = GimmickId.Decode(0x0200200A);
        Assert.Equal(2, g.BedIndex);
        Assert.Equal(0, g.PatchOrdinal);
        Assert.Equal(0x200A, g.PatchId);
    }

    // Ward keys observed 08-13 at the shared Chelsea/FC ward (subset with both estates present)
    private static readonly int[] WardKeys =
        [62, 110, 116, 117, 285, 286, 290, 365, 447, 891, 1067, 1150, 1293, 1313, 1319];

    [Fact] // Chelsea: patch-ids 0x200A/0x2010/0x2011, diffs +6,+1 -> keys 110/116/117
    public void ChelseaDiffPatternShortlists()
    {
        var candidates = JoinShortlist.Candidates([0x200A, 0x2010, 0x2011], WardKeys);
        Assert.Contains(candidates, c => c.SequenceEqual([110, 116, 117]));
    }

    [Fact] // FC: patch-ids 0x390D/0x3921/0x3927, diffs +20,+6 -> keys 1293/1313/1319
    public void FcDiffPatternShortlists()
    {
        var candidates = JoinShortlist.Candidates([0x390D, 0x3921, 0x3927], WardKeys);
        Assert.Contains(candidates, c => c.SequenceEqual([1293, 1313, 1319]));
    }

    [Fact] // a diff pattern nothing matches -> empty shortlist, never a forced guess
    public void NoMatchMeansEmptyShortlist()
        => Assert.Empty(JoinShortlist.Candidates([0x1000, 0x1003, 0x1009], WardKeys));

    [Fact] // single-patch estates (Sam's house) have no diffs: every key is a candidate
    public void SinglePatchShortlistsEveryKey()
        => Assert.Equal(WardKeys.Length, JoinShortlist.Candidates([0x200A], WardKeys).Count);
}
```

- [ ] **Step 2: Run to verify failure** -> Expected: FAIL.

- [ ] **Step 3: Implement**

`BalambGarden.Engine/Sensing/GimmickId.cs`:

```csharp
namespace BalambGarden.Engine.Sensing;

/// <summary>Bed GimmickId layout [bed idx byte3][patch ordinal byte2][patch-id u16],
/// receipt-verified at three estates 08-12/08-13.</summary>
public readonly record struct BedGimmick(byte BedIndex, byte PatchOrdinal, ushort PatchId);

public static class GimmickId
{
    public static BedGimmick Decode(uint raw) => new(
        BedIndex: (byte)(raw >> 24),
        PatchOrdinal: (byte)(raw >> 16),
        PatchId: (ushort)raw);
}
```

`BalambGarden.Engine/Census/JoinShortlist.cs`:

```csharp
namespace BalambGarden.Engine.Census;

/// <summary>Diff-pattern candidate finder: ward keys preserve patch-id pairwise diffs per
/// estate (proven 08-12, low-byte rule dead). A shortlist only PROPOSES - binding
/// requires a receipt. There is deliberately no auto-bind path here.</summary>
public static class JoinShortlist
{
    public static IReadOnlyList<IReadOnlyList<int>> Candidates(
        IReadOnlyList<ushort> patchIdsInOrdinalOrder, IReadOnlyList<int> wardKeys)
    {
        var results = new List<IReadOnlyList<int>>();
        if (patchIdsInOrdinalOrder.Count == 0)
            return results;

        var diffs = new int[patchIdsInOrdinalOrder.Count - 1];
        for (var i = 1; i < patchIdsInOrdinalOrder.Count; i++)
            diffs[i - 1] = patchIdsInOrdinalOrder[i] - patchIdsInOrdinalOrder[i - 1];

        var keys = wardKeys.Distinct().Order().ToArray();
        var keySet = keys.ToHashSet();
        foreach (var start in keys)
        {
            var candidate = new List<int> { start };
            var current = start;
            var ok = true;
            foreach (var d in diffs)
            {
                current += d;
                if (!keySet.Contains(current)) { ok = false; break; }
                candidate.Add(current);
            }
            if (ok)
                results.Add(candidate);
        }
        return results;
    }
}
```

- [ ] **Step 4: Run tests** -> Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Engine: GimmickId decode + diff-pattern join shortlist (propose-only, receipts bind)"
```

---

### Task 7: Estate identity + ledger records + persistence

**Files:**
- Create: `BalambGarden.Engine/Census/EstateKey.cs`
- Create: `BalambGarden.Engine/Ledger/Observation.cs`
- Create: `BalambGarden.Engine/Ledger/ClaimedBed.cs`
- Create: `BalambGarden.Engine/Ledger/LedgerStore.cs`
- Test: `BalambGarden.Engine.Tests/Ledger/LedgerTests.cs`

**Interfaces:**
- Consumes: nothing game-side.
- Produces:
  - `record EstateKey(ushort TerritoryId, int Ward, int Plot, int Room = -1)` - all raw 0-based; `string DisplayWardPlot()` -> `"Ward {Ward+1} Plot {Plot+1}"`; usable as dictionary key; JSON-serializable.
  - `enum Provenance { Anchored, Bracketed, Estimated }`
  - `enum ObservationSource { MapSighting, TendReceipt, PlantReceipt, HarvestReceipt, StatusTalk, RipeSkip }`
  - `record Observation(DateTimeOffset At, ushort SpeciesIndex, byte Stage, ObservationSource Source)`
  - `class ClaimedBed` with `EstateKey Estate`, `int MapKey`, `int PatchOrdinal`, `int BedSlot`, `bool IsPot`, `DateTimeOffset ClaimedAt`, `DateTimeOffset? LastTended`, `IReadOnlyList<Observation> Ring`, `void Observe(Observation o)` (ring: newest kept, capacity `RingCapacity = 8`), `Observation? Latest`.
  - `class LedgerStore` with `List<ClaimedBed> Beds`, `Dictionary<string, int> Bindings` (serialized estate+ordinal -> map key), `string ToJson()`, `static LedgerStore FromJson(string json)`.

- [ ] **Step 1: Write the failing tests**

`BalambGarden.Engine.Tests/Ledger/LedgerTests.cs`:

```csharp
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Ledger;

public class LedgerTests
{
    private static readonly EstateKey Chelsea = new(TerritoryId: 340, Ward: 11, Plot: 32);
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-13T19:10:00Z");

    private static ClaimedBed NewBed() => new()
    {
        Estate = Chelsea, MapKey = 110, PatchOrdinal = 0, BedSlot = 3,
        IsPot = false, ClaimedAt = T0,
    };

    [Fact] // 0-based raw storage, +1 display only (Frame 2 / probe-proven gotcha)
    public void DisplayConvertsZeroBasedToHuman()
        => Assert.Equal("Ward 12 Plot 33", Chelsea.DisplayWardPlot());

    [Fact]
    public void RingKeepsNewestEight()
    {
        var bed = NewBed();
        for (var i = 0; i < 12; i++)
            bed.Observe(new Observation(T0.AddHours(i), 0x41, 1, ObservationSource.MapSighting));
        Assert.Equal(8, bed.Ring.Count);
        Assert.Equal(T0.AddHours(11), bed.Latest!.At);   // newest kept
        Assert.Equal(T0.AddHours(4), bed.Ring.Min(o => o.At)); // oldest four dropped
    }

    [Fact]
    public void LedgerRoundTripsThroughJson()
    {
        var store = new LedgerStore();
        var bed = NewBed();
        bed.Observe(new Observation(T0, 0x41, 1, ObservationSource.PlantReceipt));
        bed.LastTended = T0;
        store.Beds.Add(bed);
        store.Bindings[$"{Chelsea.TerritoryId}:{Chelsea.Ward}:{Chelsea.Plot}:-1#0"] = 110;

        var restored = LedgerStore.FromJson(store.ToJson());
        var rb = Assert.Single(restored.Beds);
        Assert.Equal(Chelsea, rb.Estate);
        Assert.Equal(110, rb.MapKey);
        Assert.Equal(3, rb.BedSlot);
        Assert.Equal(T0, rb.LastTended);
        var obs = Assert.Single(rb.Ring);
        Assert.Equal(ObservationSource.PlantReceipt, obs.Source);
        Assert.Equal(110, restored.Bindings[$"{Chelsea.TerritoryId}:{Chelsea.Ward}:{Chelsea.Plot}:-1#0"]);
    }
}
```

- [ ] **Step 2: Run to verify failure** -> Expected: FAIL.

- [ ] **Step 3: Implement**

`BalambGarden.Engine/Census/EstateKey.cs`:

```csharp
namespace BalambGarden.Engine.Census;

/// <summary>Estate identity. Ward/Plot/Room stored RAW 0-based (HousingManager values);
/// +1 happens ONLY in display helpers. Room = -1 for houses; apartments use Plot as
/// building and Room as the apartment number.</summary>
public sealed record EstateKey(ushort TerritoryId, int Ward, int Plot, int Room = -1)
{
    public string DisplayWardPlot() => $"Ward {Ward + 1} Plot {Plot + 1}";
    public string BindingKey(int patchOrdinal) => $"{TerritoryId}:{Ward}:{Plot}:{Room}#{patchOrdinal}";
}
```

`BalambGarden.Engine/Ledger/Observation.cs`:

```csharp
namespace BalambGarden.Engine.Ledger;

public enum Provenance { Anchored, Bracketed, Estimated }

public enum ObservationSource { MapSighting, TendReceipt, PlantReceipt, HarvestReceipt, StatusTalk, RipeSkip }

public sealed record Observation(DateTimeOffset At, ushort SpeciesIndex, byte Stage, ObservationSource Source);
```

`BalambGarden.Engine/Ledger/ClaimedBed.cs`:

```csharp
using BalambGarden.Engine.Census;

namespace BalambGarden.Engine.Ledger;

/// <summary>One claimed bed or pot: current state + the observation ring that feeds
/// brackets, wilt clocks, and provenance (spec: approach C).</summary>
public sealed class ClaimedBed
{
    public const int RingCapacity = 8;

    public required EstateKey Estate { get; init; }
    public required int MapKey { get; init; }
    public required int PatchOrdinal { get; init; }
    public required int BedSlot { get; init; }
    public bool IsPot { get; init; }
    public DateTimeOffset ClaimedAt { get; init; }
    public DateTimeOffset? LastTended { get; set; }

    public List<Observation> RingStorage { get; init; } = [];   // public for serialization

    public IReadOnlyList<Observation> Ring => RingStorage;
    public Observation? Latest => RingStorage.Count == 0 ? null : RingStorage[^1];

    public void Observe(Observation o)
    {
        RingStorage.Add(o);
        RingStorage.Sort((a, b) => a.At.CompareTo(b.At));
        if (RingStorage.Count > RingCapacity)
            RingStorage.RemoveRange(0, RingStorage.Count - RingCapacity);
    }
}
```

`BalambGarden.Engine/Ledger/LedgerStore.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BalambGarden.Engine.Ledger;

/// <summary>The persisted census: claimed beds + receipt-bound patch->key bindings.
/// Fresh file in v0.2; the POC ledger is never read.</summary>
public sealed class LedgerStore
{
    public int Version { get; set; } = 2;
    public List<ClaimedBed> Beds { get; set; } = [];
    public Dictionary<string, int> Bindings { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static LedgerStore FromJson(string json)
        => JsonSerializer.Deserialize<LedgerStore>(json, Options)
           ?? throw new InvalidOperationException("Ledger JSON deserialized to null");
}
```

Note: `ClaimedBed` uses `required`/`init` members - if `System.Text.Json` cannot round-trip them directly, add `[JsonConstructor]` or relax to `set;` accessors; the test is the arbiter.

- [ ] **Step 4: Run tests** -> Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Engine: estate identity, observation ring, ledger store with JSON round-trip"
```

---

### Task 8: Census engine - receipts bind, claim-on-action

**Files:**
- Create: `BalambGarden.Engine/Census/ReceiptEvent.cs`
- Create: `BalambGarden.Engine/Census/CensusEngine.cs`
- Test: `BalambGarden.Engine.Tests/Census/CensusEngineTests.cs`

**Interfaces:**
- Consumes: `JoinShortlist`, `LedgerStore`, `ClaimedBed`, `Observation`, `EstateKey` from earlier tasks.
- Produces:
  - `enum ReceiptVerb { Tend, Harvest, Plant, PotWater }`
  - `record ReceiptEvent(EstateKey Estate, int PatchOrdinal, int BedSlot, ReceiptVerb Verb, ushort SpeciesIndex, byte Stage, DateTimeOffset At, bool IsPot = false)`
  - `class CensusEngine(LedgerStore ledger)` with:
    - `bool ClaimOnAction { get; set; } = true`
    - `void Bind(EstateKey estate, int patchOrdinal, int mapKey)` - records a receipt-proven binding; re-binding an ordinal overwrites (mismatch = re-bind, never silent trust).
    - `int? BoundKey(EstateKey estate, int patchOrdinal)`
    - `ClaimedBed? OnReceipt(ReceiptEvent e)` - requires an existing binding for the patch (or `IsPot`, where MapKey comes via `BindPot`); creates/updates the claimed record ONLY when `ClaimOnAction` is true or the bed is already claimed; always records the observation on claimed beds; returns the record or null (unclaimed + checkbox off, or unbound patch).
    - `void Abandon(ClaimedBed bed)` - removes from ledger.
  - There is deliberately NO `Claim(bed)` method: claim-on-action is the only path in.

- [ ] **Step 1: Write the failing tests**

`BalambGarden.Engine.Tests/Census/CensusEngineTests.cs`:

```csharp
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

public class CensusEngineTests
{
    private static readonly EstateKey Chelsea = new(340, 11, 32);
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-13T19:00:00Z");

    private static ReceiptEvent Tend(int slot, byte stage = 1) =>
        new(Chelsea, PatchOrdinal: 0, BedSlot: slot, ReceiptVerb.Tend,
            SpeciesIndex: 0x41, Stage: stage, At: T0);

    [Fact] // claim-on-action: a completed tend on a bound patch claims the bed
    public void TendReceiptClaimsAndObserves()
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Chelsea, 0, mapKey: 110);

        var bed = engine.OnReceipt(Tend(slot: 3));

        Assert.NotNull(bed);
        Assert.Equal(110, bed!.MapKey);
        Assert.Equal(3, bed.BedSlot);
        Assert.Equal(T0, bed.LastTended);
        var obs = Assert.Single(bed.Ring);
        Assert.Equal(ObservationSource.TendReceipt, obs.Source);
    }

    [Fact] // checkbox off: no new claims, receipt goes nowhere
    public void ClaimOnActionOffDoesNotClaim()
    {
        var engine = new CensusEngine(new LedgerStore()) { ClaimOnAction = false };
        engine.Bind(Chelsea, 0, 110);
        Assert.Null(engine.OnReceipt(Tend(3)));
        Assert.Empty(engine.LedgerBeds);
    }

    [Fact] // checkbox off but bed ALREADY claimed: observation still lands
    public void AlreadyClaimedBedStillObservesWithCheckboxOff()
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Chelsea, 0, 110);
        engine.OnReceipt(Tend(3));

        engine.ClaimOnAction = false;
        var bed = engine.OnReceipt(Tend(3, stage: 2));
        Assert.NotNull(bed);
        Assert.Equal(2, bed!.Ring.Count);
    }

    [Fact] // no binding = no claim: a receipt can't attach to a patch we can't identify
    public void UnboundPatchReceiptDoesNotClaim()
    {
        var engine = new CensusEngine(new LedgerStore());
        Assert.Null(engine.OnReceipt(Tend(3)));
        Assert.Empty(engine.LedgerBeds);
    }

    [Fact] // re-binding overwrites: mismatch triggers re-bind, never silent trust
    public void RebindOverwrites()
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Chelsea, 0, 110);
        engine.Bind(Chelsea, 0, 116);
        Assert.Equal(116, engine.BoundKey(Chelsea, 0));
    }

    [Fact]
    public void AbandonRemoves()
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Chelsea, 0, 110);
        var bed = engine.OnReceipt(Tend(3))!;
        engine.Abandon(bed);
        Assert.Empty(engine.LedgerBeds);
    }

    [Fact] // same bed, second receipt: one record, two observations - never duplicates
    public void SecondReceiptOnSameBedDoesNotDuplicate()
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Chelsea, 0, 110);
        engine.OnReceipt(Tend(3));
        engine.OnReceipt(Tend(3, stage: 2));
        Assert.Single(engine.LedgerBeds);
    }
}
```

- [ ] **Step 2: Run to verify failure** -> Expected: FAIL.

- [ ] **Step 3: Implement**

`BalambGarden.Engine/Census/ReceiptEvent.cs`:

```csharp
namespace BalambGarden.Engine.Census;

public enum ReceiptVerb { Tend, Harvest, Plant, PotWater }

/// <summary>A completed interaction, parsed from dialogue by the game adapter.
/// Receipts are the ONLY thing that binds and the only thing that claims.</summary>
public sealed record ReceiptEvent(
    EstateKey Estate,
    int PatchOrdinal,
    int BedSlot,
    ReceiptVerb Verb,
    ushort SpeciesIndex,
    byte Stage,
    DateTimeOffset At,
    bool IsPot = false);
```

`BalambGarden.Engine/Census/CensusEngine.cs`:

```csharp
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
```

- [ ] **Step 4: Run tests** -> Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Engine: census engine - receipts bind, claim-on-action only, abandon, no Claim() path"
```

---

### Task 9: Map sightings flow into claimed beds

**Files:**
- Modify: `BalambGarden.Engine/Census/CensusEngine.cs`
- Test: extend `BalambGarden.Engine.Tests/Census/CensusEngineTests.cs`

**Interfaces:**
- Consumes: `MapFormat` decoders (Task 4/5).
- Produces: `CensusEngine.OnMapSighting(EstateKey estate, int mapKey, IReadOnlyList<BedReading> beds, DateTimeOffset at)` - records `MapSighting` observations on CLAIMED beds whose `MapKey` matches; unclaimed slots are ignored (ward data stays ephemeral). Returns count of observations recorded.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact] // sightings feed claimed beds; unclaimed ward data stays ephemeral
    public void MapSightingObservesClaimedOnly()
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Chelsea, 0, 110);
        engine.OnReceipt(Tend(3));   // claims slot 3 only

        var readings = Enumerable.Range(0, 8)
            .Select(i => new BalambGarden.Engine.Sensing.BedReading(
                i, (ushort)(i % 2 == 0 ? 0x41 : 0x11), 2, 0, true))
            .ToList();

        var count = engine.OnMapSighting(Chelsea, mapKey: 110, readings, T0.AddDays(1));

        Assert.Equal(1, count);   // only the claimed bed
        var bed = Assert.Single(engine.LedgerBeds);
        Assert.Equal(2, bed.Ring.Count);
        Assert.Equal(ObservationSource.MapSighting, bed.Latest!.Source);
        Assert.Equal(2, bed.Latest.Stage);
    }

    [Fact] // sighting for a different key does not touch this bed
    public void MapSightingWrongKeyIgnored()
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Chelsea, 0, 110);
        engine.OnReceipt(Tend(3));
        var readings = new List<BalambGarden.Engine.Sensing.BedReading>
            { new(3, 0x41, 3, 0, true) };
        Assert.Equal(0, engine.OnMapSighting(Chelsea, mapKey: 116, readings, T0.AddDays(1)));
    }
```

- [ ] **Step 2: Run to verify failure** -> Expected: FAIL (`OnMapSighting` missing).

- [ ] **Step 3: Implement** - add to `CensusEngine`:

```csharp
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
```

- [ ] **Step 4: Run tests** -> Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Engine: map sightings observe claimed beds only"
```

---

### Task 10: Stage brackets + ETA windows

**Files:**
- Create: `BalambGarden.Engine/Derivations/EtaWindow.cs`
- Create: `BalambGarden.Engine/Derivations/StageModel.cs`
- Test: `BalambGarden.Engine.Tests/Derivations/StageModelTests.cs`

**Interfaces:**
- Consumes: `ClaimedBed.Ring`, `DomainTables` (growHours).
- Produces:
  - `record EtaWindow(DateTimeOffset Earliest, DateTimeOffset Latest, Provenance Provenance)`
  - `static class StageModel` with:
    - `(double Lo, double Hi) StageFraction(byte stage)` -> stage 1: (0, 1/3), 2: (1/3, 2/3), 3: (2/3, 1.0), 4: (1.0, 1.0). Tunable constants.
    - `EtaWindow? RipeWindow(IReadOnlyList<Observation> ring, int growHours)`:
      - If the ring contains a `PlantReceipt` -> anchored: ripe = plantTime + growHours (Earliest == Latest, `Provenance.Anchored`).
      - Else intersect plant-time constraints from every staged observation (obs at time t with stage s constrains plant time to `[t - Hi(s)*G, t - Lo(s)*G]`); ripe window = plant window + growHours. Two-plus observations -> `Bracketed`; one -> `Estimated`. Stage-4 observations mean already ripe -> window `(obs.At, obs.At)` with the ring's best provenance.
      - Empty/none-staged ring -> null.

- [ ] **Step 1: Write the failing tests**

`BalambGarden.Engine.Tests/Derivations/StageModelTests.cs`:

```csharp
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

public class StageModelTests
{
    private const int Grow = 120; // 5-day crop
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-10T18:00:00Z");

    private static Observation Obs(double hoursAfterT0, byte stage,
        ObservationSource src = ObservationSource.MapSighting)
        => new(T0.AddHours(hoursAfterT0), 0x24, stage, src);

    [Fact] // plant receipt = anchored: exact ripe time, zero-width window
    public void PlantReceiptAnchors()
    {
        var w = StageModel.RipeWindow([Obs(0, 1, ObservationSource.PlantReceipt)], Grow)!;
        Assert.Equal(Provenance.Anchored, w.Provenance);
        Assert.Equal(T0.AddHours(Grow), w.Earliest);
        Assert.Equal(w.Earliest, w.Latest);
    }

    [Fact] // one sighting: estimated, window spans the whole stage band
    public void SingleSightingEstimates()
    {
        var w = StageModel.RipeWindow([Obs(50, 2)], Grow)!;
        Assert.Equal(Provenance.Estimated, w.Provenance);
        // stage 2 at t=50h: plant in [50 - 2/3*120, 50 - 1/3*120] = [-30, +10] hrs
        Assert.Equal(T0.AddHours(-30 + Grow), w.Earliest);
        Assert.Equal(T0.AddHours(10 + Grow), w.Latest);
    }

    [Fact] // two disagreeing sightings bracket the flip and tighten the window
    public void TwoSightingsBracket()
    {
        var w = StageModel.RipeWindow([Obs(30, 1), Obs(50, 2)], Grow)!;
        Assert.Equal(Provenance.Bracketed, w.Provenance);
        // stage1@30: plant in [-10, 30]; stage2@50: plant in [-30, 10] -> intersect [-10, 10]
        Assert.Equal(T0.AddHours(-10 + Grow), w.Earliest);
        Assert.Equal(T0.AddHours(10 + Grow), w.Latest);
        Assert.True(w.Latest - w.Earliest < TimeSpan.FromHours(41)); // tighter than either alone
    }

    [Fact] // ripe observed = already ripe now, not a forecast
    public void StageFourIsRipeNow()
    {
        var w = StageModel.RipeWindow([Obs(0, 1), Obs(100, 4)], Grow)!;
        Assert.True(w.Earliest <= T0.AddHours(100));
        Assert.Equal(w.Earliest, w.Latest);
    }

    [Fact]
    public void EmptyRingGivesNull()
        => Assert.Null(StageModel.RipeWindow([], Grow));

    [Fact] // contradictory sightings (impossible intersection) -> null, never a lie
    public void ContradictionGivesNull()
        => Assert.Null(StageModel.RipeWindow([Obs(0, 3), Obs(100, 1)], Grow));
}
```

- [ ] **Step 2: Run to verify failure** -> Expected: FAIL.

- [ ] **Step 3: Implement**

`BalambGarden.Engine/Derivations/EtaWindow.cs`:

```csharp
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>Every timer is a window, never a point. Provenance says what kind of
/// claim the window is making (spec: timing model).</summary>
public sealed record EtaWindow(DateTimeOffset Earliest, DateTimeOffset Latest, Provenance Provenance);
```

`BalambGarden.Engine/Derivations/StageModel.cs`:

```csharp
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>Stage-fraction timing model. Stage bands are equal thirds of growHours -
/// a tunable model constant, calibration pending (spec: brackets).</summary>
public static class StageModel
{
    public static (double Lo, double Hi) StageFraction(byte stage) => stage switch
    {
        1 => (0.0, 1.0 / 3.0),
        2 => (1.0 / 3.0, 2.0 / 3.0),
        3 => (2.0 / 3.0, 1.0),
        4 => (1.0, 1.0),
        _ => (0.0, 1.0),
    };

    public static EtaWindow? RipeWindow(IReadOnlyList<Observation> ring, int growHours)
    {
        if (ring.Count == 0)
            return null;

        var anchor = ring.FirstOrDefault(o => o.Source == ObservationSource.PlantReceipt);
        if (anchor is not null)
        {
            var ripe = anchor.At.AddHours(growHours);
            return new EtaWindow(ripe, ripe, Provenance.Anchored);
        }

        var staged = ring.Where(o => o.Stage is >= 1 and <= 4).OrderBy(o => o.At).ToList();
        if (staged.Count == 0)
            return null;

        var provenance = staged.Count >= 2 ? Provenance.Bracketed : Provenance.Estimated;

        var ripeSeen = staged.FirstOrDefault(o => o.Stage == 4);
        if (ripeSeen is not null)
        {
            // Ripe was observed: it is ripe now; earliest possible ripe bounded by prior sightings.
            return new EtaWindow(ripeSeen.At, ripeSeen.At, provenance);
        }

        // Each observation (t, stage s) constrains plant time to [t - Hi(s)*G, t - Lo(s)*G].
        var earliestPlant = DateTimeOffset.MinValue;
        var latestPlant = DateTimeOffset.MaxValue;
        foreach (var o in staged)
        {
            var (lo, hi) = StageFraction(o.Stage);
            var min = o.At.AddHours(-hi * growHours);
            var max = o.At.AddHours(-lo * growHours);
            if (min > earliestPlant) earliestPlant = min;
            if (max < latestPlant) latestPlant = max;
        }

        if (earliestPlant > latestPlant)
            return null;   // contradictory sightings: report nothing rather than a lie

        return new EtaWindow(
            earliestPlant.AddHours(growHours),
            latestPlant.AddHours(growHours),
            provenance);
    }
}
```

- [ ] **Step 4: Run tests** -> Expected: all pass. (Check `StageFourIsRipeNow` semantics against the implementation: with a stage-4 sighting the window is `(ripeSeen.At, ripeSeen.At)` and provenance Bracketed - the test asserts exactly that.)

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Engine: stage-fraction brackets and ETA windows with provenance"
```

---

### Task 11: ClockWiltSource

**Files:**
- Create: `BalambGarden.Engine/Derivations/WiltSource.cs`
- Test: `BalambGarden.Engine.Tests/Derivations/WiltTests.cs`

**Interfaces:**
- Consumes: `ClaimedBed.LastTended`, `Crop.WiltHours/WitherHours`.
- Produces:
  - `enum WaterState { Unknown, Watered, Due, Overdue, Danger }`
  - `interface IWiltSource { WaterState StateFor(ClaimedBed bed, Crop crop, DateTimeOffset now); }`
  - `class ClockWiltSource : IWiltSource` - from `LastTended`: elapsed < 75% of WiltHours -> Watered; < WiltHours -> Due; < WiltHours + (WitherHours - WiltHours)/2 -> Overdue; else Danger. `LastTended == null` -> Unknown. Constants `DueFraction = 0.75` named.
  - A future memory sensor implements the same interface (spec: the wilt seam).

- [ ] **Step 1: Write the failing tests**

`BalambGarden.Engine.Tests/Derivations/WiltTests.cs`:

```csharp
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

public class WiltTests
{
    private static readonly Crop Krakka = new("Krakka Root", GrowHours: 72, WiltHours: 24,
        WitherHours: 48, ItemId: 4842, SeedId: 7745, SeedName: "Krakka Root Seeds", Crossable: true);
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-13T18:00:00Z");

    private static ClaimedBed Bed(DateTimeOffset? tended) => new()
    {
        Estate = new EstateKey(340, 11, 32), MapKey = 110, PatchOrdinal = 0, BedSlot = 0,
        LastTended = tended,
    };

    private static readonly ClockWiltSource Source = new();

    [Theory]
    [InlineData(0, WaterState.Watered)]
    [InlineData(17, WaterState.Watered)]  // < 18h (75% of 24)
    [InlineData(19, WaterState.Due)]      // 75% crossed
    [InlineData(25, WaterState.Overdue)]  // past 24h wilt window
    [InlineData(37, WaterState.Danger)]   // past 24 + (48-24)/2 = 36h
    public void KrakkaClockStates(int hoursSinceTend, WaterState expected)
        => Assert.Equal(expected, Source.StateFor(Bed(T0), Krakka, T0.AddHours(hoursSinceTend)));

    [Fact] // never tended under watch: honest Unknown, not a guess
    public void NoTendReceiptMeansUnknown()
        => Assert.Equal(WaterState.Unknown, Source.StateFor(Bed(null), Krakka, T0));
}
```

- [ ] **Step 2: Run to verify failure** -> Expected: FAIL.

- [ ] **Step 3: Implement**

`BalambGarden.Engine/Derivations/WiltSource.cs`:

```csharp
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

public enum WaterState { Unknown, Watered, Due, Overdue, Danger }

/// <summary>The wilt seam (spec): v1 derives from the tend clock; a future memory
/// sensor is just another implementation writing better-provenance data.</summary>
public interface IWiltSource
{
    WaterState StateFor(ClaimedBed bed, Crop crop, DateTimeOffset now);
}

public sealed class ClockWiltSource : IWiltSource
{
    public const double DueFraction = 0.75;

    public WaterState StateFor(ClaimedBed bed, Crop crop, DateTimeOffset now)
    {
        if (bed.LastTended is not { } tended)
            return WaterState.Unknown;

        var hours = (now - tended).TotalHours;
        var dangerAt = crop.WiltHours + (crop.WitherHours - crop.WiltHours) / 2.0;

        return hours switch
        {
            _ when hours < crop.WiltHours * DueFraction => WaterState.Watered,
            _ when hours < crop.WiltHours => WaterState.Due,
            _ when hours < dangerAt => WaterState.Overdue,
            _ => WaterState.Danger,
        };
    }
}
```

- [ ] **Step 4: Run tests** -> Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Engine: ClockWiltSource behind the IWiltSource seam"
```

---

### Task 12: Capacity rollups + arrival nudge

**Files:**
- Create: `BalambGarden.Engine/Derivations/Rollup.cs`
- Test: `BalambGarden.Engine.Tests/Derivations/RollupTests.cs`

**Interfaces:**
- Consumes: `LedgerStore.Beds`, `IWiltSource`, `StageModel`, `DomainTables`.
- Produces:
  - `record PatchRollup(EstateKey Estate, int PatchOrdinal, bool IsPots, int Claimed, int Ripe, int Due, int Overdue, int Danger, int Unknown, EtaWindow? NextRipe)`
  - `static class Rollups` with `IReadOnlyList<PatchRollup> ForEstate(EstateKey estate, IReadOnlyList<ClaimedBed> beds, DomainTables tables, IWiltSource wilt, DateTimeOffset now)` (grouped by PatchOrdinal + IsPot; Ripe = latest observation stage 4; NextRipe = earliest `RipeWindow` among non-ripe beds).
  - `static string? ArrivalNudge(EstateKey estate, IReadOnlyList<PatchRollup> rollups)` -> one line like `"Balamb: 3 beds thirsty here, 1 ripe"` when anything is Due-or-worse or Ripe; null otherwise (silence over filler).

- [ ] **Step 1: Write the failing tests**

`BalambGarden.Engine.Tests/Derivations/RollupTests.cs`:

```csharp
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

public class RollupTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly EstateKey Chelsea = new(340, 11, 32);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T18:00:00Z");

    private static ClaimedBed Bed(int slot, byte stage, double tendedHoursAgo)
    {
        var bed = new ClaimedBed
        {
            Estate = Chelsea, MapKey = 110, PatchOrdinal = 0, BedSlot = slot,
            LastTended = Now.AddHours(-tendedHoursAgo),
        };
        // Krakka Root (0x31): 24h wilt tier - drives the wilt states below
        bed.Observe(new Observation(Now.AddHours(-tendedHoursAgo), 0x31, stage,
            ObservationSource.TendReceipt));
        return bed;
    }

    [Fact]
    public void RollupCountsStates()
    {
        var beds = new List<ClaimedBed>
        {
            Bed(0, stage: 4, tendedHoursAgo: 1),    // ripe, watered
            Bed(1, stage: 2, tendedHoursAgo: 1),    // watered
            Bed(2, stage: 2, tendedHoursAgo: 20),   // due (>= 18h)
            Bed(3, stage: 2, tendedHoursAgo: 30),   // overdue (>= 24h)
        };

        var rollup = Assert.Single(Rollups.ForEstate(Chelsea, beds, T, new ClockWiltSource(), Now));
        Assert.Equal(4, rollup.Claimed);
        Assert.Equal(1, rollup.Ripe);
        Assert.Equal(1, rollup.Due);
        Assert.Equal(1, rollup.Overdue);
        Assert.NotNull(rollup.NextRipe);   // the stage-2 beds project a window
    }

    [Fact]
    public void NudgeSpeaksWhenAttentionNeeded()
    {
        var rollups = Rollups.ForEstate(Chelsea,
            [Bed(0, 4, 1), Bed(2, 2, 20)], T, new ClockWiltSource(), Now);
        var line = Rollups.ArrivalNudge(Chelsea, rollups);
        Assert.NotNull(line);
        Assert.Contains("1 ripe", line);
        Assert.Contains("1", line);   // one thirsty
    }

    [Fact] // all watered, nothing ripe: silence over filler
    public void NudgeSilentWhenAllQuiet()
    {
        var rollups = Rollups.ForEstate(Chelsea, [Bed(1, 2, 1)], T, new ClockWiltSource(), Now);
        Assert.Null(Rollups.ArrivalNudge(Chelsea, rollups));
    }
}
```

- [ ] **Step 2: Run to verify failure** -> Expected: FAIL.

- [ ] **Step 3: Implement**

`BalambGarden.Engine/Derivations/Rollup.cs`:

```csharp
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

public sealed record PatchRollup(
    EstateKey Estate, int PatchOrdinal, bool IsPots,
    int Claimed, int Ripe, int Due, int Overdue, int Danger, int Unknown,
    EtaWindow? NextRipe);

public static class Rollups
{
    public static IReadOnlyList<PatchRollup> ForEstate(
        EstateKey estate, IReadOnlyList<ClaimedBed> beds, DomainTables tables,
        IWiltSource wilt, DateTimeOffset now)
    {
        return beds
            .Where(b => b.Estate == estate)
            .GroupBy(b => (b.PatchOrdinal, b.IsPot))
            .Select(g =>
            {
                int ripe = 0, due = 0, overdue = 0, danger = 0, unknown = 0;
                EtaWindow? nextRipe = null;
                foreach (var bed in g)
                {
                    var latest = bed.Latest;
                    var isRipe = latest?.Stage == 4;
                    if (isRipe) ripe++;

                    var crop = latest is null ? null : tables.CropBySpeciesIndex(latest.SpeciesIndex);
                    switch (crop is null ? WaterState.Unknown : wilt.StateFor(bed, crop, now))
                    {
                        case WaterState.Due: due++; break;
                        case WaterState.Overdue: overdue++; break;
                        case WaterState.Danger: danger++; break;
                        case WaterState.Unknown: unknown++; break;
                    }

                    if (!isRipe && crop is not null
                        && StageModel.RipeWindow(bed.Ring, crop.GrowHours) is { } window
                        && (nextRipe is null || window.Earliest < nextRipe.Earliest))
                        nextRipe = window;
                }
                return new PatchRollup(estate, g.Key.PatchOrdinal, g.Key.IsPot,
                    g.Count(), ripe, due, overdue, danger, unknown, nextRipe);
            })
            .OrderBy(r => r.IsPots).ThenBy(r => r.PatchOrdinal)
            .ToList();
    }

    /// <summary>The one line the plugin ever says unprompted. Null = stay silent.</summary>
    public static string? ArrivalNudge(EstateKey estate, IReadOnlyList<PatchRollup> rollups)
    {
        var thirsty = rollups.Sum(r => r.Due + r.Overdue + r.Danger);
        var ripe = rollups.Sum(r => r.Ripe);
        if (thirsty == 0 && ripe == 0)
            return null;

        var parts = new List<string>();
        if (thirsty > 0) parts.Add($"{thirsty} bed{(thirsty == 1 ? "" : "s")} thirsty here");
        if (ripe > 0) parts.Add($"{ripe} ripe");
        return $"Balamb: {string.Join(", ", parts)}";
    }
}
```

- [ ] **Step 4: Run tests** -> Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Engine: capacity rollups and the arrival nudge (silent when quiet)"
```

---

### Task 13: Pipeline reader (tips)

**Files:**
- Create: `BalambGarden.Engine/Derivations/PipelineReader.cs`
- Test: `BalambGarden.Engine.Tests/Derivations/PipelineTests.cs`

**Interfaces:**
- Consumes: `LedgerStore.Beds`, `DomainTables` (cross lookups), `StageModel`.
- Produces:
  - `enum TipKind { Stock, Bottleneck, Anomaly }`
  - `record Tip(TipKind Kind, string Text)`
  - `record CrossIntent(EstateKey Estate, int PatchOrdinal, ushort SpeciesA, ushort SpeciesB, uint ResultSeedId)`
  - `static class PipelineReader` with:
    - `IReadOnlyList<CrossIntent> RecognizeIntents(IReadOnlyList<ClaimedBed> beds, DomainTables tables)` - per (estate, patch): if the claimed beds' latest species form exactly two alternating species A/B whose pair crosses to something, that patch has cross intent. Patches with 1 species or non-crossing pairs produce nothing.
    - `IReadOnlyList<Tip> Tips(IReadOnlyList<ClaimedBed> beds, DomainTables tables, DateTimeOffset now)` - Stock lines for each recognized intent ("Patch making X seeds"); Bottleneck when one intent's RESULT is a parent seed in another intent (a chain) and the feeder patch's ripe window is the latest among feeders; Anomaly when a patch is one-bed-off a recognized A/B alternation.

- [ ] **Step 1: Write the failing tests** (the household Onion pipeline = the acceptance case)

`BalambGarden.Engine.Tests/Derivations/PipelineTests.cs`:

```csharp
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

public class PipelineTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly EstateKey ChelseaHouse = new(340, 11, 32);
    private static readonly EstateKey FcHouse = new(340, 11, 57);
    private static readonly EstateKey SamHouse = new(641, 3, 10);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T18:00:00Z");

    private static List<ClaimedBed> Patch(EstateKey estate, int ordinal, int mapKey,
        ushort speciesEven, ushort speciesOdd, byte stage)
    {
        var beds = new List<ClaimedBed>();
        for (var slot = 0; slot < 8; slot++)
        {
            var bed = new ClaimedBed
                { Estate = estate, MapKey = mapKey, PatchOrdinal = ordinal, BedSlot = slot };
            bed.Observe(new Observation(Now.AddHours(-2),
                slot % 2 == 0 ? speciesEven : speciesOdd, stage, ObservationSource.TendReceipt));
            beds.Add(bed);
        }
        return beds;
    }

    // The receipt-verified household (08-12): Fig x Mirror -> Kukuru seeds;
    // Krakka x Mirror -> Curiel seeds; Kukuru x Curiel -> Thavnairian Onion.
    private static List<ClaimedBed> Household()
    {
        var beds = new List<ClaimedBed>();
        beds.AddRange(Patch(ChelseaHouse, 0, 110, 0x41, 0x11, stage: 1)); // Fig x Mirror
        beds.AddRange(Patch(FcHouse, 0, 1293, 0x31, 0x11, stage: 1));     // Krakka x Mirror
        beds.AddRange(Patch(SamHouse, 0, 1038, 0x24, 0x2C, stage: 3));    // Kukuru x Curiel
        return beds;
    }

    [Fact]
    public void RecognizesAllThreeIntents()
    {
        var intents = PipelineReader.RecognizeIntents(Household(), T);
        Assert.Equal(3, intents.Count);
        var onionIntent = intents.Single(i => i.Estate == SamHouse);
        Assert.Contains("Thavnairian Onion", T.CropBySeedId(onionIntent.ResultSeedId)!.Name);
    }

    [Fact] // Stock lines name what each patch is making
    public void StockTipsNameTheProducts()
    {
        var tips = PipelineReader.Tips(Household(), T, Now);
        var stock = tips.Where(t => t.Kind == TipKind.Stock).ToList();
        Assert.Equal(3, stock.Count);
        Assert.Contains(stock, t => t.Text.Contains("Thavnairian Onion"));
    }

    [Fact] // chained intents (result feeds a parent) surface as pipeline awareness
    public void ChainedIntentsProduceBottleneckLine()
    {
        var tips = PipelineReader.Tips(Household(), T, Now);
        Assert.Contains(tips, t => t.Kind == TipKind.Bottleneck);
    }

    [Fact] // one bed off-pattern: anomaly, phrased as a question, never a correction
    public void BrokenAlternationIsAnAnomaly()
    {
        var beds = Patch(ChelseaHouse, 0, 110, 0x41, 0x11, 1);
        beds[5].Observe(new Observation(Now.AddHours(-1), 0x31, 1, ObservationSource.TendReceipt));

        var tips = PipelineReader.Tips(beds, T, Now);
        Assert.Contains(tips, t => t.Kind == TipKind.Anomaly);
    }

    [Fact] // a single-species patch is not a cross - no intent, no tips noise
    public void MonocultureProducesNoIntent()
    {
        var beds = Patch(ChelseaHouse, 0, 110, 0x31, 0x31, 2);
        Assert.Empty(PipelineReader.RecognizeIntents(beds, T));
    }
}
```

- [ ] **Step 2: Run to verify failure** -> Expected: FAIL.

- [ ] **Step 3: Implement**

`BalambGarden.Engine/Derivations/PipelineReader.cs`:

```csharp
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

public enum TipKind { Stock, Bottleneck, Anomaly }

public sealed record Tip(TipKind Kind, string Text);

public sealed record CrossIntent(
    EstateKey Estate, int PatchOrdinal, ushort SpeciesA, ushort SpeciesB, uint ResultSeedId);

/// <summary>Reads the pattern the gardener already chose and reports its state.
/// Never a planner, never prescriptive (spec: tips). Anomalies ask, corrections never.</summary>
public static class PipelineReader
{
    public static IReadOnlyList<CrossIntent> RecognizeIntents(
        IReadOnlyList<ClaimedBed> beds, DomainTables tables)
    {
        var intents = new List<CrossIntent>();
        foreach (var patch in beds.Where(b => !b.IsPot).GroupBy(b => (b.Estate, b.PatchOrdinal)))
        {
            var latest = patch
                .Where(b => b.Latest is not null)
                .ToDictionary(b => b.BedSlot, b => b.Latest!.SpeciesIndex);
            if (latest.Count < 4)
                continue;

            var evens = latest.Where(kv => kv.Key % 2 == 0).Select(kv => kv.Value).Distinct().ToList();
            var odds = latest.Where(kv => kv.Key % 2 == 1).Select(kv => kv.Value).Distinct().ToList();
            if (evens.Count != 1 || odds.Count != 1 || evens[0] == odds[0])
                continue;

            var seedA = tables.SeedIdBySpeciesIndex(evens[0]);
            var seedB = tables.SeedIdBySpeciesIndex(odds[0]);
            if (seedA is null || seedB is null)
                continue;
            if (tables.CrossResult(seedA.Value, seedB.Value) is not { } result)
                continue;

            intents.Add(new CrossIntent(patch.Key.Estate, patch.Key.PatchOrdinal,
                evens[0], odds[0], result));
        }
        return intents;
    }

    public static IReadOnlyList<Tip> Tips(
        IReadOnlyList<ClaimedBed> beds, DomainTables tables, DateTimeOffset now)
    {
        var tips = new List<Tip>();
        var intents = RecognizeIntents(beds, tables);

        foreach (var intent in intents)
        {
            var product = tables.CropBySeedId(intent.ResultSeedId)?.Name
                          ?? $"seed {intent.ResultSeedId}";
            tips.Add(new Tip(TipKind.Stock,
                $"{intent.Estate.DisplayWardPlot()} patch {intent.PatchOrdinal + 1}: " +
                $"{tables.SpeciesName(intent.SpeciesA)} x {tables.SpeciesName(intent.SpeciesB)} " +
                $"-> {product}"));
        }

        // Chain: one intent's result seed is a parent in another intent -> pipeline.
        foreach (var feeder in intents)
        {
            var feederResultIndex = tables.SpeciesIndexBySeedId(feeder.ResultSeedId);
            foreach (var consumer in intents)
            {
                if (consumer == feeder) continue;
                var consumerParents = new[]
                {
                    tables.SeedIdBySpeciesIndex(consumer.SpeciesA),
                    tables.SeedIdBySpeciesIndex(consumer.SpeciesB),
                };
                if (!consumerParents.Contains(feeder.ResultSeedId))
                    continue;
                var product = tables.CropBySeedId(consumer.ResultSeedId)?.Name ?? "?";
                var feederName = tables.CropBySeedId(feeder.ResultSeedId)?.Name ?? "?";
                tips.Add(new Tip(TipKind.Bottleneck,
                    $"{feederName} seeds feed the {product} patch " +
                    $"({consumer.Estate.DisplayWardPlot()}) - feeder is " +
                    $"{feeder.Estate.DisplayWardPlot()} patch {feeder.PatchOrdinal + 1}"));
            }
        }

        // Anomaly: a patch that is one bed away from a clean A/B alternation.
        foreach (var patch in beds.Where(b => !b.IsPot).GroupBy(b => (b.Estate, b.PatchOrdinal)))
        {
            var latest = patch.Where(b => b.Latest is not null)
                .ToDictionary(b => b.BedSlot, b => b.Latest!.SpeciesIndex);
            if (latest.Count < 5)
                continue;
            var evenGroups = latest.Where(kv => kv.Key % 2 == 0)
                .GroupBy(kv => kv.Value).OrderByDescending(g => g.Count()).ToList();
            var oddGroups = latest.Where(kv => kv.Key % 2 == 1)
                .GroupBy(kv => kv.Value).OrderByDescending(g => g.Count()).ToList();
            var misfits = evenGroups.Skip(1).Sum(g => g.Count()) + oddGroups.Skip(1).Sum(g => g.Count());
            if (misfits != 1)
                continue;
            var offSlot = latest.First(kv =>
                (kv.Key % 2 == 0 && kv.Value != evenGroups[0].Key) ||
                (kv.Key % 2 == 1 && kv.Value != oddGroups[0].Key)).Key;
            tips.Add(new Tip(TipKind.Anomaly,
                $"{patch.Key.Estate.DisplayWardPlot()} patch {patch.Key.PatchOrdinal + 1} " +
                $"bed {offSlot + 1} breaks the alternation - intentional?"));
        }

        return tips;
    }
}
```

- [ ] **Step 4: Run tests** -> Expected: all pass. The Onion-pipeline test uses THE receipt-verified household layout; if `RecognizesAllThreeIntents` fails, the bug is in recognition or the cross tables - do not weaken the test.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Engine: pipeline reader - intents, stock/bottleneck/anomaly tips; Onion pipeline acceptance test"
```

---

### Task 14: Debug trail writer + full-suite checkpoint

**Files:**
- Create: `BalambGarden.Engine/Ledger/DebugTrail.cs`
- Test: `BalambGarden.Engine.Tests/Ledger/DebugTrailTests.cs`

**Interfaces:**
- Consumes: `ReceiptEvent`.
- Produces: `class DebugTrail(string path)` with `void Append(ReceiptEvent e)` (one JSON line per receipt, append-only, create-if-missing) and `static IReadOnlyList<string> ReadLines(string path)`. The engine NEVER reads the trail for state - it exists for ground-truthing only.

- [ ] **Step 1: Write the failing test**

`BalambGarden.Engine.Tests/Ledger/DebugTrailTests.cs`:

```csharp
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Ledger;

public class DebugTrailTests
{
    [Fact]
    public void AppendsOneJsonLinePerReceipt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"balamb-trail-{Guid.NewGuid():N}.jsonl");
        try
        {
            var trail = new DebugTrail(path);
            var e = new ReceiptEvent(new EstateKey(340, 11, 32), 0, 3, ReceiptVerb.Tend,
                0x41, 1, DateTimeOffset.Parse("2026-08-13T19:00:00Z"));
            trail.Append(e);
            trail.Append(e with { BedSlot = 4 });

            var lines = DebugTrail.ReadLines(path);
            Assert.Equal(2, lines.Count);
            Assert.Contains("\"BedSlot\":3", lines[0]);
            Assert.Contains("Tend", lines[0]);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify failure** -> Expected: FAIL.

- [ ] **Step 3: Implement**

`BalambGarden.Engine/Ledger/DebugTrail.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using BalambGarden.Engine.Census;

namespace BalambGarden.Engine.Ledger;

/// <summary>Append-only receipt log for ground-truthing. The engine never reads
/// this for state (spec: approach C - the trail is evidence, not memory).</summary>
public sealed class DebugTrail(string path)
{
    private static readonly JsonSerializerOptions Options = new()
        { Converters = { new JsonStringEnumConverter() } };

    public void Append(ReceiptEvent e)
        => File.AppendAllText(path, JsonSerializer.Serialize(e, Options) + Environment.NewLine);

    public static IReadOnlyList<string> ReadLines(string path)
        => File.Exists(path) ? File.ReadAllLines(path) : [];
}
```

- [ ] **Step 4: Run the FULL suite** (checkpoint)

Run: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj`
Expected: every test from Tasks 1-14 passes.
Run: `dotnet build BalambGarden.sln -c Debug -p:Platform=x64`
Expected: whole solution (plugin included) builds clean.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Engine: debug trail writer; full engine suite green"
```

---

### Task 15: Plugin smoke-wire + engine handoff checkpoint

**Files:**
- Modify: `BalambGarden/Plugin.cs`
- Modify: `BalambGarden/Windows/MainWindow.cs` (one debug line)

**Interfaces:**
- Consumes: `DomainTables.Load()`.
- Produces: proof the plugin loads the Engine in-game; the anchor point Plan B's adapters build on.

- [ ] **Step 1: Load the tables at plugin start**

In `Plugin.cs`, add a static property and initialize it in the constructor right after `Configuration` is loaded:

```csharp
    public static BalambGarden.Engine.Domain.DomainTables Tables { get; private set; } = null!;
```

```csharp
        Tables = BalambGarden.Engine.Domain.DomainTables.Load();
        Log.Information($"[Engine] domain tables loaded: sunflower check = {Tables.SpeciesName(103)}");
```

- [ ] **Step 2: Surface one engine fact in the POC UI**

In `MainWindow.cs`, find the draw method and add near the top (temporary, Plan B replaces this window wholesale):

```csharp
        ImGui.TextDisabled($"Engine v2 loaded - {Plugin.Tables.SpeciesName(0x24)} says hi");
```

(Match the file's existing `ImGui` using/namespace style.)

- [ ] **Step 3: Build**

Run: `dotnet build BalambGarden.sln -c Debug -p:Platform=x64` -> Expected: clean.

- [ ] **Step 4: Bench check (Sam, in game)**

Load the dev plugin; `/garden`. Expected: the disabled-text line reads "Engine v2 loaded - Royal Kukuru Bean says hi" and dalamud.log shows `[Engine] domain tables loaded: sunflower check = Garden Sunflower`. This is the only in-game step in Plan A; if Sam is not available, note it as pending and do not block the commit.

- [ ] **Step 5: Commit + hand off**

```bash
git add -A && git commit -m "Plugin: load Engine domain tables at startup (Plan A complete)"
git push -u origin rebuild
```

Plan A is complete. Plan B (game adapters, chains, UI) gets written from the Engine's now-real interfaces.

---

## Self-Review (completed at write time)

- **Spec coverage**: Engine-side spec sections all have tasks (domain tables T2-3, sensors' pure decode T4-6, census/claim T8-9, ledger T7, brackets T10, wilt T11, rollups+nudge T12, tips T13, trail T14). NOT in this plan by design: game adapters, chains, UI, `#if DEBUG` probe carryover - all Plan B, per the two-plan split.
- **Placeholder scan**: clean; every step carries real code or an exact command.
- **Type consistency**: `EstateKey`/`Observation`/`ClaimedBed`/`ReceiptEvent` signatures match across Tasks 7-14; `BedReading`/`PotReading` match Tasks 4-5 usage in Task 9; `Provenance` lives in `Ledger` namespace and is consumed by `Derivations` (EtaWindow) - imports shown where needed.
- **Known judgment calls the executor may hit**: JSON round-trip of `required`/`init` members (Task 7 note), stage-4 window semantics (Task 10 note), indoor sub-entry emptiness bound (Task 5 note). Each note says how to resolve against the fixture/test, not by weakening it.
