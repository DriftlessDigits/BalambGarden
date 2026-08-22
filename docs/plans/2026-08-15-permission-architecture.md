# Permission Architecture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the per-bed claim ceremony with game-authored permission: the teleport-list roster gates what Balamb attempts and tracks, the composed menu enforces per-verb permission at act time, and ledger rows become pure census records created by sightings and receipts.

**Architecture:** A new Engine `AccessRoster` (pure, tested) converts HouseId fields to `EstateKey`s and answers coverage; a new plugin `RosterSensor` reads raw `Telepo` (the probe-proven route) plus `GetOwnedHouseId` corroboration into that roster. `CensusEngine.OnMapSighting` learns to CREATE bed rows (bound outdoor keys; all indoor pots) so a visit to a rostered estate populates the ledger without ceremony. `CensusPump` gates all ledger writes on roster coverage. Claim-on-action config/engine flag dies. UI: tabs = rostered estates + current standing; unrostered-current renders display-only.

**Tech Stack:** C# / .NET 9, Dalamud plugin (FFXIVClientStructs), xUnit for `BalambGarden.Engine.Tests`.

**Spec:** vault `Book Of Holding/Planet Express/Deliveries/Balamb Garden/Balamb Garden - Permission Architecture.md` (mirrored rulings in `.superpowers/sdd/2026-08-14-rebuild-plan-b/progress.md`). Ground-truth captures: `captures/2026-08-15-roster-recon.log` (HouseId equality, roster shape), `captures/2026-08-13-gardener-fc-probe.log`.

## Global Constraints

- **NEVER build or run the plugin project (`BalambGarden/BalambGarden.csproj`).** Any build of it hot-loads into Drift's RUNNING GAME. Implementers verify plugin-side files by careful reading only; Fable builds at checkpoint on Drift's explicit go. Engine work is verified with `dotnet test BalambGarden.Engine.Tests -c Debug` — that project is safe to build and MUST be green before every commit.
- Verify green THEN commit, as separate steps. Check the exit code, never pipe build output through anything that masks it.
- No AI co-authorship lines in commits. Work on branch `rebuild`. One commit per completed task.
- V1 ruling (Drift, 2026-08-15): rostered estate ⇒ assume all verbs actionable. NO grant-observation machinery. Permission refusals surface at act time from the composed menu, worded as permission answers, not errors.
- Fail closed everywhere: anything that fits no receipted shape is refused loudly with the raw value in the log.
- Comment style: constraints and receipts (dates), never narration of the next line. Match the existing files.
- Drift's live ledger is sacred. No task rewrites `ledger-v2.json` on disk; the one serialization change (Task 2) keeps the JSON field name via attribute so the file is byte-stable.

## File Structure

- Create: `BalambGarden.Engine/Census/AccessRoster.cs` — pure roster model + HouseId-parts→EstateKey conversion + coverage.
- Create: `BalambGarden.Engine.Tests/Census/AccessRosterTests.cs`
- Modify: `BalambGarden.Engine/Census/CensusEngine.cs` — sighting-creates-rows, reverse key lookup, ClaimOnAction removed.
- Modify: `BalambGarden.Engine/Ledger/ClaimedBed.cs` — `ClaimedAt` → `FirstRecorded` (JSON name preserved).
- Modify: `BalambGarden.Engine/Ledger/LedgerMigration.cs`, `BalambGarden.Engine.Tests/**` — rename fallout, test updates.
- Create: `BalambGarden/Game/RosterSensor.cs` — raw Telepo + owned reads → cached `AccessRoster`.
- Modify: `BalambGarden/Game/CensusPump.cs` — roster scope gate on every write path; claim strings die.
- Modify: `BalambGarden/Chains/TendChain.cs`, `BalambGarden/Chains/CycleChain.cs` — permission language, preflight reword.
- Modify: `BalambGarden/Windows/MainWindow.cs` — roster tabs, unrostered banner, untracked reword, verb gating.
- Modify: `BalambGarden/Windows/ConfigWindow.cs`, `BalambGarden/Configuration.cs`, `BalambGarden/GardenService.cs` — ClaimOnAction removal.

---

### Task 1: Engine AccessRoster (pure model + conversion + coverage)

**Files:**
- Create: `BalambGarden.Engine/Census/AccessRoster.cs`
- Test: `BalambGarden.Engine.Tests/Census/AccessRosterTests.cs`

