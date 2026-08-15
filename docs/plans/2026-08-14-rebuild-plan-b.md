# Balamb Garden Rebuild - Plan B: Adapters, Chains, UI

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire the bench-verified Engine (Plan A, merged @ f586a33) into the running game: game-side sensors, the census pump (acting IS censusing), the chain framework with Tend All / Harvest->Replant cycle / pot verbs, and the two windows - shipping as v0.2.0.0.

**Architecture:** The plugin project grows three areas that each depend only on the Engine plus Dalamud: `Game/` (stateless sensor adapters reading HousingManager/DataMap/objects, from the receipt-verified ReconProbe code on the `probe` branch), `Chains/` (a ChainBase extracted from the POC TendChain, plus Tend/Cycle/Pot chains that emit receipts back through the census pump), and `Windows/` (Dashboard + Run Log rebuilt on Engine rollups). New pure logic (receipt parsing, join confirmation, pot binding, estate roster, soils) goes in the Engine with unit tests; only genuinely game-bound code lives in the plugin.

**Tech Stack:** C# / .NET 9, Dalamud.NET.Sdk 15, ECommons (LegacyTaskManager, AddonMaster, Callback), FFXIVClientStructs (HousingManager, InventoryManager), xUnit for Engine tests.

**Spec:** `C:\Obsidian\Book Of Holding\Planet Express\Deliveries\Balamb Garden\Balamb Garden - Rebuild Spec.md` (approved 2026-08-13). Plan A (context + conventions): `docs/plans/2026-08-13-rebuild-engine.md`.

## Global Constraints

- Work on the `rebuild` branch. Never commit to `main`; merges to `main` are Sam's save points (three planned: end of Stage 1, 2, 3).
- Commit messages: plain, no AI co-authorship line (repo convention).
- `BalambGarden.Engine` / `.Engine.Tests` never reference Dalamud, ECommons, or the plugin project.
- Receipts-only is structural: no code path may bind a key or claim a bed from a pattern alone. Shortlists propose; only a receipt confirms.
- 0-based ward/plot/room stored raw; +1 only in display helpers.
- Unknown species surface as unknown ("Unknown (0xNN)"), never guessed.
- Every ETA is a window with provenance; UI renders provenance as a glyph, never color alone.
- Fail closed, report honest: sensors log + skip + surface "N unreadable"; chains refuse pre-flight or stop clean at a bed boundary with a stated reason. Status surfaces mark at confirmation, never at fire.
- Chains pace at human tempo (existing knobs: TendPaceMS 750 +/- 400, PostTendDelayMS 8000 +/- 1000); TaskManager TimeLimitMS derived above the longest tunable step, never racing it.
- **Bench discipline: never test chains on Chelsea's garden.** Bench checkpoints run at Sam's own house/pots. Chelsea's beds are production.
- Build: `dotnet build BalambGarden.sln -c Debug -p:Platform=x64`. Test: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj`. A stale DLL means the x64 flag was dropped.
- All Engine timestamps `DateTimeOffset` (UTC). Plugin-side `DateTime.UtcNow` converts at the boundary via `DateTimeOffset.UtcNow`.
- The POC ledger (`Configuration.Ledger`) is never read or migrated. The v2 ledger is a fresh file `ledger-v2.json` in the plugin config directory.

## File Structure

```
BalambGarden.Engine/
  Census/ReceiptParser.cs        NEW  pure parse of bed headers + ordinal words
  Census/JoinConfirm.cs          NEW  shortlist + receipt -> binding (or null)
  Census/PotBind.cs              NEW  species-uniqueness pot binding
  Ledger/EstateRecord.cs         NEW  roster entry (nickname, first/last visit)
  Ledger/LedgerStore.cs          MOD  + Estates list, + UpsertEstate
  Domain/DomainTables.cs         MOD  + SpeciesIndexByName, + Soils
  Domain/Soil.cs                 NEW  soil record
BalambGarden.Engine.Tests/
  Census/ReceiptParserTests.cs   NEW
  Census/JoinConfirmTests.cs     NEW
  Census/PotBindTests.cs         NEW
  Ledger/EstateRosterTests.cs    NEW
  Domain/SoilsTests.cs           NEW
Data/Soils.json                  NEW  generated from xivapi
tools/build-soils.mjs            NEW  generator
BalambGarden/
  GardenService.cs               NEW  ledger load/save, census, trail (the spine)
  Game/EstateSensor.cs           NEW  HousingManager -> EstateKey
  Game/MapSensor.cs              NEW  DataMap -> BedReading / PotReading
  Game/ObjectSensor.cs           NEW  beds by GimmickId, patches by PatchId, pots
  Game/CensusPump.cs             NEW  sightings, visits, receipt routing, nudge
  Chains/ChainBase.cs            NEW  pacing/telemetry/guards extracted from POC
  Chains/TendChain.cs            NEW  rewrite on ChainBase (old file deleted)
  Chains/PlantFlow.cs            NEW  plant-dialog driver (recon-gated constants)
  Chains/CycleChain.cs           NEW  harvest->replant interleave + pre-flight
  Chains/PotChain.cs             NEW  pot water/plant/harvest
  ReconProbe.cs                  NEW  ported from probe branch, #if DEBUG
  Windows/MainWindow.cs          MOD  full Dashboard rewrite
  Windows/RunLogWindow.cs        MOD  clean-stop report line
  Windows/ConfigWindow.cs        MOD  new toggles
  Configuration.cs               MOD  v1: ClaimOnAction, NudgeEnabled, TrailEnabled
  Plugin.cs                      MOD  service wiring, framework tick
  TendChain.cs                   DEL  (replaced by Chains/TendChain.cs)
  GardenScanner.cs               DEL  (replaced by Game/ObjectSensor.cs)
```

---

## Stage 1 - The read side + the tend loop (save point A)

After Task 7 the plugin senses estates, joins receipts to map keys, claims on tend, persists the v2 ledger, and shows a minimal live dashboard. Sam merges to `main`.

### Task 1: Engine - ReceiptParser + SpeciesIndexByName

**Files:**
- Create: `BalambGarden.Engine/Census/ReceiptParser.cs`
- Modify: `BalambGarden.Engine/Domain/DomainTables.cs`
- Test: `BalambGarden.Engine.Tests/Census/ReceiptParserTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ReceiptParser.ParseBedHeader(string) -> (int BedSlot, int PatchOrdinal)?` (0-based both; "2nd Bed, 1st Patch" -> (1, 0)); `DomainTables.SpeciesIndexByName(string) -> ushort?` (case-insensitive, trimmed).

- [ ] **Step 1: Write the failing tests**

```csharp
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