**Interfaces:**
- Consumes: `EstateKey` (`BalambGarden.Engine/Census/EstateKey.cs`) — `new EstateKey(ushort TerritoryId, int Ward, int Plot, int Room = -1)`, `EstateKey.Apartment(ushort buildingTerritory, int ward, int division, int room)`.
- Produces: `public sealed record RosterEstate(EstateKey Key, string Kind)`; `public sealed class AccessRoster` with `AccessRoster(IReadOnlyList<RosterEstate> estates)`, `IReadOnlyList<RosterEstate> Estates`, `static readonly AccessRoster Empty`, `static EstateKey? FromHouseParts(ushort territory, int ward, int plot, int room, bool isApartment, int division)`, `bool Covers(EstateKey estate)`. Tasks 3–6 rely on these exact names.

- [ ] **Step 1: Write the failing tests** (values are the 2026-08-15 roster-recon receipts)

```csharp
using BalambGarden.Engine.Census;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

public class AccessRosterTests
{
    // Receipts, captures/2026-08-15-roster-recon.log: roster HouseIds decode to RAW
    // ward/plot (Gardener ward=11 plot=32; FC ward=11 plot=57; Papa's t641 w3 p51;
    // apartment t979 w7 room=29 div=0).
    private static AccessRoster SamsRoster() => new([
        new RosterEstate(new EstateKey(340, 11, 32), "SharedEstate"),
        new RosterEstate(new EstateKey(340, 11, 57), "FreeCompanyEstate"),
        new RosterEstate(new EstateKey(641, 3, 51), "PersonalEstate"),
        new RosterEstate(EstateKey.Apartment(979, 7, 0, 29), "ApartmentRoom"),
    ]);

    [Fact]
    public void HousePartsWithRoomZeroKeyAsTheHouse()
        => Assert.Equal(new EstateKey(340, 11, 32),
            AccessRoster.FromHouseParts(340, 11, 32, room: 0, isApartment: false, division: 0));

    [Fact]
    public void HousePartsWithARealRoomKeyAsThatRoom()
        => Assert.Equal(new EstateKey(340, 11, 57, 7),
            AccessRoster.FromHouseParts(340, 11, 57, room: 7, isApartment: false, division: 0));

    [Fact]
    public void ApartmentPartsUseTheApartmentShape()
        => Assert.Equal(EstateKey.Apartment(979, 7, 0, 29),
            AccessRoster.FromHouseParts(979, 7, 0, room: 29, isApartment: true, division: 0));

    [Theory]
    [InlineData(0, 11, 32, 0, false)]   // no territory
    [InlineData(340, -1, 32, 0, false)] // no ward
    [InlineData(340, 11, -5, 0, false)] // negative plot on a non-apartment
    [InlineData(979, 7, 0, 0, true)]    // apartment with no room number
    public void UnreceiptedShapesConvertToNull(
        ushort territory, int ward, int plot, int room, bool isApartment)
        => Assert.Null(AccessRoster.FromHouseParts(territory, ward, plot, room, isApartment, 0));

    [Fact]
    public void CoversARosteredHouseExactly()
        => Assert.True(SamsRoster().Covers(new EstateKey(340, 11, 32)));

    [Fact]
    public void AHouseRowCoversItsRooms()   // FC chambers room 7 rode the FC row (recon: no chambers row exists)
        => Assert.True(SamsRoster().Covers(new EstateKey(340, 11, 57, 7)));

    [Fact]
    public void CoversTheApartmentRoom()
        => Assert.True(SamsRoster().Covers(EstateKey.Apartment(979, 7, 0, 29)));

    [Fact]
    public void AnApartmentRowCoversOnlyItsOwnRoom()
        => Assert.False(SamsRoster().Covers(EstateKey.Apartment(979, 7, 0, 30)));

    [Fact]
    public void DoesNotCoverARandosPlot()   // recon negative: raw W3 P50 Shirogane matched nothing
        => Assert.False(SamsRoster().Covers(new EstateKey(641, 3, 50)));

    [Fact]
    public void EmptyRosterCoversNothing()
        => Assert.False(AccessRoster.Empty.Covers(new EstateKey(340, 11, 32)));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test BalambGarden.Engine.Tests -c Debug --filter AccessRosterTests`
Expected: FAIL — `AccessRoster` does not exist (compile error).

- [ ] **Step 3: Implement**

```csharp
namespace BalambGarden.Engine.Census;

/// <summary>One estate the game itself lists for this player - a teleport-list row or an
/// owned-estate read - converted to our key. Kind is the game's EstateType name, kept for
/// labels only.</summary>
public sealed record RosterEstate(EstateKey Key, string Kind);

/// <summary>The access roster (spec: Permission Architecture, 2026-08-15). Presence here is
/// the game saying "you can act at this estate" (Drift's v1 ruling: rostered = assume
/// actionable; the composed menu refuses per-verb weirdness gracefully at act time).
/// Coverage is also the census scope: an estate not covered is not tracked at all.</summary>
public sealed class AccessRoster(IReadOnlyList<RosterEstate> estates)
{
    public IReadOnlyList<RosterEstate> Estates { get; } = estates;

    public static readonly AccessRoster Empty = new([]);

    /// <summary>From a HouseId's own decoded fields. Receipt (roster recon 2026-08-15):
    /// the embedded HouseId carries RAW ward/plot - Gardener's row read ward=11 plot=32,
    /// already the ledger convention - and room 0 is the house itself. Null for anything
    /// that fits no receipted shape: a row we cannot name is dropped loudly by the caller,
    /// never misfiled quietly here.</summary>
    public static EstateKey? FromHouseParts(
        ushort territory, int ward, int plot, int room, bool isApartment, int division)
    {
        if (territory == 0 || ward < 0)
            return null;
        if (isApartment)
            return room > 0 ? EstateKey.Apartment(territory, ward, division, room) : null;
        if (plot < 0)
            return null;
        return room > 0
            ? new EstateKey(territory, ward, plot, room)
            : new EstateKey(territory, ward, plot);
    }

    /// <summary>Whether the game-granted set contains this estate. A house row covers its
    /// whole plot, rooms included (recon receipt: chambers have no row of their own - the
    /// FC row is their parent). An apartment row covers exactly its own room - the
    /// building's other doors are other people's homes.</summary>
    public bool Covers(EstateKey estate)
    {
        foreach (var entry in Estates)
        {
            if (entry.Key == estate)
                return true;
            if (!entry.Key.IsApartment && entry.Key.Room < 0
                && entry.Key.TerritoryId == estate.TerritoryId
                && entry.Key.Ward == estate.Ward
                && entry.Key.Plot == estate.Plot)
                return true;
        }
        return false;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test BalambGarden.Engine.Tests -c Debug`
Expected: PASS, full suite green (162 existing + 10 new).

- [ ] **Step 5: Commit**

```bash
git add BalambGarden.Engine/Census/AccessRoster.cs BalambGarden.Engine.Tests/Census/AccessRosterTests.cs
git commit -m "Engine: the access roster - game-granted estates from HouseId parts, coverage includes a house's rooms"
```

---

### Task 2: CensusEngine — sightings create rows; ClaimOnAction dies; FirstRecorded rename

**Files:**
- Modify: `BalambGarden.Engine/Census/CensusEngine.cs`
- Modify: `BalambGarden.Engine/Ledger/ClaimedBed.cs`
- Modify: `BalambGarden.Engine/Ledger/LedgerMigration.cs` (rename fallout only: `ClaimedAt` refs at lines 187, 201)
- Modify: `BalambGarden.Engine.Tests/Census/CensusEngineTests.cs` (drop the two ClaimOnAction-off tests, add the new ones below)
- Modify (rename fallout, `ClaimedAt` → `FirstRecorded` at the listed lines): `BalambGarden.Engine.Tests/Ledger/LedgerTests.cs:15`, `Ledger/EstateRosterTests.cs:44`, `Ledger/EstateNormalizationTests.cs:23,110,120`, `Census/EstateShapeTests.cs:70`. NOTE: the raw-JSON fixture at `EstateNormalizationTests.cs:244` keeps the literal string `"ClaimedAt"` — with the `JsonPropertyName` attribute it must still pass, and that test is now the wire-stability receipt for this rename. `ClaimedAt` appears NOWHERE in the plugin project (verified 2026-08-15) — Engine tests fully guard this change.

**Interfaces:**
- Produces: `CensusEngine.OnMapSighting(EstateKey estate, int mapKey, IReadOnlyList<BedReading> beds, DateTimeOffset at, bool isPot = false, bool mayRecord = false)` — same return (count of observations landed). `CensusEngine.OrdinalOfKey(EstateKey estate, int mapKey, bool isPot = false) : int?`. `ClaimedBed.FirstRecorded` (replaces `ClaimedAt`; JSON field name stays `ClaimedAt`). `CensusEngine.ClaimOnAction` and the `OnReceipt` gate on it are REMOVED.
- Consumes: nothing new.

- [ ] **Step 1: Write the failing tests** (append to `CensusEngineTests.cs`; delete `ClaimOnActionOffDoesNotClaim` and the second test that sets `ClaimOnAction = false` at lines ~33–48)