public class ReceiptParserTests
{
    // Header shape mapped live 2026-08-11: SelectString AtkValues[2] = "2nd Bed, 1st Patch".
    [Theory]
    [InlineData("1st Bed, 1st Patch", 0, 0)]
    [InlineData("2nd Bed, 1st Patch", 1, 0)]
    [InlineData("3rd Bed, 2nd Patch", 2, 1)]
    [InlineData("8th Bed, 3rd Patch", 7, 2)]
    public void ParsesBedHeaders(string header, int slot, int ordinal)
    {
        var parsed = ReceiptParser.ParseBedHeader(header);
        Assert.NotNull(parsed);
        Assert.Equal(slot, parsed!.Value.BedSlot);
        Assert.Equal(ordinal, parsed.Value.PatchOrdinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(unknown bed)")]
    [InlineData("Oasis Flowerpot")]
    public void RejectsNonBedHeaders(string header)
        => Assert.Null(ReceiptParser.ParseBedHeader(header));

    [Fact] // reverse lookup feeds receipt -> species joins
    public void SpeciesIndexByNameRoundTrips()
    {
        var tables = DomainTables.Load();
        var index = tables.SpeciesIndexByName("Royal Kukuru");
        Assert.NotNull(index);
        Assert.Equal("Royal Kukuru", tables.SpeciesName(index!.Value));
        Assert.Equal(index, tables.SpeciesIndexByName("  royal kukuru "));
        Assert.Null(tables.SpeciesIndexByName("Definitely Not A Plant"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj --filter ReceiptParserTests`
Expected: FAIL - `ReceiptParser` does not exist.

- [ ] **Step 3: Implement**

`BalambGarden.Engine/Census/ReceiptParser.cs`:

```csharp
using System.Text.RegularExpressions;

namespace BalambGarden.Engine.Census;

/// <summary>Pure parsing of the game's receipt strings. "Nth Bed" is 1-based in
/// dialogue (verified 08-12); slots and ordinals are 0-based everywhere in code.</summary>
public static partial class ReceiptParser
{
    [GeneratedRegex(@"^(\d+)(?:st|nd|rd|th) Bed, (\d+)(?:st|nd|rd|th) Patch$", RegexOptions.IgnoreCase)]
    private static partial Regex BedHeader();

    public static (int BedSlot, int PatchOrdinal)? ParseBedHeader(string header)
    {
        var m = BedHeader().Match(header.Trim());
        if (!m.Success)
            return null;
        var bed = int.Parse(m.Groups[1].Value);
        var patch = int.Parse(m.Groups[2].Value);
        if (bed < 1 || patch < 1)
            return null;
        return (bed - 1, patch - 1);
    }
}
```

In `DomainTables`, add a reverse-name map. In the private constructor after `indexBySeedId` is built, add:

```csharp
        indexByName = nameByIndex.ToDictionary(
            kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
```

with the field `private readonly Dictionary<string, ushort> indexByName;` and the public method:

```csharp
    /// <summary>Receipt joins: dialogue names a plant, the map speaks species indices.</summary>
    public ushort? SpeciesIndexByName(string name)
        => indexByName.TryGetValue(name.Trim(), out var i) ? i : null;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj`
Expected: all pass (57 existing + new).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Engine: receipt header parser + species reverse lookup"
```

### Task 2: Engine - JoinConfirm + PotBind

**Files:**
- Create: `BalambGarden.Engine/Census/JoinConfirm.cs`
- Create: `BalambGarden.Engine/Census/PotBind.cs`
- Test: `BalambGarden.Engine.Tests/Census/JoinConfirmTests.cs`
- Test: `BalambGarden.Engine.Tests/Census/PotBindTests.cs`

**Interfaces:**
- Consumes: `JoinShortlist.Candidates` output shape (`IReadOnlyList<IReadOnlyList<int>>`, one key per patch ordinal), `BedReading`, `PotReading`.
- Produces:
  - `JoinConfirm.Confirm(candidates, patchOrdinal, bedSlot, speciesIndex, mapByKey) -> IReadOnlyList<int>?` - the single candidate whose key at `patchOrdinal` shows `speciesIndex` at `bedSlot`; null unless exactly one survives.
  - `PotBind.UniqueSpeciesKey(speciesIndex, indoorMap) -> int?` - the single indoor key showing that species; null if zero or several.

- [ ] **Step 1: Write the failing tests**

`JoinConfirmTests.cs`:

```csharp
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

public class JoinConfirmTests
{
    private static IReadOnlyList<BedReading> Patch(params ushort[] species)
        => species.Select((s, i) => new BedReading(i, s, 1, 0, s != 0)).ToList();

    // Two candidates propose different keys for ordinal 0; the map shows Fig (0x41)
    // at slot 3 only under key 110 -> the receipt confirms candidate [110,116,117].
    [Fact]
    public void ReceiptSpeciesMatchPicksTheOneCandidate()
    {
        var candidates = new[] { new[] { 110, 116, 117 }, new[] { 285, 291, 292 } };
        var map = new Dictionary<int, IReadOnlyList<BedReading>>
        {
            [110] = Patch(0, 0, 0, 0x41, 0, 0, 0, 0),
            [285] = Patch(0, 0, 0, 0x11, 0, 0, 0, 0),
        };
        var confirmed = JoinConfirm.Confirm(
            candidates, patchOrdinal: 0, bedSlot: 3, speciesIndex: 0x41,
            key => map.GetValueOrDefault(key));
        Assert.NotNull(confirmed);
        Assert.Equal([110, 116, 117], confirmed);
    }

    [Fact] // both candidates show the same species at that slot -> ambiguous -> null
    public void AmbiguousMatchBindsNothing()
    {
        var candidates = new[] { new[] { 110 }, new[] { 285 } };
        var map = new Dictionary<int, IReadOnlyList<BedReading>>
        {
            [110] = Patch(0x41), [285] = Patch(0x41),
        };
        Assert.Null(JoinConfirm.Confirm(candidates, 0, 0, 0x41, k => map.GetValueOrDefault(k)));
    }

    [Fact] // species mismatch everywhere -> null, never a forced guess
    public void NoMatchBindsNothing()
    {
        var candidates = new[] { new[] { 110 } };
        var map = new Dictionary<int, IReadOnlyList<BedReading>> { [110] = Patch(0x11) };
        Assert.Null(JoinConfirm.Confirm(candidates, 0, 0, 0x41, k => map.GetValueOrDefault(k)));
    }

    [Fact] // a candidate whose key is missing from the map simply doesn't survive
    public void MissingMapEntryEliminatesCandidate()
    {
        var candidates = new[] { new[] { 110 }, new[] { 999 } };
        var map = new Dictionary<int, IReadOnlyList<BedReading>> { [110] = Patch(0x41) };
        Assert.Equal([110], JoinConfirm.Confirm(candidates, 0, 0, 0x41, k => map.GetValueOrDefault(k)));
    }
}
```

`PotBindTests.cs`:

```csharp
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

public class PotBindTests
{
    // The 08-13 sunflower bind: species 0x67 at exactly one key (129) minutes after planting.
    [Fact]
    public void UniqueSpeciesBinds()
    {
        var map = new Dictionary<int, PotReading>
        {
            [126] = new(0x64, 1, 0, true, true),
            [129] = new(0x67, 1, 0, true, true),
        };
        Assert.Equal(129, PotBind.UniqueSpeciesKey(0x67, map));
    }

    [Fact] // two pots, same species -> ambiguous -> null
    public void DuplicateSpeciesBindsNothing()
    {
        var map = new Dictionary<int, PotReading>
        {
            [129] = new(0x67, 1, 0, true, true),
            [130] = new(0x67, 2, 0, true, true),
        };
        Assert.Null(PotBind.UniqueSpeciesKey(0x67, map));
    }

    [Fact]
    public void AbsentSpeciesBindsNothing()
        => Assert.Null(PotBind.UniqueSpeciesKey(0x67, new Dictionary<int, PotReading>()));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj --filter "JoinConfirmTests|PotBindTests"`
Expected: FAIL - types do not exist.

- [ ] **Step 3: Implement**

`BalambGarden.Engine/Census/JoinConfirm.cs`:

```csharp
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
```

`BalambGarden.Engine/Census/PotBind.cs`:

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Engine: receipt-confirmed join + pot species-uniqueness binding"
```

### Task 3: Engine - estate roster in the ledger

**Files:**
- Create: `BalambGarden.Engine/Ledger/EstateRecord.cs`
- Modify: `BalambGarden.Engine/Ledger/LedgerStore.cs`
- Test: `BalambGarden.Engine.Tests/Ledger/EstateRosterTests.cs`

**Interfaces:**
- Consumes: `EstateKey`.
- Produces: `EstateRecord { EstateKey Key; string Nickname; DateTimeOffset FirstSeen; DateTimeOffset LastVisited; }`; `LedgerStore.Estates : List<EstateRecord>`; `LedgerStore.UpsertEstate(EstateKey, DateTimeOffset) -> EstateRecord`; `EstateRecord.DisplayName` (nickname if set, else `Key.DisplayWardPlot()`).

- [ ] **Step 1: Write the failing tests**

```csharp
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Ledger;

public class EstateRosterTests
{
    private static readonly EstateKey Chelsea = new(340, 11, 32);
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-14T18:00:00Z");

    [Fact] // Frame 2: estates discovered on visit and remembered
    public void UpsertCreatesThenUpdates()
    {
        var store = new LedgerStore();
        var first = store.UpsertEstate(Chelsea, T0);
        Assert.Equal(T0, first.FirstSeen);
        Assert.Equal(T0, first.LastVisited);

        var second = store.UpsertEstate(Chelsea, T0.AddDays(1));
        Assert.Same(first, second);
        Assert.Single(store.Estates);
        Assert.Equal(T0, second.FirstSeen);
        Assert.Equal(T0.AddDays(1), second.LastVisited);
    }

    [Fact]
    public void NicknameWinsDisplay()
    {
        var record = new EstateRecord { Key = Chelsea, FirstSeen = T0, LastVisited = T0 };
        Assert.Equal("Ward 12 Plot 33", record.DisplayName);
        record.Nickname = "Chelsea's";
        Assert.Equal("Chelsea's", record.DisplayName);
    }

    [Fact] // the roster must survive the JSON round trip with beds and bindings intact
    public void RosterRoundTripsThroughJson()
    {
        var store = new LedgerStore();
        store.UpsertEstate(Chelsea, T0).Nickname = "Chelsea's";
        store.Bindings[Chelsea.BindingKey(0)] = 110;
        store.Beds.Add(new ClaimedBed
        {
            Estate = Chelsea, MapKey = 110, PatchOrdinal = 0, BedSlot = 3, ClaimedAt = T0,
        });

        var restored = LedgerStore.FromJson(store.ToJson());
        var estate = Assert.Single(restored.Estates);
        Assert.Equal("Chelsea's", estate.Nickname);
        Assert.Equal(Chelsea, estate.Key);
        Assert.Equal(110, restored.Bindings[Chelsea.BindingKey(0)]);
        Assert.Equal(3, Assert.Single(restored.Beds).BedSlot);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj --filter EstateRosterTests`
Expected: FAIL - `EstateRecord` does not exist.

- [ ] **Step 3: Implement**

`BalambGarden.Engine/Ledger/EstateRecord.cs`:

```csharp
using System.Text.Json.Serialization;
using BalambGarden.Engine.Census;

namespace BalambGarden.Engine.Ledger;

/// <summary>Frame 2: an estate discovered on visit and remembered, with its learned
/// identity. Capacity shape is derived live from bindings + claimed beds, not stored.</summary>
public sealed class EstateRecord
{
    public required EstateKey Key { get; init; }
    public string Nickname { get; set; } = "";
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastVisited { get; set; }

    [JsonIgnore]
    public string DisplayName => Nickname.Length > 0 ? Nickname : Key.DisplayWardPlot();
}
```

In `LedgerStore`, add below `Bindings`:

```csharp
    public List<EstateRecord> Estates { get; set; } = [];

    public EstateRecord UpsertEstate(Census.EstateKey key, DateTimeOffset now)
    {
        var record = Estates.FirstOrDefault(e => e.Key == key);
        if (record is null)
        {
            record = new EstateRecord { Key = key, FirstSeen = now, LastVisited = now };
            Estates.Add(record);
        }
        else
        {
            record.LastVisited = now;
        }
        return record;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Engine: estate roster records in the ledger"
```

### Task 4: Plugin - GardenService spine + Configuration v1

**Files:**
- Create: `BalambGarden/GardenService.cs`
- Modify: `BalambGarden/Configuration.cs`
- Modify: `BalambGarden/Plugin.cs`

**Interfaces:**
- Consumes: `LedgerStore`, `CensusEngine`, `DomainTables`, `DebugTrail`, `ClockWiltSource`.
- Produces: `Plugin.Garden : GardenService` with `Ledger`, `Census`, `Trail`, `Wilt` properties and `Save()`; `Configuration.ClaimOnAction` (default true), `NudgeEnabled` (default true), `TrailEnabled` (default true).

- [ ] **Step 1: Write GardenService**

`BalambGarden/GardenService.cs`:

```csharp
using System;
using System.IO;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;

namespace BalambGarden;

/// <summary>The v2 spine: one ledger file, one census brain, one debug trail.
/// The POC ledger (Configuration.Ledger) is never read - fresh start by spec.</summary>
public sealed class GardenService
{
    private readonly string ledgerPath;

    public LedgerStore Ledger { get; }
    public CensusEngine Census { get; }
    public DebugTrail Trail { get; }
    public IWiltSource Wilt { get; } = new ClockWiltSource();

    private GardenService(string ledgerPath, LedgerStore ledger, string trailPath)
    {
        this.ledgerPath = ledgerPath;
        Ledger = ledger;
        Census = new CensusEngine(ledger) { ClaimOnAction = Plugin.Configuration.ClaimOnAction };
        Trail = new DebugTrail(trailPath);
    }

    public static GardenService Load(string configDirectory)
    {
        Directory.CreateDirectory(configDirectory);
        var ledgerPath = Path.Combine(configDirectory, "ledger-v2.json");
        var trailPath = Path.Combine(configDirectory, "trail.jsonl");

        var ledger = new LedgerStore();
        if (File.Exists(ledgerPath))
        {
            try
            {
                ledger = LedgerStore.FromJson(File.ReadAllText(ledgerPath));
            }
            catch (Exception ex)
            {
                // Fail closed: never overwrite a file we could not read. Park it and start fresh.
                var parked = ledgerPath + $".unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                File.Move(ledgerPath, parked);
                Plugin.Log.Error($"[Garden] ledger unreadable ({ex.Message}) - parked at {parked}, starting fresh");
            }
        }

        return new GardenService(ledgerPath, ledger, trailPath);
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(ledgerPath, Ledger.ToJson());
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Garden] ledger save failed: {ex.Message}");
        }
    }
}
```

- [ ] **Step 2: Extend Configuration**

In `Configuration.cs`, bump `Version` to `1` and add below the pacing knobs:

```csharp
    // v2 census behavior (spec: claim-on-action, arrival nudge, debug trail).
    public bool ClaimOnAction { get; set; } = true;
    public bool NudgeEnabled { get; set; } = true;
    public bool TrailEnabled { get; set; } = true;
```

Leave `Ledger` (the POC list) in place, unread - deleting the property would throw away Chelsea's existing config file shape for no gain.

- [ ] **Step 3: Wire into Plugin**

In `Plugin.cs`: add `public static GardenService Garden { get; private set; } = null!;` and in the constructor, after `Tables = ...`:

```csharp
        Garden = GardenService.Load(PluginInterface.GetPluginConfigDirectory());
```

In `Dispose()`, before `ECommonsMain.Dispose()`: `Garden.Save();`

- [ ] **Step 4: Build**

Run: `dotnet build BalambGarden.sln -c Debug -p:Platform=x64`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Plugin: GardenService spine - v2 ledger file, census brain, trail"
```

### Task 5: Plugin - the three sensors

**Files:**
- Create: `BalambGarden/Game/EstateSensor.cs`
- Create: `BalambGarden/Game/MapSensor.cs`
- Create: `BalambGarden/Game/ObjectSensor.cs`
- Delete: `BalambGarden/GardenScanner.cs` (its recon table use in MainWindow moves to ObjectSensor in Task 7's minimal window)

**Interfaces:**
- Consumes: Engine `MapFormat`, `BedReading`, `PotReading`, `GimmickId`, `BedGimmick`, `EstateKey`; game APIs exactly as receipt-verified in `probe:BalambGarden/ReconProbe.cs`.
- Produces:
  - `EstateSensor.Current() -> EstateKey?` and `EstateSensor.IsInside() -> bool`.
  - `MapSensor.ReadOutdoor() -> Dictionary<int, IReadOnlyList<BedReading>>`; `MapSensor.ReadIndoor() -> Dictionary<int, PotReading>`; both empty when not applicable. `MapSensor.UnreadableCount` (int, last read) for the "N unreadable" surface.
  - `ObjectSensor.NearbyBeds(float) -> List<BedObject>` where `BedObject(IGameObject Object, BedGimmick Gimmick, float Distance, bool Targetable)`; `ObjectSensor.Patches() -> List<PatchGroup>` where `PatchGroup(ushort PatchId, int Ordinal, Vector3 Center, List<BedObject> Beds, float Distance)` with `InReach => Distance <= EventObjRange (4.6f)`; `ObjectSensor.NearbyPots(float) -> List<PotObject>` where `PotObject(IGameObject Object, string Name, float Distance, bool InReach)`.

- [ ] **Step 1: EstateSensor**

`BalambGarden/Game/EstateSensor.cs`:

```csharp
using BalambGarden.Engine.Census;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BalambGarden.Game;

/// <summary>Estate identity from HousingManager, raw 0-based (verified via probe
/// 08-12/08-13). Room rides only when inside; outdoors an estate is (territory,
/// ward, plot).</summary>
internal static unsafe class EstateSensor
{
    internal static EstateKey? Current()
    {
        var housing = HousingManager.Instance();
        if (housing == null || housing->CurrentTerritory == null)
            return null;

        var ward = housing->GetCurrentWard();
        var plot = housing->GetCurrentPlot();
        if (ward < 0 || plot < 0)
            return null;

        var room = housing->IsInside() ? housing->GetCurrentRoom() : -1;
        return new EstateKey(Plugin.ClientState.TerritoryType, ward, plot, room);
    }

    internal static bool IsInside()
    {
        var housing = HousingManager.Instance();
        return housing != null && housing->IsInside();
    }
}
```

- [ ] **Step 2: MapSensor**

`BalambGarden/Game/MapSensor.cs`:

```csharp
using System;
using System.Collections.Generic;
using BalambGarden.Engine.Sensing;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BalambGarden.Game;

/// <summary>Stateless reader of the housing DataMap - the plant sensor for both
/// outdoor patches (48-byte entries, 8 beds) and indoor pots (same block, sub-entry 0).
/// Reading path receipt-verified in ReconProbe (probe branch) across 08-12/08-13.</summary>
internal static unsafe class MapSensor
{
    /// <summary>Entries whose bytes failed to decode on the last read - the honest
    /// "N unreadable" surface for the dashboard.</summary>
    internal static int UnreadableCount { get; private set; }

    private static Dictionary<int, byte[]> ReadRawEntries()
    {
        var result = new Dictionary<int, byte[]>();
        var housing = HousingManager.Instance();
        if (housing == null || housing->CurrentTerritory == null)
            return result;

        var outdoor = housing->OutdoorTerritory;
        var indoor = housing->IndoorTerritory;
        HousingFurnitureManager* furniture =
            outdoor != null ? &outdoor->FurnitureManager
            : indoor != null ? &indoor->FurnitureManager
            : null;
        if (furniture == null)
            return result;

        foreach (var pair in furniture->ObjectManager.DataMap)
        {
            var data = pair.Item2;
            var bytes = new byte[MapFormat.OutdoorEntrySize];
            var p = (byte*)&data;
            var n = Math.Min(sizeof(HousingObjectManager.HousingObjectData), bytes.Length);
            for (var i = 0; i < n; i++)
                bytes[i] = p[i];
            result[(int)pair.Item1] = bytes;
        }
        return result;
    }

    /// <summary>Outdoor: every non-empty 8-bed entry decoded. Unclaimed data stays
    /// ephemeral - callers hold this for the session, never persist it.</summary>
    internal static Dictionary<int, IReadOnlyList<BedReading>> ReadOutdoor()
    {
        var result = new Dictionary<int, IReadOnlyList<BedReading>>();
        UnreadableCount = 0;
        if (EstateSensor.IsInside())
            return result;

        foreach (var (key, bytes) in ReadRawEntries())
        {
            try
            {
                var beds = MapFormat.DecodeOutdoorEntry(bytes);
                if (beds.Any(b => b.Occupied))
                    result[key] = beds;
            }
            catch (Exception ex)
            {
                UnreadableCount++;
                Plugin.Log.Warning($"[MapSensor] key={key} unreadable: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>Indoor: single-plant pot entries only; multi-slot furniture rejected
    /// by the decoder (aquariums etc., 08-13).</summary>
    internal static Dictionary<int, PotReading> ReadIndoor()
    {
        var result = new Dictionary<int, PotReading>();
        UnreadableCount = 0;
        if (!EstateSensor.IsInside())
            return result;

        foreach (var (key, bytes) in ReadRawEntries())
        {
            try
            {
                if (MapFormat.DecodeIndoorEntry(
                        bytes, s => Plugin.Tables.SeedIdBySpeciesIndex(s) is not null)
                    is { Occupied: true } pot)
                    result[key] = pot;
            }
            catch (Exception ex)
            {
                UnreadableCount++;
                Plugin.Log.Warning($"[MapSensor] indoor key={key} unreadable: {ex.Message}");
            }
        }
        return result;
    }
}
```

- [ ] **Step 3: ObjectSensor**

`BalambGarden/Game/ObjectSensor.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BalambGarden.Engine.Sensing;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace BalambGarden.Game;

internal readonly record struct BedObject(
    IGameObject Object, BedGimmick Gimmick, float Distance, bool Targetable)
{
    internal bool InReach => Targetable && Distance <= ObjectSensor.EventObjRange;
}

internal sealed record PatchGroup(
    ushort PatchId, int Ordinal, Vector3 Center, List<BedObject> Beds, float Distance)
{
    internal bool InReach => Distance <= ObjectSensor.EventObjRange;
}

internal readonly record struct PotObject(IGameObject Object, string Name, float Distance)
{
    internal bool InReach => Distance <= ObjectSensor.HousingEventObjRange;
}

/// <summary>Nearby bed/pot objects. Patches group by GimmickId patch-id (the game's
/// own identity, receipt-verified 08-12/08-13) - never by position clustering.</summary>
internal static unsafe class ObjectSensor
{
    internal const uint GardenBedDataId = 2003757;
    internal const float EventObjRange = 4.6f;          // field-verified 08-11
    internal const float HousingEventObjRange = 6.5f;

    internal static List<BedObject> NearbyBeds(float maxDistance = 40f)
    {
        var beds = new List<BedObject>();
        if (!Player.Available || Player.Object is not { } me)
            return beds;

        foreach (var obj in Svc.Objects)
        {
            if (obj is null || !obj.IsValid())
                continue;
            if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject))
                continue;
            if (obj.BaseId != GardenBedDataId)
                continue;

            var distance = Vector3.Distance(me.Position, obj.Position);
            if (distance > maxDistance)
                continue;

            var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
            if (native == null)
                continue;

            beds.Add(new BedObject(obj, GimmickId.Decode(native->GimmickId), distance, obj.IsTargetable));
        }
        return beds;
    }

    internal static List<PatchGroup> Patches(float maxDistance = 40f)
        => NearbyBeds(maxDistance)
            .GroupBy(b => b.Gimmick.PatchId)
            .Select(g =>
            {
                var beds = g.OrderBy(b => b.Gimmick.BedIndex).ToList();
                // All beds in a patch share the centre position (08-11): in range of
                // the centre IS in range of every bed.
                return new PatchGroup(
                    g.Key, beds[0].Gimmick.PatchOrdinal, beds[0].Object.Position,
                    beds, beds.Min(b => b.Distance));
            })
            .OrderBy(p => p.Ordinal)
            .ToList();

    /// <summary>Indoor pots by name ("Flowerpot" models). Pots are dumb props with
    /// per-model DataIds (08-13) - the name filter is the honest v1 identifier; a
    /// pot the filter misses simply shows no verbs, never a wrong one.</summary>
    internal static List<PotObject> NearbyPots(float maxDistance = 20f)
    {
        var pots = new List<PotObject>();
        if (!EstateSensor.IsInside() || !Player.Available || Player.Object is not { } me)
            return pots;

        foreach (var obj in Svc.Objects)
        {
            if (obj is null || !obj.IsValid() || !obj.IsTargetable)
                continue;
            if (obj.ObjectKind != ObjectKind.HousingEventObject)
                continue;
            var name = obj.Name.TextValue;
            if (!name.Contains("Flowerpot", StringComparison.OrdinalIgnoreCase))
                continue;

            var distance = Vector3.Distance(me.Position, obj.Position);
            if (distance > maxDistance)
                continue;
            pots.Add(new PotObject(obj, name, distance));
        }
        return pots.OrderBy(p => p.Distance).ToList();
    }
}
```

Delete `BalambGarden/GardenScanner.cs`. MainWindow references break; Task 7 rewrites MainWindow - to keep the build green within this task, replace the bodies of `DrawPatches`/`DrawRecon` in `MainWindow.cs` with the ObjectSensor equivalents (`ObjectSensor.Patches()`, `ObjectSensor.NearbyBeds()`), mapping `patch.Beds.Count`, `patch.InReach`, `patch.Distance` one-to-one and using `bed.Object` for the tend buttons.

- [ ] **Step 4: Build**

Run: `dotnet build BalambGarden.sln -c Debug -p:Platform=x64`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Plugin: estate/map/object sensors from receipt-verified probe paths"
```

### Task 6: Plugin - CensusPump (sightings, visits, receipt routing, nudge)

**Files:**
- Create: `BalambGarden/Game/CensusPump.cs`
- Modify: `BalambGarden/Plugin.cs` (framework tick wiring)

**Interfaces:**
- Consumes: all three sensors, `Plugin.Garden` (Census/Ledger/Trail), Engine `JoinShortlist`, `JoinConfirm`, `PotBind`, `ReceiptParser`, `Rollups.ArrivalNudge`.
- Produces (used by chains in Task 7+ and the dashboard):
  - `CensusPump.Tick()` - called from `Framework.Update`, self-throttled (2s), handles visit upsert + one-shot arrival nudge + periodic sighting while a garden window is open.
  - `CensusPump.SightNow()` - reads the map, feeds `OnMapSighting` for claimed beds, refreshes `LastOutdoor` / `LastIndoor` session snapshots.
  - `CensusPump.LastOutdoor : IReadOnlyDictionary<int, IReadOnlyList<BedReading>>`, `CensusPump.LastIndoor : IReadOnlyDictionary<int, PotReading>`.
  - `CensusPump.OnBedReceipt(ReceiptVerb verb, string bedHeader, string plantName, byte? stageOverride = null) -> string` - full routing: parse, bind-if-needed, claim/observe, trail, save; returns a short outcome string for the run log.
  - `CensusPump.OnPotReceipt(ReceiptVerb verb, string plantName) -> string` - the pot path (species-uniqueness bind).
  - `CensusPump.OnRipeSkip(string bedHeader, string plantName) -> string` - a skipped ripe bed recorded as a stage-4 `RipeSkip` observation.

- [ ] **Step 1: Write CensusPump**

`BalambGarden/Game/CensusPump.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using ECommons.DalamudServices;

namespace BalambGarden.Game;

/// <summary>The census heartbeat. Sensors read, receipts route, the ledger learns.
/// Acting IS censusing: every chain completion lands here.</summary>
internal static class CensusPump
{
    private static DateTime nextTickUtc = DateTime.MinValue;
    private static EstateKey? announcedEstate;

    internal static IReadOnlyDictionary<int, IReadOnlyList<BedReading>> LastOutdoor
        { get; private set; } = new Dictionary<int, IReadOnlyList<BedReading>>();
    internal static IReadOnlyDictionary<int, PotReading> LastIndoor
        { get; private set; } = new Dictionary<int, PotReading>();

    internal static void Tick()
    {
        if (DateTime.UtcNow < nextTickUtc)
            return;
        nextTickUtc = DateTime.UtcNow.AddSeconds(2);

        var estate = EstateSensor.Current();
        if (estate is null)
        {
            announcedEstate = null;
            return;
        }

        // First tick at a new estate: visit + sight + (maybe) the one chat line.
        if (announcedEstate != estate)
        {
            SightNow();
            // The map can populate a beat after zone-in; an empty read means try
            // again next tick rather than announcing a garden we haven't seen.
            if (LastOutdoor.Count == 0 && LastIndoor.Count == 0
                && Plugin.Garden.Ledger.Beds.Any(b => b.Estate == estate))
                return;

            announcedEstate = estate;
            Plugin.Garden.Ledger.UpsertEstate(estate, DateTimeOffset.UtcNow);
            Plugin.Garden.Save();

            if (Plugin.Configuration.NudgeEnabled)
            {
                var rollups = Rollups.ForEstate(
                    estate, Plugin.Garden.Census.LedgerBeds, Plugin.Tables,
                    Plugin.Garden.Wilt, DateTimeOffset.UtcNow);
                if (Rollups.ArrivalNudge(estate, rollups) is { } line)
                    Svc.Chat.Print(line);
            }
        }
    }

    internal static void SightNow()
    {
        var estate = EstateSensor.Current();
        if (estate is null)
            return;

        var now = DateTimeOffset.UtcNow;
        if (EstateSensor.IsInside())
        {
            LastIndoor = MapSensor.ReadIndoor();
            foreach (var (key, pot) in LastIndoor)
            {
                Plugin.Garden.Census.OnMapSighting(estate, key,
                    [new BedReading(0, pot.SpeciesIndex, pot.Stage, pot.Extra, pot.Occupied)], now);
            }
        }
        else
        {
            LastOutdoor = MapSensor.ReadOutdoor();
            foreach (var (key, beds) in LastOutdoor)
                Plugin.Garden.Census.OnMapSighting(estate, key, beds, now);
        }
    }

    internal static string OnBedReceipt(
        ReceiptVerb verb, string bedHeader, string plantName, byte? stageOverride = null)
    {
        var estate = EstateSensor.Current();
        if (estate is null)
            return "no estate identity - receipt logged only";

        if (ReceiptParser.ParseBedHeader(bedHeader) is not { } parsed)
            return $"unparseable bed header '{bedHeader}' - receipt logged only";

        SightNow();   // acting is censusing: fresh map before the receipt lands

        var species = Plugin.Tables.SpeciesIndexByName(plantName) ?? 0;
        if (species == 0 && plantName.Length > 0)
            Plugin.Log.Warning($"[Census] unknown plant name '{plantName}' - observing as unknown");

        // Bind if this patch has no key yet: shortlist from object patch-ids x map
        // keys, confirmed by THIS receipt's species at (ordinal, slot).
        if (Plugin.Garden.Census.BoundKey(estate, parsed.PatchOrdinal) is null && species != 0)
        {
            var patchIds = ObjectSensor.Patches().Select(p => p.PatchId).ToList();
            var candidates = JoinShortlist.Candidates(patchIds, LastOutdoor.Keys.ToList());
            var confirmed = JoinConfirm.Confirm(
                candidates, parsed.PatchOrdinal, parsed.BedSlot, species,
                key => LastOutdoor.GetValueOrDefault(key));
            if (confirmed is not null)
            {
                for (var ordinal = 0; ordinal < confirmed.Count; ordinal++)
                    Plugin.Garden.Census.Bind(estate, ordinal, confirmed[ordinal]);
                Plugin.Log.Information(
                    $"[Census] receipt bound {estate.DisplayWardPlot()}: keys {string.Join(",", confirmed)}");
            }
        }

        var stage = stageOverride
            ?? (Plugin.Garden.Census.BoundKey(estate, parsed.PatchOrdinal) is { } key
                && LastOutdoor.TryGetValue(key, out var beds)
                && parsed.BedSlot < beds.Count
                ? beds[parsed.BedSlot].Stage : (byte)0);

        var receipt = new ReceiptEvent(
            estate, parsed.PatchOrdinal, parsed.BedSlot, verb, species, stage,
            DateTimeOffset.UtcNow);
        return Deliver(receipt, $"{bedHeader}: {DisplayPlant(plantName)}");
    }

    internal static string OnPotReceipt(ReceiptVerb verb, string plantName)
    {
        var estate = EstateSensor.Current();
        if (estate is null)
            return "no estate identity - receipt logged only";

        SightNow();
        var species = Plugin.Tables.SpeciesIndexByName(plantName) ?? 0;
        if (species == 0)
            return $"pot plant '{plantName}' unknown - cannot bind, receipt logged only";

        var key = PotBind.UniqueSpeciesKey(species, LastIndoor);
        if (key is null)
            return $"pot with {plantName} is ambiguous (several or none in map) - unbound";

        // A pot is its own one-bed patch: ordinal = map key, slot 0.
        Plugin.Garden.Census.Bind(estate, key.Value, key.Value);
        var stage = LastIndoor.TryGetValue(key.Value, out var pot) ? pot.Stage : (byte)0;
        var receipt = new ReceiptEvent(
            estate, key.Value, 0, verb, species, stage, DateTimeOffset.UtcNow, IsPot: true);
        return Deliver(receipt, $"pot (key {key}): {DisplayPlant(plantName)}");
    }

    internal static string OnRipeSkip(string bedHeader, string plantName)
    {
        // A ripe bed offers no tend - the skip itself is a stage-4 sighting (spec).
        var estate = EstateSensor.Current();
        if (estate is null || ReceiptParser.ParseBedHeader(bedHeader) is not { } parsed)
            return $"{bedHeader}: skipped (ripe?) - not recorded";

        var species = Plugin.Tables.SpeciesIndexByName(plantName) ?? 0;
        var bed = Plugin.Garden.Census.LedgerBeds.FirstOrDefault(b =>
            b.Estate == estate && b.PatchOrdinal == parsed.PatchOrdinal
            && b.BedSlot == parsed.BedSlot && !b.IsPot);
        if (bed is null)
            return $"{bedHeader}: skipped (ripe, unclaimed - tend won't claim a bed it can't touch)";

        bed.Observe(new Observation(
            DateTimeOffset.UtcNow,
            species != 0 ? species : bed.Latest?.SpeciesIndex ?? 0,
            4, ObservationSource.RipeSkip));
        Plugin.Garden.Save();
        return $"{bedHeader}: {DisplayPlant(plantName)} - ripe, skipped (recorded)";
    }

    private static string Deliver(ReceiptEvent receipt, string label)
    {
        if (Plugin.Configuration.TrailEnabled)
            Plugin.Garden.Trail.Append(receipt);

        var bed = Plugin.Garden.Census.OnReceipt(receipt);
        Plugin.Garden.Save();
        return bed is null
            ? $"{label} - done (not claimed: {(Plugin.Configuration.ClaimOnAction ? "patch unbound" : "claim-as-I-go off")})"
            : $"{label} - done";
    }

    private static string DisplayPlant(string plantName)
        => plantName.Length > 0 ? plantName : "?";
}
```

- [ ] **Step 2: Wire the tick + config sync**

In `Plugin.cs` constructor, after window setup: `Svc.Framework.Update += OnFrameworkUpdate;` with:

```csharp
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!PlayerState.IsLoaded)
            return;
        Game.CensusPump.Tick();
    }
```

and in `Dispose()`: `Svc.Framework.Update -= OnFrameworkUpdate;` (add `using Dalamud.Plugin.Services;` and `using ECommons.DalamudServices;` as needed). Census claim flag follows config: wherever the config value changes (ConfigWindow/Dashboard checkbox), set `Plugin.Garden.Census.ClaimOnAction = value` alongside `Configuration.Save()`.

- [ ] **Step 3: Build**

Run: `dotnet build BalambGarden.sln -c Debug -p:Platform=x64`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "Plugin: census pump - sightings, receipt routing, visit nudge"
```

### Task 7: Chains - ChainBase + TendChain v2 + minimal dashboard, bench checkpoint A

**Files:**
- Create: `BalambGarden/Chains/ChainBase.cs`
- Create: `BalambGarden/Chains/TendChain.cs`
- Delete: `BalambGarden/TendChain.cs`
- Modify: `BalambGarden/Windows/MainWindow.cs` (minimal v2: estate line, patch buttons, claimed-bed table)
- Modify: `BalambGarden/Plugin.cs` (namespace of TendChain)

**Interfaces:**
- Consumes: `CensusPump.OnBedReceipt/OnRipeSkip`, `ObjectSensor.Patches`, POC pacing knobs.
- Produces:
  - `ChainBase` (abstract): `Busy`, `LastOutcome`, `Report`, `RunStartUtc`, `TotalUnits`, `Elapsed`, `Eta`, `Abort(string reason)`, protected `TaskManager`, `PaceReady()`, `Acted()`, `ApplyJitter(int)`, `ApplyJitter(int, int)`, `BeginRun(int units, string startOutcome) -> bool` (occupied guard + telemetry reset + derived TimeLimitMS), `RecordOutcome(string)`, `FinishRun(string summary)`. Everything the POC TendChain proved, verbatim in behavior: pacing gate, jitter floor 250ms, ETA anchored at unit completions (the sawtooth fix), sticky report.
  - `TendChain : ChainBase` with `TendPatch(PatchGroup)`, `TendAll(IEnumerable<PatchGroup>)`, `TendOne(BedObject)`; every completed tend calls `CensusPump.OnBedReceipt(ReceiptVerb.Tend, header, plant)`; a menu without Tend but with Harvest calls `CensusPump.OnRipeSkip(header, plant)`; other no-tend menus quit honestly with the POC's outcome line.

- [ ] **Step 1: Extract ChainBase**

`BalambGarden/Chains/ChainBase.cs` - lift from the POC `TendChain.cs` (repo history has it; it is also reproduced in full in the current working tree before deletion) exactly these members, renamed where noted:

```csharp
using System;
using System.Collections.Generic;
using ECommons;
using ECommons.Automation.LegacyTaskManager;

namespace BalambGarden.Chains;

/// <summary>Paced dialogue-chain framework (Scrooge lineage via the POC TendChain):
/// human tempo + jitter, telemetry for the run log, occupied-player guard, derived
/// task ceiling, clean stop with a stated reason.</summary>
internal abstract class ChainBase : IDisposable
{
    protected readonly TaskManager TaskManager = new()
    {
        TimeLimitMS = 10000,
        AbortOnTimeout = true,
    };

    private readonly Random random = new();
    private DateTime nextActionAt = DateTime.MinValue;
    private DateTime lastUnitAt;

    internal bool Busy => TaskManager.IsBusy;
    internal string LastOutcome { get; private protected set; } = "idle";
    internal List<string> Report { get; } = [];
    internal DateTime RunStartUtc { get; private set; }
    internal int TotalUnits { get; private set; }

    internal TimeSpan Elapsed => Busy ? DateTime.UtcNow - RunStartUtc : TimeSpan.Zero;

    /// <summary>Countdown ETA, pace frozen at unit boundaries (POC/Scrooge ruling -
    /// a live-clock pace bills the current unit's wait to every future unit).</summary>
    internal TimeSpan? Eta
    {
        get
        {
            if (!Busy || TotalUnits == 0)
                return null;
            var done = Report.Count;
            var remaining = TotalUnits - done;
            if (remaining <= 0)
                return null;

            double msPerUnit;
            DateTime anchor;
            if (done > 0)
            {
                msPerUnit = (lastUnitAt - RunStartUtc).TotalMilliseconds / done;
                anchor = lastUnitAt;
            }
            else
            {
                msPerUnit = SeedMsPerUnit();
                anchor = RunStartUtc;
            }

            var spent = (DateTime.UtcNow - anchor).TotalMilliseconds;
            return TimeSpan.FromMilliseconds(Math.Max(0, (msPerUnit * remaining) - spent));
        }
    }

    /// <summary>Pre-first-completion ETA seed; chains override with their own shape.</summary>
    protected virtual double SeedMsPerUnit()
        => Plugin.Configuration.PostTendDelayMS + (4.0 * Plugin.Configuration.TendPaceMS);

    protected bool PaceReady() => DateTime.UtcNow >= nextActionAt;

    protected void Acted()
        => nextActionAt = DateTime.UtcNow.AddMilliseconds(ApplyJitter(Plugin.Configuration.TendPaceMS));

    protected int ApplyJitter(int baseMS) => ApplyJitter(baseMS, Plugin.Configuration.JitterMS);

    // No global jitter kill-switch (Scrooge ruling): zeroing a slider is deliberate.
    protected int ApplyJitter(int baseMS, int jitterMS)
    {
        if (jitterMS <= 0)
            return baseMS;
        var offset = (int)(((random.NextDouble() * 2.0) - 1.0) * jitterMS);
        return Math.Max(250, baseMS + offset);
    }

    /// <summary>Occupied guard + telemetry reset + task ceiling derived above the
    /// longest tunable step. False = refused (reason already in LastOutcome).</summary>
    protected bool BeginRun(int units, string startOutcome)
    {
        if (TaskManager.IsBusy || units == 0)
            return false;
        if (GenericHelpers.IsOccupied())
        {
            LastOutcome = "can't start: you're busy (in a dialog, cutscene, or event)";
            return false;
        }

        TaskManager.TimeLimitMS = Math.Max(
            15000, Plugin.Configuration.PostTendDelayMS + Plugin.Configuration.PostTendJitterMS + 5000);
        stopRequested = false;
        Report.Clear();
        RunStartUtc = DateTime.UtcNow;
        lastUnitAt = RunStartUtc;
        TotalUnits = units;
        LastOutcome = startOutcome;
        return true;
    }

    /// <summary>One unit's outcome line: feeds the report and anchors the ETA.</summary>
    protected void RecordOutcome(string line)
    {
        Report.Add(line);
        lastUnitAt = DateTime.UtcNow;
    }

    private bool stopRequested;

    /// <summary>The user's stop: honored at the NEXT unit boundary, never mid-dialogue
    /// (spec: interruption stops clean at a bed boundary). Chains call
    /// CheckStop() as the first step of every unit.</summary>
    internal void RequestStop() => stopRequested = true;

    /// <summary>Unit-boundary gate. Enqueue as each unit's first task: true = carry on,
    /// aborts the run cleanly when a stop was requested.</summary>
    protected bool CheckStop(string unitLabel)
    {
        if (!stopRequested)
            return true;
        Abort($"stopped by user before {unitLabel}");
        return true;   // this task completed; the queue behind it is gone
    }

    /// <summary>Hard stop with a stated reason - for stale state and broken menus,
    /// where continuing would be worse than an abrupt end. User stops go through
    /// RequestStop instead.</summary>
    internal void Abort(string reason = "aborted")
    {
        TaskManager.Abort();
        LastOutcome = $"stopped at {Report.Count}/{TotalUnits} - {reason}";
    }

    public void Dispose() => TaskManager.Abort();
}
```

- [ ] **Step 2: Rewrite TendChain on the base**

`BalambGarden/Chains/TendChain.cs` - carry the POC's step methods (`Interact`, `AdvanceToMenu`, `CapturePlantName`, `FinishDialogue`, `ReadBedHeader`, `ReadStringValue`, `DumpStrings`) unchanged except:

- Class declaration: `internal sealed unsafe class TendChain : ChainBase` - drop the fields/members now inherited (task manager, pacing, telemetry, report, ETA, Abort, Dispose).
- Entry points take sensor types:

```csharp
    internal void TendOne(BedObject bed) => Tend([bed]);
    internal void TendPatch(PatchGroup patch) => Tend(patch.Beds);
    internal void TendAll(IEnumerable<PatchGroup> patches)
        => Tend(patches.SelectMany(p => p.Beds).ToList());

    private void Tend(List<BedObject> beds)
    {
        if (!BeginRun(beds.Count,
                beds.Count == 1 ? "tending bed..." : $"watering {beds.Count} beds..."))
            return;

        for (var i = 0; i < beds.Count; i++)
        {
            var bed = beds[i];
            TaskManager.DelayNext(i == 0
                ? ApplyJitter(Plugin.Configuration.TendPaceMS)
                : ApplyJitter(Plugin.Configuration.PostTendDelayMS, Plugin.Configuration.PostTendJitterMS));
            var label = $"bed {i + 1}/{beds.Count}";
            TaskManager.Enqueue(() => CheckStop(label), $"gate {i}");
            TaskManager.Enqueue(() => Interact(bed.Object), $"interact {i}");
            TaskManager.Enqueue(AdvanceToMenu, $"advance {i}");
            TaskManager.Enqueue(TendOrQuit, $"tend {i}");
            TaskManager.Enqueue(FinishDialogue, $"finish {i}");
        }

        var total = beds.Count;
        TaskManager.Enqueue(() =>
        {
            var tended = Report.Count(r => r.Contains("- done", StringComparison.Ordinal));
            LastOutcome = $"done: {tended}/{total} tended";
            foreach (var line in Report)
                Plugin.Log.Information($"[TendChain] report: {line}");
            return true;
        }, "report");
    }
```

- `TendOrQuit` outcome wiring replaces the POC ledger upsert (`RecordTend` is deleted; `_currentBedPos` no longer needed):
  - On a successful Tend selection: `RecordOutcome(Game.CensusPump.OnBedReceipt(Engine.Census.ReceiptVerb.Tend, header, _currentPlant));`
  - On no Tend but a `Harvest` entry present: select `Quit`, then `RecordOutcome(Game.CensusPump.OnRipeSkip(header, _currentPlant));`
  - On no Tend and no Harvest: select `Quit`, `RecordOutcome($"{header}: skipped (no tend option - empty or no rights?)");`
  - Unrecognized menu: unchanged POC behavior but via `Abort("unrecognized menu")`.
- `Interact`'s stale-bed branch uses `Abort("bed list went stale (zone change?)")` after recording the skip.

Update `Plugin.cs`: `using BalambGarden.Chains;` and construct `TendChain` as before.

- [ ] **Step 3: Minimal dashboard**

Replace `MainWindow.Draw` content (full rewrite comes in Stage 3; this keeps Stage 1 honest and benchable):

- Header: estate line - `EstateSensor.Current()` is null -> "Not at a housing estate."; else `record.DisplayName` (via `Plugin.Garden.Ledger.UpsertEstate` result read-only lookup - do NOT upsert from Draw; look up in `Plugin.Garden.Ledger.Estates`, fall back to `estate.DisplayWardPlot()`), plus `MapSensor.UnreadableCount > 0 ? $" - {count} unreadable" : ""`.
- "Claim as I go" checkbox bound to `Plugin.Configuration.ClaimOnAction`, syncing `Plugin.Garden.Census.ClaimOnAction` and saving on change.
- Patch buttons exactly as the POC (`Tend All` + per-patch `Water Patch` from `ObjectSensor.Patches()`, reach coloring), launching `plugin.TendChain` and opening the run log.
- Claimed-beds table for the current estate: columns Bed (`PatchOrdinal+1`/`BedSlot+1` or "pot key N"), Plant (`Plugin.Tables.SpeciesName(bed.Latest?.SpeciesIndex ?? 0)` or "?"), Stage (`bed.Latest?.Stage`), Water (`Plugin.Garden.Wilt.StateFor(bed, crop, now)` when the crop resolves, else "?"), Last seen (age of `bed.Latest?.At`, POC `Ago` helper).
- Keep the recon collapsing section, now reading `ObjectSensor.NearbyBeds()` (name/gimmick hex/distance/reach + per-bed Tend button).

- [ ] **Step 4: Build + full test suite**

Run: `dotnet build BalambGarden.sln -c Debug -p:Platform=x64`
Expected: Build succeeded, 0 errors.
Run: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Chains: ChainBase framework + TendChain v2 emitting census receipts; minimal v2 dashboard"
```

- [ ] **Step 6: BENCH CHECKPOINT A (Sam at the keyboard, Sam's own garden)**

Reload the plugin, then at Sam's house:
1. `/garden` - estate line shows the right ward/plot; no beds claimed yet.
2. Run Water Patch on Sam's patch. Expected: run log narrates; after the run, beds appear in the claimed table with plant names, stage, and "just now" ages; `ledger-v2.json` exists in the config dir with bindings + beds.
3. Re-open after a `/xlplugins` reload: claims persist.
4. Zone out and back in: arrival nudge prints one line if anything is Due/ripe, silence otherwise.
5. Sam rules the save point: merge `rebuild` -> `main`.

---

## Stage 2 - Cycle + pots (save point B)

### Task 8: Data - soils table

**Files:**
- Create: `tools/build-soils.mjs`
- Create: `Data/Soils.json`
- Create: `BalambGarden.Engine/Domain/Soil.cs`
- Modify: `BalambGarden.Engine/Domain/DomainTables.cs`
- Modify: `BalambGarden.Engine/BalambGarden.Engine.csproj` (embed)
- Test: `BalambGarden.Engine.Tests/Domain/SoilsTests.cs`

**Interfaces:**
- Consumes: xivapi (generation time only).
- Produces: `Soil(uint ItemId, string Name, int Grade)`; `DomainTables.Soils : IReadOnlyList<Soil>` (ordered by ItemId); `DomainTables.SoilByItemId(uint) -> Soil?`.

- [ ] **Step 1: Generator**

`tools/build-soils.mjs` (same idiom as `tools/build-domain-data.mjs` - fetch, distill, write; sources snapshot to `tools/source/`):

```javascript
// Builds Data/Soils.json from xivapi item search: every "* Topsoil" gardening item.
// Run: node tools/build-soils.mjs   (network required; snapshot lands in tools/source/)
import { writeFileSync } from "node:fs";

const url = "https://v2.xivapi.com/api/search?sheets=Item&query=Name~%22topsoil%22&fields=Name,ItemUICategory.Name&limit=100";
const res = await fetch(url);
if (!res.ok) throw new Error(`xivapi ${res.status}`);
const body = await res.json();
writeFileSync("tools/source/xivapi_topsoil.json", JSON.stringify(body, null, 2));

const soils = body.results
  .filter(r => r.fields.ItemUICategory?.fields?.Name === "Gardening Items")
  .map(r => ({
    itemId: r.row_id,
    name: r.fields.Name,
    grade: /Grade (\d)/.exec(r.fields.Name) ? Number(/Grade (\d)/.exec(r.fields.Name)[1]) : 0,
  }))
  .sort((a, b) => a.itemId - b.itemId);

if (soils.length < 9) throw new Error(`only ${soils.length} soils - xivapi shape changed?`);
writeFileSync("Data/Soils.json", JSON.stringify(soils, null, 2));
console.log(`wrote ${soils.length} soils`);
```

Run it; commit the generated `Data/Soils.json` and the snapshot. If the xivapi response shape differs from this script's assumption, fix the script against the actual response - the snapshot is the receipt.

- [ ] **Step 2: Failing test**

```csharp
using BalambGarden.Engine.Domain;
using Xunit;

namespace BalambGarden.Engine.Tests.Domain;

public class SoilsTests
{
    [Fact]
    public void SoilsLoadAndLookUp()
    {
        var tables = DomainTables.Load();
        Assert.True(tables.Soils.Count >= 9);   // 3 regions x 3 grades minimum
        Assert.All(tables.Soils, s => Assert.Contains("Topsoil", s.Name));
        var first = tables.Soils[0];
        Assert.Equal(first, tables.SoilByItemId(first.ItemId));
        Assert.Null(tables.SoilByItemId(1));
    }
}
```

Run: `dotnet test ... --filter SoilsTests` -> Expected: FAIL.

- [ ] **Step 3: Implement**

`Soil.cs`: `public sealed record Soil(uint ItemId, string Name, int Grade);`

Embed in the Engine csproj ItemGroup: `<EmbeddedResource Include="..\Data\Soils.json" LogicalName="Data.Soils.json" />`

In `DomainTables`: load in `Load()` (same `ReadJson` idiom - array of `{itemId, name, grade}`), store `private readonly List<Soil> soils;` (constructor parameter like the others), expose:

```csharp
    public IReadOnlyList<Soil> Soils => soils;
    public Soil? SoilByItemId(uint itemId) => soils.FirstOrDefault(s => s.ItemId == itemId);
```

- [ ] **Step 4: Run tests** -> all pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Data: soils table generated from xivapi, embedded in Engine"
```

### Task 9: Plant-flow recon instrument + bench recon

**Files:**
- Create: `BalambGarden/Chains/PlantFlow.cs` (instrument half only in this task)
- Modify: `BalambGarden/Windows/MainWindow.cs` (recon section: "Log plant-flow addons" toggle, `#if DEBUG`)

**Interfaces:**
- Consumes: ECommons `GenericHelpers.TryGetAddonByName`, addon events.
- Produces: `PlantFlow.StartWatching()` / `StopWatching()` / `Watching` - while on, every frame logs any of the addons `HousingGardening`, `ContextIconMenu`, `SelectYesno`, `SelectString`, `Talk` that is visible: all string AtkValues (the POC `DumpStrings` idiom) plus int AtkValues for `HousingGardening`/`ContextIconMenu` (type Int -> `value.Int`). Throttled to one dump per addon per open (hash the addon name + AtkValuesCount + first int).

- [ ] **Step 1: Write the watcher** - a static class with a `Tick()` called from `CensusPump.Tick()` when `Watching` (DEBUG builds only, `#if DEBUG` around the whole class and call site). Dump lines prefix `[PlantRecon]`.

- [ ] **Step 2: Build x64 Debug, commit**

```bash
git add -A && git commit -m "Recon: plant-flow addon watcher (debug builds)"
```

- [ ] **Step 3: BENCH RECON (Sam, his own garden/pot, one manual plant + one manual pot plant)**

Sam turns on the watcher, then by hand: harvests one ripe bed of his own (or uses an empty bed), plants a seed in it (soil + seed via the game UI), and plants/waters one indoor pot. Deliverables, appended to `captures/` as `YYYY-MM-DD-plant-flow.log` and committed:
- The SelectString option text for planting on an empty bed (exact wording).
- `HousingGardening` AtkValues before/after choosing soil and seed; which Callback-visible values identify the two slots and the confirm button.
- `ContextIconMenu` values while the soil/seed picker is open (does selection carry the item id?).
- The `SelectYesno` prompt text on sowing, and the harvest dialogue text (for the harvest receipt parse).
- Pot flow: whether pots use the same addons and whether soil is skipped.

**This capture is the binding authority for Task 10's constants.** If the log contradicts anything Task 10 assumed, Task 10's code follows the log.

### Task 10: CycleChain - pre-flight + harvest->replant interleave

**Files:**
- Modify: `BalambGarden/Chains/PlantFlow.cs` (driver half)
- Create: `BalambGarden/Chains/CycleChain.cs`

**Interfaces:**
- Consumes: `ChainBase`, `CensusPump.OnBedReceipt`, `ObjectSensor`, `DomainTables` (crops, soils), InventoryManager, the Task 9 capture.
- Produces:
  - `ReplantPlan { uint SoilItemId; Dictionary<int /*bedSlot*/, uint /*seedItemId*/> Seeds; bool AnchorTendRound; }` with `static ReplantPlan DefaultFor(EstateKey, int patchOrdinal)` (same-as-harvested from the ledger: each claimed bed's latest species -> `CropBySpeciesIndex(...).SeedId`; beds with no ledger species get no entry and are skipped by the cycle).
  - `CycleChain : ChainBase` with `Run(PatchGroup patch, ReplantPlan plan)`; `PreflightError(PatchGroup, ReplantPlan) -> string?` (null = go).
  - `PlantFlow` driver methods used by CycleChain and PotChain: `bool? SelectPlantOption()`, `bool? DriveGardeningAddon(uint soilId, uint seedId)`, `bool? ConfirmSow()` - tri-state step idiom, constants at the top of the file sourced from the Task 9 capture with the capture line quoted in a comment beside each constant.
- Pre-flight, fail-closed (spec): free bag slots >= beds to harvest; soil count >= beds to replant; per-seed counts >= plan counts; every planned bed claimed and in-reach; patch not half-cycled by a previous abort (all planned beds currently ripe or empty). Any failure -> `LastOutcome = "refused: <reason>"`, no partial launch.
- Chain shape is the interleave invariant: per bed `[harvest bed N -> plant bed N]` before moving on; batch order unexpressible.
- Optional `AnchorTendRound`: after the cycle, a full tend pass over the patch (mints anchored plant + tend receipts).
- Every harvest completion -> `CensusPump.OnBedReceipt(ReceiptVerb.Harvest, header, plant)`; every plant completion -> `OnBedReceipt(ReceiptVerb.Plant, header, seedCropName, stageOverride: 1)` where seedCropName = `CropBySeedId(seedId).Name`.

- [ ] **Step 1: Pre-flight** - `PreflightError` reads `InventoryManager.Instance()`:

```csharp
    var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
    if (inventory == null)
        return "inventory unavailable";
    var toHarvest = ...; // planned beds currently ripe (ledger Latest.Stage == 4)
    var free = inventory->GetEmptySlotsInBag();
    if (free < toHarvest)
        return $"need {toHarvest} free bag slots for yields, have {free}";
    var soil = inventory->GetInventoryItemCount(plan.SoilItemId);
    if (soil < plan.Seeds.Count)
        return $"need {plan.Seeds.Count}x {Plugin.Tables.SoilByItemId(plan.SoilItemId)?.Name ?? "soil"}, have {soil}";
    foreach (var group in plan.Seeds.GroupBy(kv => kv.Value))
    {
        var have = inventory->GetInventoryItemCount(group.Key);
        if (have < group.Count())
            return $"need {group.Count()}x {Plugin.Tables.CropBySeedId(group.Key)?.SeedName ?? $"seed {group.Key}"}, have {have}";
    }
```

plus the claimed/in-reach/half-cycled checks against the ledger and `ObjectSensor`.

- [ ] **Step 2: The chain** - `Run` refuses on `PreflightError`, then per planned bed enqueues: interact -> advance -> select Harvest -> finish dialogue -> receipt; interact again -> select the plant option (`PlantFlow.SelectPlantOption`) -> `DriveGardeningAddon(plan.SoilItemId, seed)` -> `ConfirmSow` -> finish -> receipt; between-bed delay = the tuned PostTend knobs; each bed's first enqueued task is `CheckStop($"bed {n}")` so a user stop lands between beds, never between a harvest and its replant. Empty planned beds (no harvest needed) skip straight to planting. After all beds, the optional anchor tend round re-runs the TendChain step trio per bed. Abort anywhere stops at the bed boundary via `Abort(reason)` - a half-cycled patch is reported, never silently resumed.

- [ ] **Step 3: Build x64 Debug; commit**

```bash
git add -A && git commit -m "Chains: harvest->replant cycle with fail-closed pre-flight and interleave shape"
```

### Task 11: PotChain + bench checkpoint B

**Files:**
- Create: `BalambGarden/Chains/PotChain.cs`
- Modify: `BalambGarden/Windows/MainWindow.cs` (pots section when indoors: nearby pots with Water / Plant / Harvest verbs, reach-gated)
- Modify: `BalambGarden/Plugin.cs` (own the chain instances: `TendChain`, `CycleChain`, `PotChain`; only one may run at a time - a shared `AnyBusy` check in each `BeginRun` call site)

**Interfaces:**
- Consumes: `ChainBase`, `PlantFlow`, `CensusPump.OnPotReceipt`, `ObjectSensor.NearbyPots`.
- Produces: `PotChain : ChainBase` with `Water(PotObject)`, `Harvest(PotObject)`, `Plant(PotObject, uint seedItemId)` - single-pot, no interleave, same menu-driving idiom as TendChain (pot menus per the Task 9 capture); completions route `CensusPump.OnPotReceipt(verb, plantName)`.

- [ ] **Step 1: Implement + wire the UI section.** Plant seed picker: a combo of crossable-false-inclusive crops whose seeds are in inventory (`GetInventoryItemCount > 0`), showing counts.

- [ ] **Step 2: Build x64 Debug; commit**

```bash
git add -A && git commit -m "Chains: pot verbs (water/plant/harvest) on the chain framework"
```

- [ ] **Step 3: BENCH CHECKPOINT B (Sam, his own garden + his own pot - never Chelsea's)**

1. Cycle a ripe patch of Sam's with default plan: pre-flight numbers print honestly; interleave visible in the run log; ledger shows Harvest + Plant receipts, new anchored windows.
2. Deliberately under-stock soil -> launch refuses with the exact shortfall.
3. Abort mid-run -> stops at a bed boundary, reports "stopped at N/M - <reason>".
4. Pot: water Sam's sunflower pot -> claim lands via species-uniqueness bind (key 129 expected).
5. Sam rules save point B: merge to `main`.

---

## Stage 3 - The two windows + ship prep (save point C)

### Task 12: Dashboard - estate roster, rollups, bed grid

**Files:**
- Modify: `BalambGarden/Windows/MainWindow.cs` (full rewrite to the spec shape)

**Interfaces:**
- Consumes: `Rollups.ForEstate`, `StageModel.RipeWindow`, `IWiltSource`, `PipelineReader.Tips`, ledger roster, sensors.
- Produces: the spec's Dashboard, organized by estate roster, current estate pinned first and expanded.

Structure (all code in the task, summarized here by section):

- **Roster ordering**: `Plugin.Garden.Ledger.Estates` sorted current-estate-first (match `EstateSensor.Current()`), then by `LastVisited` desc. Each estate a collapsing header, current one `DefaultOpen`, titled `{DisplayName} - {claimed} claimed{staleness}` where staleness = `" · seen {Ago(LastVisited)}"` for non-current estates.
- **Nickname edit**: small pencil button beside the header toggles an inline `InputText` writing `record.Nickname`, saved on deactivation.
- **Rollup rows** per patch/pot-group (from `Rollups.ForEstate`): `Patch {ordinal+1}: {Claimed}/8 claimed · {Due+Overdue+Danger} thirsty · {Ripe} ripe · ripe ~{window}` - window formatted as local `ddd HH:mm` range with the provenance glyph. Pots group renders as `Pots: N claimed · ...`.
- **Provenance glyphs** (never color alone): Anchored `=` rendered as FontAwesome anchor if trivially available via ECommons `FontAwesome`, else text markers: `[A]` anchored, `[~]` bracketed, `[?]` estimated. Tooltip on hover explains the claim ("anchored: planted under watch at {t}").
- **Expanded bed grid** per patch row (`TreeNode`): table Bed / Plant / Stage / Water / Ripe window; water state text + colored dot both; in-reach beds get a highlight marker (compare against `ObjectSensor.Patches()` when at the estate); rows for beds whose latest map read shows unoccupied render the drift prose instead: `Bed N reads empty now - replanted without me?` with the Abandon button beside it.
- **Staleness on every number**: ages from `bed.Latest.At` shown dimmed beside stage/water.
- **Unclaimed line** on the current-estate header when `ObjectSensor` sees more beds than the ledger claims here: `{n} unclaimed beds here - tend to claim`.

- [ ] Build x64 Debug; commit: `git add -A && git commit -m "Dashboard: estate roster, rollups, bed grid with provenance and drift prose"`

### Task 13: Dashboard - verbs, cycle launch, tips, abandon

**Files:**
- Modify: `BalambGarden/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: the three chains, `ReplantPlan`, `PipelineReader.Tips`.
- Produces: verbs living on the rows they act on (spec), tips panel, relabel-not-modal no-undo presses.

- **Estate header**: `Tend All` (claimed + in-reach beds only - intersect ledger with `ObjectSensor.Patches()`), disabled with reason tooltip when nothing is in reach or a chain is busy.
- **Patch row**: `Water Patch` + `Cycle...` - Cycle opens an inline panel (not a modal): per-bed seed combo (default from `ReplantPlan.DefaultFor`), soil combo (soils in inventory with counts), `Anchor tend round` checkbox (the gold-standard pass), a live pre-flight line (`PreflightError` re-checked each frame, shown in red while non-null), and the launch button using relabel-not-modal: first click relabels to `Run cycle: {n} beds - sure?`, second click launches (any other click elsewhere resets).
- **Bed row**: single Tend; pot rows: Water / Harvest / Plant (seed combo inline).
- **Abandon** on bed rows and drift lines: relabel-not-modal (`Abandon` -> `Abandon - sure?`), then `Census.Abandon(bed); Garden.Save();`.
- **Tips panel** at the bottom: collapsing header `Tips ({n})`, hidden entirely when `PipelineReader.Tips(...)` is empty (silence over filler); each tip prefixed by kind tag `[stock]` / `[bottleneck]` / `[anomaly]`.
- **Busy coordination**: all verb buttons disabled while any chain is busy (`Plugin.AnyChainBusy` helper).

- [ ] Build x64 Debug; commit: `git add -A && git commit -m "Dashboard: verbs on their rows, cycle launch with live pre-flight, tips panel"`

### Task 14: Run Log + Config polish

**Files:**
- Modify: `BalambGarden/Windows/RunLogWindow.cs`
- Modify: `BalambGarden/Windows/ConfigWindow.cs`

- **RunLogWindow**: track the active chain generically - `plugin.ActiveChain : ChainBase?` (the busy one, else the last one that ran). Keep sticky scroll + countdown. The idle line shows `LastOutcome` which now carries the clean-stop report (`stopped at N/M - reason`) from `ChainBase.Abort`. Abort button calls `chain.RequestStop()` (honored at the next bed boundary - never mid-dialogue; button relabels to "Stopping..." while the request is pending).
- **ConfigWindow**: add checkboxes for `NudgeEnabled` ("Arrival nudge - one chat line when a garden needs you"), `TrailEnabled` ("Debug trail - append receipts to trail.jsonl"), `ClaimOnAction` mirror; keep the pacing sliders. Sync `Plugin.Garden.Census.ClaimOnAction` on change.

- [ ] Build x64 Debug; commit: `git add -A && git commit -m "Run log clean-stop reporting; config toggles for nudge, trail, claim"`

### Task 15: Probe port (#if DEBUG) + SpeciesIndex reconciliation

**Files:**
- Create: `BalambGarden/ReconProbe.cs` (from `probe` branch)
- Modify: `Data/SpeciesIndex.json`, `tools/build-domain-data.mjs`, `tools/source/*` (from `probe` branch)
- Modify: `BalambGarden/Windows/MainWindow.cs` (probe buttons in the recon section, `#if DEBUG`)

The `probe` branch carries the instrument and the extended species work that never landed on `rebuild`:

- [ ] **Step 1**: `git checkout probe -- BalambGarden/ReconProbe.cs tools/build-domain-data.mjs tools/source/lotlab_seeds.json tools/source/lotlab_times.json captures/2026-08-13-chelsea-fc-probe.log`
- [ ] **Step 2**: Diff `probe:Data/SpeciesIndex.json` against `rebuild`'s copy. Take the superset (probe's has the 100-107 tail named via xivapi, 08-13). Fold in the 08-13 follow-ups: names for 100/102/103/107 must be present; add id 108 as unknown-but-listed if absent. Run the Engine tests - `SpeciesIndexByName` and species tests must still pass.
- [ ] **Step 3**: Wrap `ReconProbe.cs` entirely in `#if DEBUG` / `#endif`. Rewrite its map-reading half to go through `MapSensor.ReadRawEntries` semantics where possible (instrument and app must not disagree about what was seen - spec); keep the raw hex dump as-is. Delete its `SpeciesTable` dependency (`SpeciesTable.g.cs` is superseded by Engine `DomainTables`): decoded lines use `Plugin.Tables.SpeciesName`.
- [ ] **Step 4**: Recon section of MainWindow (`#if DEBUG`): buttons `Log housing`, `Dump records`, `Dump bed structs`, `Watch plant flow` calling the probe + PlantFlow watcher. Release builds compile all of it out - verify with `dotnet build -c Release -p:Platform=x64` (zero probe symbols; a `#if !DEBUG` compile is the check).
- [ ] **Step 5**: Build both configs x64 + run tests; commit: `git add -A && git commit -m "Probe instrument ported behind DEBUG; species index reconciled with probe branch"`

### Task 16: v0.2.0.0 + final bench

**Files:**
- Modify: `BalambGarden/BalambGarden.csproj` (`<Version>0.2.0.0</Version>`)
- Modify: `README.md` (v0.2 blurb: what the suite does, fresh-ledger note)

- [ ] **Step 1**: Version bump + README; build Release x64 clean; all Engine tests green.
- [ ] **Step 2**: Commit: `git add -A && git commit -m "v0.2.0.0: the rebuild ships - suite of tools on the five-layer engine"`
- [ ] **Step 3: FINAL BENCH (Sam)** - the acceptance walk, at the household estates in production use (read-only surfaces plus Sam's own garden for verbs):
  1. Dashboard shows the estate roster with Chelsea's, FC, Sam's; rollup lines read like the spec example.
  2. The Onion pipeline appears in Tips (stock + bottleneck lines) once the claimed plantings cover it - user story #1.
  3. Windows and provenance glyphs behave; nudge fires once per arrival; run log reports clean stops.
  4. Sam rules save point C: merge to `main`, tag, DalamudPluginRepo update, Chelsea installs v0.2.0.0 over the POC (fresh ledger; one tend run per estate re-censuses).

---

## Deferred (unchanged from spec - not in this plan)

Wilt memory sensor (lab running), indoor byte-3 pigment binding, flowerpot-only flower names, indoor map oddities (census already shows unknown), Phase 2 planner (doorway = the replant-plan editor shipped in Task 13). Deferred minors from Plan A stand: nudge label hardcoded, `Tips(now)` unused parameter, stage-4 provenance display rule.

## Self-review notes

- Spec coverage: sensors (T5), census join lifecycle + claim-on-action (T2/T6), ledger approach C persistence (T3/T4), derivations consumed (T7/T12), tips (T13), chains framework + Tend All + cycle + pots (T7/T10/T11), two windows (T12-T14), failure philosophy (pre-flight T10, clean stop T7, unreadable surface T5/T7, mark-at-confirmation via receipt-after-completion routing T6), probe survival (T15), v0.2.0.0 in-place ship w/ fresh ledger (T4/T16), captures-as-fixtures (T1/T2 tests reuse capture-derived values; plant-flow capture T9).
- The one deliberately recon-gated area: PlantFlow constants (T9 -> T10). The capture is the authority; code follows the log.
- Type consistency spot-checks: `ReceiptVerb` enum reused from Engine; `BedObject`/`PatchGroup`/`PotObject` defined once in T5 and consumed in T7/T10/T11/T12; `OnBedReceipt` signature identical at every call site; `ReplantPlan` defined in T10, consumed in T13.