```csharp
[Fact]
public void SightingCreatesRowsForABoundOutdoorKey()
{
    var store = new LedgerStore();
    var engine = new CensusEngine(store);
    var estate = new EstateKey(340, 11, 32);
    engine.Bind(estate, patchOrdinal: 1, mapKey: 116);

    var landed = engine.OnMapSighting(estate, 116,
        [new BedReading(0, 0x41, 4, 0, true), new BedReading(1, 0x11, 4, 0, true)],
        DateTimeOffset.UtcNow, mayRecord: true);

    Assert.Equal(2, landed);
    Assert.Equal(2, store.Beds.Count);
    Assert.All(store.Beds, b => Assert.Equal(1, b.PatchOrdinal));
    Assert.Equal(4, store.Beds[0].Latest!.Stage);
}

[Fact]
public void SightingNeverCreatesRowsForAnUnboundKey()   // ward-visible neighbor data stays ephemeral
{
    var store = new LedgerStore();
    var engine = new CensusEngine(store);
    var landed = engine.OnMapSighting(new EstateKey(340, 11, 32), 62,
        [new BedReading(0, 0x41, 4, 0, true)], DateTimeOffset.UtcNow, mayRecord: true);
    Assert.Equal(0, landed);
    Assert.Empty(store.Beds);
}

[Fact]
public void SightingWithoutRecordRightsOnlyUpdatesExistingRows()
{
    var store = new LedgerStore();
    var engine = new CensusEngine(store);
    var estate = new EstateKey(340, 11, 32);
    engine.Bind(estate, 0, 110);
    engine.OnMapSighting(estate, 110,
        [new BedReading(0, 0x41, 2, 0, true)], DateTimeOffset.UtcNow, mayRecord: false);
    Assert.Empty(store.Beds);
}

[Fact]
public void PotSightingBindsAndCreatesItsOwnRow()   // indoor map is house-scoped (08-13); idx==key (08-15)
{
    var store = new LedgerStore();
    var engine = new CensusEngine(store);
    var estate = EstateKey.Apartment(979, 7, 0, 29);
    var landed = engine.OnMapSighting(estate, 0,
        [new BedReading(0, 44, 2, 0, true)], DateTimeOffset.UtcNow, isPot: true, mayRecord: true);
    Assert.Equal(1, landed);
    Assert.Equal(0, engine.BoundKey(estate, 0, isPot: true));
    var bed = Assert.Single(store.Beds);
    Assert.True(bed.IsPot);
}

[Fact]
public void EmptyBedsCreateNoRows()   // a bare bed has nothing to record
{
    var store = new LedgerStore();
    var engine = new CensusEngine(store);
    var estate = new EstateKey(340, 11, 32);
    engine.Bind(estate, 0, 110);
    engine.OnMapSighting(estate, 110,
        [new BedReading(0, 0, 0, 0, false)], DateTimeOffset.UtcNow, mayRecord: true);
    Assert.Empty(store.Beds);
}

[Fact]
public void ReceiptAlwaysCreatesTheRow()   // the ClaimOnAction=false path is gone
{
    var store = new LedgerStore();
    var engine = new CensusEngine(store);
    var estate = new EstateKey(340, 11, 32);
    engine.Bind(estate, 0, 110);
    var bed = engine.OnReceipt(new ReceiptEvent(estate, 0, 0, ReceiptVerb.Tend, 0x41, 2, DateTimeOffset.UtcNow));
    Assert.NotNull(bed);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test BalambGarden.Engine.Tests -c Debug --filter CensusEngineTests`
Expected: FAIL — no `mayRecord` parameter, `ClaimOnAction` deletions break the two old tests (which is why they are removed in this same task).

- [ ] **Step 3: Implement**

In `ClaimedBed.cs`, rename the property, keep the wire name (Drift's live ledger stays byte-stable):

```csharp
[System.Text.Json.Serialization.JsonPropertyName("ClaimedAt")]
public DateTimeOffset FirstRecorded { get; init; }
```

In `CensusEngine.cs`, delete `ClaimOnAction` and the `if (!ClaimOnAction) return null;` gate in `OnReceipt` (the row constructor's `ClaimedAt =` becomes `FirstRecorded =`). Add the reverse lookup and rewrite `OnMapSighting`:

```csharp
/// <summary>Which ordinal a bound map key belongs to at this estate - the bindings are the
/// receipts, this only reads them backward. Null = not ours (ward-visible neighbor data).</summary>
public int? OrdinalOfKey(EstateKey estate, int mapKey, bool isPot = false)
{
    for (var ordinal = 0; ordinal < 16; ordinal++)
        if (BoundKey(estate, ordinal, isPot) == mapKey)
            return ordinal;
    return null;
}

/// <summary>Map sightings are census records now (spec 2026-08-15: no ceremony gates
/// tracking). With mayRecord - the caller vouching the estate is roster-covered - a
/// sighting CREATES rows: any occupied bed of a receipt-bound outdoor key, and any
/// occupied pot (the indoor map is house-scoped, 08-13, and furniture idx == key, 08-15,
/// so a pot sighting carries its own identity and binds on sight). An unbound outdoor
/// key stays ephemeral regardless - that is the neighbors' garden passing by.</summary>
public int OnMapSighting(
    EstateKey estate, int mapKey, IReadOnlyList<Sensing.BedReading> beds, DateTimeOffset at,
    bool isPot = false, bool mayRecord = false)
{
    if (mayRecord && isPot && BoundKey(estate, mapKey, isPot: true) is null)
        Bind(estate, mapKey, mapKey, isPot: true);

    var ordinal = isPot ? mapKey : OrdinalOfKey(estate, mapKey);

    var count = 0;
    foreach (var reading in beds)
    {
        if (!reading.Occupied)
            continue;
        var bed = ledger.Beds.FirstOrDefault(b =>
            b.Estate == estate && b.IsPot == isPot
            && b.MapKey == mapKey && b.BedSlot == reading.Slot);
        if (bed is null)
        {
            if (!mayRecord || ordinal is null)
                continue;
            bed = new ClaimedBed
            {
                Estate = estate, MapKey = mapKey, PatchOrdinal = ordinal.Value,
                BedSlot = reading.Slot, IsPot = isPot, FirstRecorded = at,
            };
            ledger.Beds.Add(bed);
        }
        bed.Observe(new Observation(at, reading.SpeciesIndex, reading.Stage, ObservationSource.MapSighting));
        count++;
    }
    return count;
}
```

Fix the `ClaimedAt` references in `LedgerMigration.cs` (`Rekey`, `Combine`) and any tests to `FirstRecorded`. Note the doc comment on the class: "claim-on-action" language in the header becomes "receipts and sightings are the only write paths".

- [ ] **Step 4: Run full suite**

Run: `dotnet test BalambGarden.Engine.Tests -c Debug`
Expected: PASS, everything green.

- [ ] **Step 5: Commit**

```bash
git add BalambGarden.Engine BalambGarden.Engine.Tests
git commit -m "Engine: sightings create census rows on covered estates; the claim ceremony dies; ClaimedAt becomes FirstRecorded (wire name kept)"
```

---

### Task 3: RosterSensor (plugin — code only, NO BUILD)

**Files:**
- Create: `BalambGarden/Game/RosterSensor.cs`

**Interfaces:**
- Consumes: `AccessRoster`, `RosterEstate`, `AccessRoster.FromHouseParts` (Task 1). Raw `FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo` (fields verified by reflection AND live capture 2026-08-15: `Instance()`, `UpdateAetheryteList()` null-on-refusal, `TeleportList` of `TeleportInfo { AetheryteId, TerritoryId, EstateType, HouseId, Ward, Plot, SubIndex }`), `FFXIVClientStructs.FFXIV.Client.Game.HousingManager.GetOwnedHouseId(EstateType, int)`, `FFXIVClientStructs.FFXIV.Client.Game.EstateType`, `ECommons.GameHelpers.Player`.
- Produces: `internal static AccessRoster RosterSensor.Current()` — cached, never throws, never null (falls back to last good roster, `AccessRoster.Empty` before the first read). Tasks 4–6 call exactly this.

- [ ] **Step 1: Write the file**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using BalambGarden.Engine.Census;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BalambGarden.Game;

/// <summary>
/// The access roster, read from the game (spec: Permission Architecture, 2026-08-15;
/// receipts: captures/2026-08-15-roster-recon.log). Two corroborating sources, both
/// HouseId-shaped: the teleport list (raw Telepo - the SAME route the recon probe proved,
/// one instrument, one app, one truth) and HousingManager's owned-estate answers (which
/// also carry the chambers no teleport row names). The union is the set of estates the
/// game says Drift can reach; per Drift's v1 ruling that set is assumed actionable, and the
/// composed menu handles any per-verb weirdness gracefully at act time.
///
/// <para>Fail-open on staleness, fail-closed on shape: a refused refresh keeps the last
/// good roster (an estate does not stop being Drift's because a read misfired), but a row
/// that fits no receipted shape is dropped with its raw HouseId in the log.</para>
/// </summary>
internal static unsafe class RosterSensor
{
    private static AccessRoster cached = AccessRoster.Empty;
    private static DateTime nextRefreshUtc = DateTime.MinValue;
    private static bool warnedNoRead;

    internal static AccessRoster Current()
    {
        if (DateTime.UtcNow < nextRefreshUtc)
            return cached;
        nextRefreshUtc = DateTime.UtcNow.AddSeconds(60);

        try
        {
            if (Read() is { } fresh)
            {
                cached = fresh;
                warnedNoRead = false;
            }
            else if (!warnedNoRead)
            {
                warnedNoRead = true;
                Plugin.Log.Warning("[Roster] refresh refused (no player or game said no) - "
                    + $"holding the last good roster ({cached.Estates.Count} estate(s))");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Roster] read failed: {ex.Message} - holding the last good roster");
        }

        return cached;
    }

    private static AccessRoster? Read()
    {
        if (!Player.Available)
            return null;

        var telepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance();
        if (telepo == null || telepo->UpdateAetheryteList() == null)
            return null;

        var entries = new List<RosterEstate>();
        foreach (var row in telepo->TeleportList)
        {
            // 255 = a plain aetheryte; all-Fs = the game's own "none" (recon receipt).
            if ((int)row.EstateType == 255 || row.HouseId.Id == ulong.MaxValue)
                continue;
            Add(entries, row.HouseId, row.EstateType.ToString());
        }

        // The owned-estate reads: corroboration, plus the chambers the list has no row for.
        foreach (var (type, index) in new[]
        {
            (EstateType.FreeCompanyEstate, 0), (EstateType.PersonalChambers, 0),
            (EstateType.PersonalEstate, 0), (EstateType.SharedEstate, 0),
            (EstateType.SharedEstate, 1), (EstateType.ApartmentRoom, 0),
        })
        {
            var id = HousingManager.GetOwnedHouseId(type, index);
            if (id.Id == ulong.MaxValue)
                continue;
            Add(entries, id, type.ToString());
        }

        return new AccessRoster(entries);
    }

    private static void Add(List<RosterEstate> entries, HouseId houseId, string kind)
    {
        var key = AccessRoster.FromHouseParts(
            (ushort)houseId.TerritoryTypeId, houseId.WardIndex, houseId.PlotIndex,
            houseId.RoomNumber, houseId.IsApartment, houseId.ApartmentDivision);
        if (key is null)
        {
            Plugin.Log.Warning(
                $"[Roster] {kind} row fits no receipted shape - houseId=0x{houseId.Id:X16} - dropped");
            return;
        }
        if (entries.All(e => e.Key != key))
            entries.Add(new RosterEstate(key, kind));
    }
}
```

- [ ] **Step 2: Re-read the file against the Consumes list above** — every FFXIVClientStructs member named there is capture-verified; anything you were tempted to use beyond them is not. Do NOT build.

- [ ] **Step 3: Commit**

```bash
git add BalambGarden/Game/RosterSensor.cs
git commit -m "Roster sensor: game-granted estates from the teleport list + owned reads, cached, fail-open on staleness"
```

---

### Task 4: CensusPump — the roster is the census scope

**Files:**
- Modify: `BalambGarden/Game/CensusPump.cs`
- Modify: `BalambGarden/GardenService.cs:24` (drop `{ ClaimOnAction = ... }` initializer — property is gone)
- Modify: `BalambGarden/Configuration.cs:43` (delete `ClaimOnAction`)

**Interfaces:**
- Consumes: `RosterSensor.Current()` (Task 3), `AccessRoster.Covers` (Task 1), `OnMapSighting(..., mayRecord:)` (Task 2).
- Produces: `internal static bool CensusPump.CoveredHere` — true when the current estate is roster-covered; MainWindow (Task 6) reads it. The uncovered refusal string used by every write path: `"not on your teleport list - Balamb doesn't track here"`.

- [ ] **Step 1: Add the scope gate**

At the top of the class:

```csharp
/// <summary>Whether the estate under our feet is game-granted (spec: the roster is the
/// census scope). Refreshed with the tick; false anywhere unrostered, and every ledger
/// write path below checks it - Balamb can SEE a stranger's garden, it does not KEEP it.</summary>
internal static bool CoveredHere { get; private set; }

private const string NotCovered = "not on your teleport list - Balamb doesn't track here";
```

In `Tick()`, right after `estate` is resolved (line ~57):

```csharp
CoveredHere = RosterSensor.Current().Covers(estate);
```

and gate the visit write (the `UpsertEstate`/`Save`/nudge block, lines ~94–104) with `if (CoveredHere)` — an uncovered estate still announces (`announcedEstate = estate`) so the tick settles, but writes nothing and nudges nothing.

- [ ] **Step 2: Gate the sighting deliveries**

In `SightNow()` both `OnMapSighting` calls gain the flag (this is what populates Gardener's 24 beds on arrival):

```csharp
Plugin.Garden.Census.OnMapSighting(estate, key, ..., isPot: true, mayRecord: CoveredHere);
// and the outdoor call:
Plugin.Garden.Census.OnMapSighting(estate, key, beds, now, mayRecord: CoveredHere);
```

After the loop in each branch, when `CoveredHere`, follow with `Plugin.Garden.Save();` only if any call returned > 0 (sightings now create rows; a visit that learned something persists it).

- [ ] **Step 3: Gate the receipt paths and retire the claim strings**

At the top of `OnBedReceipt`, `OnPotReceipt`, and `OnRipeSkip`, after the estate null-check: `if (!CoveredHere) return NotCovered;`

In `OnRipeSkip`: insert `SightNow();` before the bed lookup (the sighting may have just created the row), and the not-found fallback line becomes:

```csharp
return $"{bedHeader}: ripe - patch not identified yet (tend a growing bed here once and the whole patch joins)";
```

In `Deliver` (line ~336) the claim branch dies:

```csharp
return bed is null
    ? $"{label} - done (not recorded: patch not identified yet)"
    : $"{label} - done";
```

- [ ] **Step 4: Remove `ClaimOnAction` config plumbing** — `Configuration.cs:43` property deleted; `GardenService.cs:24` becomes `Census = new CensusEngine(ledger);`.

- [ ] **Step 5: Re-read the diff. Do NOT build.**

- [ ] **Step 6: Commit**

```bash
git add BalambGarden/Game/CensusPump.cs BalambGarden/GardenService.cs BalambGarden/Configuration.cs
git commit -m "Census scope is the roster: uncovered estates read as display only, sightings populate covered ones, claim strings retired"
```

---

### Task 5: Chains — permission language + preflight reword

**Files:**
- Modify: `BalambGarden/Chains/TendChain.cs:214`
- Modify: `BalambGarden/Chains/CycleChain.cs:130,139–142`

**Interfaces:**
- Consumes: nothing new (CensusPump's gates from Task 4 already protect the write side).
- Produces: user-facing strings only.

- [ ] **Step 1: TendChain** — the no-tend skip line (`TendChain.cs:214`) becomes a permission answer, not a shrug:

```csharp
: $"{header}: skipped (the menu offered no tend - empty bed, or not permitted here)");
```

- [ ] **Step 2: CycleChain preflight** — rows are sighting-created now, so the two claim-worded refusals reword. Line 130:

```csharp
return "nothing planned: no recorded species here to replant - stand by the patch a moment, or tend it once";
```

Lines 139–142 (`claimed is null` — variable renames to `recorded`):

```csharp
var recorded = Plugin.Garden.Census.LedgerBeds.FirstOrDefault(b =>
    b.Estate == estate && b.PatchOrdinal == patch.Ordinal && b.BedSlot == slot && !b.IsPot);
if (recorded is null)
    return $"bed {slot + 1} has no census record yet - stand by the patch a moment";
```

- [ ] **Step 3: Sweep both files for remaining "claim" wording in strings and comments** — comments update to census/roster language where they describe behavior this plan changed (e.g. TendChain's class doc "binds a patch or claims a bed" → "binds a patch and records the bed"). Behavior untouched.

- [ ] **Step 4: Re-read the diff. Do NOT build.**

- [ ] **Step 5: Commit**

```bash
git add BalambGarden/Chains/TendChain.cs BalambGarden/Chains/CycleChain.cs
git commit -m "Chains speak permission language: menu refusals and census-record preflights, claim vocabulary retired"
```

---

### Task 6: MainWindow + ConfigWindow — roster tabs, unrostered banner, reworded strings

**Files:**
- Modify: `BalambGarden/Windows/MainWindow.cs` (tab loop ~133–155, `UntrackedTag` :46, empty-estate line ~344, unclaimed lines ~396–404)
- Modify: `BalambGarden/Windows/ConfigWindow.cs:139–144` (Claim-as-I-go checkbox deleted)

**Interfaces:**
- Consumes: `RosterSensor.Current()`, `AccessRoster.Covers`, `CensusPump.CoveredHere`, `EstateRecord { Key, FirstSeen, LastVisited }`.
- Produces: UI only.

- [ ] **Step 1: Tabs = rostered estates + current standing.** Replace the tab-loop filter (lines 143–147):

```csharp
// A tab is an estate the GAME grants (spec 2026-08-15: the roster is the tab set),
// plus wherever we are standing - the one place that must explain itself even when
// it is nobody's. Never-visited grants still tab: "access granted" is real state.
var roster = Game.RosterSensor.Current();
var records = estates
    .Where(e => e.Key == here || roster.Covers(e.Key))
    .ToList();
foreach (var granted in roster.Estates.Where(g => records.All(r => r.Key != g.Key)))
    records.Add(new EstateRecord { Key = granted.Key });
foreach (var record in records
             .OrderByDescending(e => e.Key == here)
             .ThenByDescending(e => e.LastVisited))
    DrawEstateTab(record, here, now);
```

(Ledger-only estates that are neither rostered nor underfoot drop out of the tab bar; their records stay in the ledger untouched.)

- [ ] **Step 2: The unrostered-current banner.** In `DrawEstateTab`'s body path, when `isHere && !CensusPump.CoveredHere`, draw before anything else and suppress all verb buttons for this tab:

```csharp
ImGui.TextColored(Amber, "not on your teleport list - Balamb doesn't track here");
ImGui.TextDisabled("what you can see below is live sensing only; nothing is recorded");
```

The cleanest mechanical form: `DrawEstateTab` computes `var actionable = !isHere || CensusPump.CoveredHere;` and passes it where the body draws verbs; verb `ImGui.Button` sites for tend/cycle/pot actions render disabled with tooltip `"Balamb doesn't act here - not on your teleport list"` when `!actionable`. (Away-tabs already draw no verbs; this only bites the standing tab.)

- [ ] **Step 3: Reword the identity strings.** Line 46:

```csharp
private const string UntrackedTag = "not identified yet - Balamb hasn't matched this to the game's own records";
```

Line ~344 (`"Nothing claimed here yet - tend a bed and it appears."`):

```csharp
? "Nothing recorded here yet - garden here once (or just stand near a known patch) and it appears."
```

Line ~403 pair (`{n} beds here are untracked` stays; the tag line under it now reads the new UntrackedTag automatically). Sweep the file for "claimed" in USER-FACING strings → "recorded" (`"{beds.Count} claimed · last visited ..."` → `"{beds.Count} recorded · last visited ..."`, `$"{rollup.Claimed} claimed"` label text likewise; the `PatchRollup.Claimed` property name itself is Engine surface and does not change in this plan).

- [ ] **Step 4: ConfigWindow** — delete the Claim-as-I-go checkbox block (lines 139–144) entirely.

- [ ] **Step 5: Re-read the diff. Do NOT build.**

- [ ] **Step 6: Commit**

```bash
git add BalambGarden/Windows/MainWindow.cs BalambGarden/Windows/ConfigWindow.cs
git commit -m "Tabs are the game-granted roster plus where you stand; unrostered ground is display-only; claim wording retired from the UI"
```

---

## Checkpoint (Fable + Drift, after Task 6)

1. Fable reviews every diff, then builds Debug x64 on Drift's explicit go (hot-load warning: it deploys live).
2. Live shakeout, in order:
   - **Papa's Place / apartment**: tabs render from the roster; pots now auto-record on sight (the wilt-lab twins should appear as records without any ceremony).
   - **Gardener's estate**: arrival populates 24 bed records from one sighting (patch 110 was already claimed; 116/117 fill in). Verdict line lights up with the ripe wall.
   - **THE ACCEPTANCE TEST**: one Cycle press on one ripe Gardener bed — menu offers Harvest, obtain line lands, ledger receipt records. Then the rest at Drift's pace.
   - **Rando's plot**: tab says display-only, no verbs, nothing written.
3. Merge `rebuild` → `main` when Drift calls the save point.

## Explicitly deferred (spec records why)

- GardenRights source 1 (direct grant read) and source 2 (banked menu observations) — Drift's v1 ruling: rostered = assume actionable.
- Auto-fill slot-click fix (separate queue item; FC pot 2 stays the acceptance test).
- `ClaimedBed` the TYPE keeps its name this pass — meaning documented in its header; renaming is churn across 20+ files with no behavior change.
