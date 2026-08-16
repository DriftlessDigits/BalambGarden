# One-Grammar UI Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collapse the claim-era UI split (outdoor tables vs. the "Pots in reach" card) into one row grammar for every growing thing, exception-first.

**Architecture:** Engine rollups learn two things (one pot group per estate; ripe windows grouped by species), then MainWindow renders indoor and outdoor through the same rollup-row + expandable-grid path, moves pot verbs onto pot rows, retires the pots-in-reach card and per-row Abandon buttons, and drops the Water column in favor of exception text.

**Tech Stack:** C# / Dalamud ImGui (Dalamud.Bindings.ImGui + ImRaii), xUnit for Engine.

**Spec:** The Design Rulings section below (ruled by Sam, 2026-08-15 evening, in conversation). Permission background: vault `Deliveries/Balamb Garden/Balamb Garden - Permission Architecture.md`.

## Design Rulings (the spec)

1. **High-level summary lives above the tabs** (verdict line + locator notes). Already true; unchanged. The tab is the estate's work surface.
2. **One row grammar for indoor and outdoor.** The Outdoor/Indoor section headers stay (they describe real space), but rows read identically: what's planted, stage, ripe window, verbs. The differences that survive are the ones the game earns: outdoor beds group into patches with a strip and aggregate verbs (Water Patch, Cycle); pots are singletons.
3. **The "Pots in reach" card dies.** It was Plan B's live-sensing surface from before pots had identity. Sightings create ledger rows on rostered ground now, so a rostered occupied pot is always recorded; reach governs whether a row's *verbs* are enabled, not which *surface* the pot appears on. Verbs move onto pot rows.
4. **One Pots group per estate.** Today each pot gets its own "Pots" section (rollups group by PatchOrdinal, and pots carry distinct ordinals). All of an estate's pots are one group, one grid.
5. **Empty pots still need a row.** An empty pot has no ledger row (nothing to sight), but Plant is exactly the verb an empty pot needs. Pots in reach with no ledger row render as sensed rows with a Plant verb.
6. **Soil/seed pickers exist only when a Plant press needs them.** Plant opens an inline per-pot panel (like the Cycle panel); the two always-visible global pickers go away.
7. **Abandon leaves the rows.** Ledger rows are game-grounded now; forgetting one is a rare correction, not a per-row invitation. It becomes a right-click context menu on the row's name cell, relabeled "Forget this record" (abandon was claim vocabulary). The drift row keeps an inline button (forgetting is that row's entire purpose), same label.
8. **Exception-first water.** The grid's Water column goes away. A steady patch says "all watered" once on its rollup line; a bed that wants something says so in amber/red text beside its name. The strip's under-bars and tooltips keep carrying the detail.
9. **Ripe summary groups by species.** A patch running two crops shows both windows on its rollup line ("Royal Kukuru ~Mon 20:42-Wed 14:55 · Curiel Root ~Mon 08:45-12:03"), not just the nearest.
10. **No pot-immortality assertions.** The "pots never wilt" evidence base is flower seeds only; whether it's a pot mechanic or a flower oddity is exactly what the running Krakka/melon twins labs discriminate. The pot Water button says "Water" (not "Water (pigment)"), tooltips say what's receipted (pigment changes) and what's unverified (whether pots need water to live). Engine *behavior* is unchanged until the labs report - `WaterState.NotApplicable` stays - but comments stop overclaiming.

## Global Constraints

- **Agents NEVER build or run the plugin project** (`BalambGarden/BalambGarden.csproj`). A build hot-loads into Sam's running game. Engine work only: `dotnet build BalambGarden.Engine.Tests` and `dotnet test BalambGarden.Engine.Tests` are the only build/test commands permitted. Fable builds the plugin on Sam's explicit "go", never an agent.
- The plugin project may be non-compilable mid-sequence (Task 1 changes a grouping the UI consumes until Task 3 catches up). Pre-ruled acceptable; the checkpoint build at the end proves it out.
- **Verify green THEN commit, as separate steps, checking exit codes.** Never pipe a build into anything that can mask its exit code.
- **No AI co-authorship lines in commits.**
- Branch: a new branch `ui-redesign` off `rebuild` (execution starts only after Sam calls it; the permission-architecture shakeout and its merge to `main` come first).
- The ledger file (`ledger-v2.json`) is sacred: no schema or serialization changes in this plan.
- Copy rules: user-facing strings use " - " never em-dashes; "recorded"/"record" vocabulary, never "claim"/"abandon".

## File Structure

- `BalambGarden.Engine/Derivations/Rollup.cs` - pot grouping + species ripe windows (Tasks 1-2)
- `BalambGarden.Engine.Tests/Derivations/RollupTests.cs` - tests for both (Tasks 1-2)
- `BalambGarden/Windows/MainWindow.cs` - everything else (Tasks 3-5)

No new files. No other file changes.

---

### Task 1: One pot group per estate (Engine)

**Files:**
- Modify: `BalambGarden.Engine/Derivations/Rollup.cs`
- Test: `BalambGarden.Engine.Tests/Derivations/RollupTests.cs`

**Interfaces:**
- Produces: `Rollups.PotsOrdinal` (public const int, value -1). Pot rollups now arrive as ONE `PatchRollup` per estate with `PatchOrdinal == Rollups.PotsOrdinal`. Task 3 consumes this in `MainWindow.BedsOf`.

- [ ] **Step 1: Write the failing test**

Add to `RollupTests.cs` (follow the file's existing helper style for building `ClaimedBed`s - it already constructs pot beds for the NotApplicable tests; reuse those helpers verbatim):

```csharp
[Fact]
public void PotsRollUpAsOneGroupPerEstate()
{
    // Two pots with different patch ordinals (real shape: pot ordinals differ per pot).
    var beds = new List<ClaimedBed>
    {
        PotBed(mapKey: 180, patchOrdinal: 180),
        PotBed(mapKey: 181, patchOrdinal: 181),
    };

    var rollups = Rollups.ForEstate(Estate, beds, Tables, Wilt, T0);

    var pots = Assert.Single(rollups, r => r.IsPots);
    Assert.Equal(2, pots.Claimed);
    Assert.Equal(Rollups.PotsOrdinal, pots.PatchOrdinal);
}
```

If the file has no `PotBed(mapKey, patchOrdinal)` helper with that exact shape, add one modeled on its existing pot-bed construction (Estate + MapKey + PatchOrdinal + BedSlot + IsPot true).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test BalambGarden.Engine.Tests --filter PotsRollUpAsOneGroupPerEstate`
Expected: FAIL (two rollups come back, or `PotsOrdinal` does not exist - compile error is an acceptable failure mode for this step).

- [ ] **Step 3: Implement**

In `Rollup.cs`, add the constant to `Rollups` and change the grouping key:

```csharp
public static class Rollups
{
    /// <summary>The ordinal every pot rollup carries: an estate's pots are ONE group
    /// (UI ruling 2026-08-15), whatever per-pot ordinals the ledger rows hold.</summary>
    public const int PotsOrdinal = -1;
```

and in `ForEstate`, replace

```csharp
            .GroupBy(b => (b.PatchOrdinal, b.IsPot))
```

with

```csharp
            .GroupBy(b => (PatchOrdinal: b.IsPot ? PotsOrdinal : b.PatchOrdinal, IsPot: b.IsPot))
```

(The `.Select` body already reads `g.Key.PatchOrdinal` / `g.Key.IsPot`; the named tuple keeps those expressions compiling unchanged.)

- [ ] **Step 4: Run the full Engine suite**

Run: `dotnet test BalambGarden.Engine.Tests`
Expected: PASS, all green. If an existing test asserted per-pot rollups, update it to the new one-group contract (that is the ruled behavior, not a regression).

- [ ] **Step 5: Commit**

```bash
git add BalambGarden.Engine/Derivations/Rollup.cs BalambGarden.Engine.Tests/Derivations/RollupTests.cs
git commit -m "An estate's pots roll up as one group"
```

---

### Task 2: Ripe windows grouped by species (Engine)

**Files:**
- Modify: `BalambGarden.Engine/Derivations/Rollup.cs`
- Test: `BalambGarden.Engine.Tests/Derivations/RollupTests.cs`

**Interfaces:**
- Produces: `public sealed record SpeciesRipe(ushort SpeciesIndex, EtaWindow Window);` and a new `PatchRollup` member `IReadOnlyList<SpeciesRipe> RipeBySpecies` (ordered by `Window.Earliest`, one entry per species that has a computable window among non-ripe beds). `NextRipe` remains and equals `RipeBySpecies` first entry's window (or null). Task 5 consumes `RipeBySpecies` in `MainWindow.DrawRollupSummary`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void RipeWindowsGroupBySpecies()
{
    // Two species in one patch, different grow clocks -> two entries, earliest first,
    // and NextRipe agrees with the first entry.
    var beds = new List<ClaimedBed>
    {
        BedWithSpecies(slot: 0, speciesA),   // the slower crop
        BedWithSpecies(slot: 1, speciesB),   // the faster crop
        BedWithSpecies(slot: 2, speciesA),   // duplicate species: still one entry
    };

    var rollup = Assert.Single(Rollups.ForEstate(Estate, beds, Tables, Wilt, T0));

    Assert.Equal(2, rollup.RipeBySpecies.Count);
    Assert.Equal(speciesB, rollup.RipeBySpecies[0].SpeciesIndex);
    Assert.Equal(speciesA, rollup.RipeBySpecies[1].SpeciesIndex);
    Assert.True(rollup.RipeBySpecies[0].Window.Earliest <= rollup.RipeBySpecies[1].Window.Earliest);
    Assert.Equal(rollup.RipeBySpecies[0].Window, rollup.NextRipe);
}
```

Use two species the test file's `Tables` fixture already knows with different `GrowHours` (pick from the existing fixture; do not invent table entries). `BedWithSpecies` = an outdoor bed whose ring holds one observation of that species at a pre-ripe stage, in the file's existing style.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test BalambGarden.Engine.Tests --filter RipeWindowsGroupBySpecies`
Expected: FAIL (`RipeBySpecies` does not exist - compile error acceptable).

- [ ] **Step 3: Implement**

In `Rollup.cs`:

```csharp
public sealed record SpeciesRipe(ushort SpeciesIndex, EtaWindow Window);

public sealed record PatchRollup(
    EstateKey Estate, int PatchOrdinal, bool IsPots,
    int Claimed, int Ripe, int Due, int Overdue, int Danger, int Unknown,
    EtaWindow? NextRipe, IReadOnlyList<SpeciesRipe> RipeBySpecies);
```

In `ForEstate`'s select body, replace the `nextRipe` accumulation with per-species accumulation:

```csharp
                int ripe = 0, due = 0, overdue = 0, danger = 0, unknown = 0;
                var ripeBySpecies = new Dictionary<ushort, EtaWindow>();
                foreach (var bed in g)
                {
                    var latest = bed.Latest;
                    var isRipe = latest?.Stage == 4;
                    if (isRipe) ripe++;

                    var crop = latest is null ? null : tables.CropBySpeciesIndex(latest.SpeciesIndex);
                    // (keep the existing water-state block and its comment edits from Task 5 out
                    // of scope here - only the ripe accumulation changes)
                    var state = bed.IsPot ? WaterState.NotApplicable
                        : crop is null ? WaterState.Unknown
                        : wilt.StateFor(bed, crop, now);
                    switch (state)
                    {
                        case WaterState.Due: due++; break;
                        case WaterState.Overdue: overdue++; break;
                        case WaterState.Danger: danger++; break;
                        case WaterState.Unknown: unknown++; break;
                    }

                    if (!isRipe && latest is not null && crop is not null
                        && StageModel.RipeWindow(bed.Ring, crop.GrowHours) is { } window
                        && (!ripeBySpecies.TryGetValue(latest.SpeciesIndex, out var held)
                            || window.Earliest < held.Earliest))
                        ripeBySpecies[latest.SpeciesIndex] = window;
                }

                var speciesRipe = ripeBySpecies
                    .Select(kv => new SpeciesRipe(kv.Key, kv.Value))
                    .OrderBy(s => s.Window.Earliest)
                    .ToList();
                return new PatchRollup(estate, g.Key.PatchOrdinal, g.Key.IsPot,
                    g.Count(), ripe, due, overdue, danger, unknown,
                    speciesRipe.Count == 0 ? null : speciesRipe[0].Window, speciesRipe);
```

Fix every call site that constructs `PatchRollup` directly (tests may): append `, []` or a real list as the new argument.

- [ ] **Step 4: Run the full Engine suite**

Run: `dotnet test BalambGarden.Engine.Tests`
Expected: PASS, all green.

- [ ] **Step 5: Commit**

```bash
git add BalambGarden.Engine/Derivations/Rollup.cs BalambGarden.Engine.Tests/Derivations/RollupTests.cs
git commit -m "Rollups carry a ripe window per species, earliest first"
```

---

### Task 3: One grammar - pot rows in the grid, pots-in-reach card retired (plugin, NO BUILD)

**Files:**
- Modify: `BalambGarden/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: `Rollups.PotsOrdinal` (Task 1); `PotObject { Object, Name, Distance, MapKey, InReach }` from `ObjectSensor.NearbyPots()`; `PotChain.Water(PotObject)`, `.Harvest(PotObject)`, `.Plant(PotObject, uint soilItemId, uint expectedSeedId)`.
- Produces: pot verbs live on grid rows; `DrawPots`, `DrawPotSoilPicker`, `DrawPotSeedPicker`, `DrawPotIdentity` and the fields `potSeedId`/`potSoilId` are deleted; new fields `plantPanelPot` (int?) + `plantSoilId`/`plantSeedId` (uint) back the inline Plant panel. `SoilsInBag()` and `InventoryCount` survive (the panel uses them).

**No build, no test run.** This task is reviewed as a diff. Self-check is by reading: every deleted symbol must have zero remaining references in the file.

- [ ] **Step 1: Route pot rollups into the shared grid**

Replace `BedsOf` so the merged pot group finds its beds (pots ignore ordinal, ordered by map key):

```csharp
    private static List<ClaimedBed> BedsOf(EstateKey estate, PatchRollup rollup)
    {
        var beds = Plugin.Garden.Census.LedgerBeds
            .Where(b => b.Estate == estate && b.IsPot == rollup.IsPots);
        return rollup.IsPots
            ? beds.OrderBy(b => b.MapKey).ToList()
            : beds.Where(b => b.PatchOrdinal == rollup.PatchOrdinal)
                  .OrderBy(b => b.BedSlot).ToList();
    }
```

- [ ] **Step 2: Rewrite the Indoor section**

Replace `DrawIndoorSection` entirely:

```csharp
    /// <summary>One grammar (UI ruling 2026-08-15): pots render through the same rollup
    /// row + grid as patches. The rows are the ledger; reach only decides whether a row's
    /// verbs light up. The one thing the ledger cannot show is an EMPTY pot in reach -
    /// nothing to sight, no row - and Plant is exactly the verb an empty pot needs, so
    /// those render as sensed rows below the grid.</summary>
    private void DrawIndoorSection(
        EstateRecord record, List<PatchRollup> rollups, List<PotObject> pots,
        bool isHere, bool actionable, DateTimeOffset now)
    {
        foreach (var rollup in rollups)
            DrawRollupRow(record, rollup, [], isHere, actionable, now);

        // Pots in front of us that no ledger row names: empty pots, plus any the
        // position read could not key. They need a row or Plant is unreachable.
        var unrecorded = pots
            .Where(p => p.MapKey is not { } key || !Plugin.Garden.Census.LedgerBeds.Any(
                b => b.Estate == record.Key && b.IsPot && b.MapKey == key))
            .ToList();

        foreach (var pot in unrecorded)
        {
            using var id = ImRaii.PushId((int)pot.Object.EntityId);
            ImGui.Spacing();
            if (!pot.InReach)
            {
                ImGui.TextDisabled($"{pot.Name} · {pot.Distance:F1}y away - walk closer");
                continue;
            }

            ImGui.TextDisabled(pot.MapKey is { } mapKey
                ? $"{pot.Name} · empty"
                : $"{pot.Name} · {UntrackedTag}");
            ImGui.SameLine();
            using (ImRaii.Disabled(plugin.AnyChainBusy || !actionable))
            {
                if (ImGui.SmallButton("Plant..."))
                    TogglePlantPanel(pot);
            }
            BusyTip();
            UnrosteredTip(actionable);
            DrawPlantPanel(pot);
        }
    }
```

- [ ] **Step 3: Give pot rows verbs in the grid**

In `DrawBedGrid`, the verbs cell currently calls `DrawBedVerbs(record, bed, bedObject, actionable)`. Change that call site to route by kind:

```csharp
            ImGui.TableNextColumn();
            if (bed.IsPot)
                DrawPotRowVerbs(bed, actionable);
            else
                DrawBedVerbs(record, bed, bedObject, actionable);
```

and add, next to `DrawBedVerbs`:

```csharp
    /// <summary>A pot row's verbs, lit only when the pot object itself is in reach.
    /// Identity is the direct read: furniture index == map key (08-15), so matching the
    /// row to the object is a lookup, not a diff.</summary>
    private void DrawPotRowVerbs(ClaimedBed bed, bool actionable)
    {
        var pot = EstateSensor.IsInside()
            ? ObjectSensor.NearbyPots().FirstOrDefault(p => p.MapKey == bed.MapKey)
            : null;

        if (pot is null || !pot.InReach)
        {
            ImGui.TextDisabled(pot is null ? "-" : $"{pot.Distance:F1}y");
            return;
        }

        using (ImRaii.Disabled(plugin.AnyChainBusy || !actionable))
        {
            if (ImGui.SmallButton("Water"))
            {
                plugin.PotChain.Water(pot);
                plugin.Launched(plugin.PotChain);
            }
            if (actionable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(
                    "Changes petal pigment (receipted). Whether a pot also NEEDS water"
                    + "\nto live is unverified - the twins lab will say.");
            UnrosteredTip(actionable);

            ImGui.SameLine();
            if (ImGui.SmallButton("Harvest"))
            {
                plugin.PotChain.Harvest(pot);
                plugin.Launched(plugin.PotChain);
            }
            UnrosteredTip(actionable);

            ImGui.SameLine();
            if (ImGui.SmallButton("Plant..."))
                TogglePlantPanel(pot);
            UnrosteredTip(actionable);
        }

        BusyTip();
        DrawPlantPanel(pot);
    }
```

Note `DrawPlantPanel` is drawn from inside a table cell here; ImGui tolerates child widgets in cells, and the panel is intentionally compact (two combos + one button) so it stays inside the row's cell. If the reviewer judges in-cell layout too cramped to read, the fallback ruling is pre-made: draw the panel after the table instead, keyed off `plantPanelPot`, matching how `DrawCyclePanel` renders after its row.

- [ ] **Step 4: The inline Plant panel**

Replace the fields `potSeedId`/`potSoilId` and their comment block with:

```csharp
    // The inline Plant panel: which pot's panel is open (by map key when it has one,
    // else by entity id negated to avoid collision), and its order form. 0 = "whatever
    // I pick in game" - the picker stays the player's and the chain only verifies.
    private long? plantPanelPot;
    private uint plantSoilId;
    private uint plantSeedId;

    private static long PanelKey(PotObject pot)
        => pot.MapKey is { } key ? key : -(long)pot.Object.EntityId;

    private void TogglePlantPanel(PotObject pot)
    {
        var key = PanelKey(pot);
        if (plantPanelPot == key)
        {
            plantPanelPot = null;
            return;
        }
        plantPanelPot = key;
        plantSoilId = 0;
        plantSeedId = 0;
    }
```

Then replace `DrawPotSoilPicker` and `DrawPotSeedPicker` with one panel (their combo bodies move here nearly verbatim - keep the tooltips, keep the live `SoilsInBag()` read and its rationale comment from the old `DrawPotSoilPicker` doc comment):

```csharp
    /// <summary>The order form for one pot, open only while a Plant is being set up
    /// (UI ruling 2026-08-15: pickers exist only when a Plant press needs them). Soil is
    /// read live off the bags by name - there is no potting-soil table, on purpose (see
    /// the git history of DrawPotSoilPicker for the full rationale). Naming soil/seed
    /// lets the chain fill the game's picker; leaving either on its default keeps those
    /// clicks the player's. The confirmation is checked against this form either way.</summary>
    private void DrawPlantPanel(PotObject pot)
    {
        if (plantPanelPot != PanelKey(pot))
            return;

        using var indent = ImRaii.PushIndent();

        var soils = SoilsInBag();
        var chosenSoil = soils.FirstOrDefault(s => s.ItemId == plantSoilId);
        var soilLabel = plantSoilId == 0 || chosenSoil.ItemId == 0
            ? "Whatever's in the picker"
            : $"{chosenSoil.Name} ({chosenSoil.Count})";
        ImGui.SetNextItemWidth(260f);
        using (var combo = ImRaii.Combo("Soil", soilLabel))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable("Whatever's in the picker", plantSoilId == 0))
                    plantSoilId = 0;
                foreach (var soil in soils)
                {
                    if (ImGui.Selectable($"{soil.Name} ({soil.Count})", soil.ItemId == plantSoilId))
                        plantSoilId = soil.ItemId;
                }
            }
        }

        var seedLabel = plantSeedId == 0
            ? "Whatever I pick in game"
            : Plugin.Tables.CropBySeedId(plantSeedId)?.SeedName ?? $"seed {plantSeedId}";
        ImGui.SetNextItemWidth(260f);
        using (var combo = ImRaii.Combo("Verify seed", seedLabel))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable("Whatever I pick in game", plantSeedId == 0))
                    plantSeedId = 0;
                foreach (var crop in Plugin.Tables.Crops)
                {
                    var have = InventoryCount(crop.SeedId);
                    if (have == 0 && crop.SeedId != plantSeedId)
                        continue;
                    if (ImGui.Selectable($"{crop.SeedName} ({have})", crop.SeedId == plantSeedId))
                        plantSeedId = crop.SeedId;
                }
            }
        }

        using (ImRaii.Disabled(plugin.AnyChainBusy))
        {
            if (ImGui.SmallButton("Plant"))
            {
                plugin.PotChain.Plant(pot, plantSoilId, plantSeedId);
                plugin.Launched(plugin.PotChain);
                plantPanelPot = null;
            }
        }
        BusyTip();
    }
```

- [ ] **Step 5: Delete the card**

Delete entirely: `DrawPots`, `DrawPotIdentity`, `DrawPotSoilPicker`, `DrawPotSeedPicker`, and the `DrawPots(...)` call at the end of the old `DrawIndoorSection`. Keep `SoilsInBag()` and `InventoryCount` (the panel uses them). Update the pot-row name cell in `DrawBedGrid` from `pot key {bed.MapKey}` to `Pot {bed.MapKey}` (the word "key" is plumbing vocabulary). Search the file for `potSeedId`, `potSoilId`, `DrawPots`, `Pots in reach` - all must be gone.

- [ ] **Step 6: Self-review and commit**

Read the full diff. Checklist: no deleted symbol referenced anywhere; `DrawRollupRow`'s pot path (`patches: []`) still guards `patch is null` everywhere it dereferences; `using var id` scoping unique per row/pot; strings use " - " and "recorded" vocabulary.

```bash
git add BalambGarden/Windows/MainWindow.cs
git commit -m "One grammar: pot verbs live on their rows, the pots-in-reach card retires"
```

---

### Task 4: Forgetting is a context menu (plugin, NO BUILD)

**Files:**
- Modify: `BalambGarden/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: `ArmedButton` (existing), `Plugin.Garden.Census.Abandon(bed)` (existing Engine API - the *method* name stays; only user-facing copy changes).
- Produces: no per-row Abandon buttons; right-click on a row's name cell opens the forget menu; drift rows keep an inline button relabeled.

- [ ] **Step 1: The context menu**

Add:

```csharp
    /// <summary>Forgetting a record is manual, deliberate, and now tucked behind a
    /// right-click (UI ruling 2026-08-15): rows are game-grounded, so removing one is a
    /// rare correction, not a per-row invitation. Still armed - two clicks, no modal.</summary>
    private void DrawForgetMenu(EstateRecord record, ClaimedBed bed)
    {
        if (!ImGui.BeginPopupContextItem($"forget{bed.MapKey}:{bed.BedSlot}"))
            return;

        ImGui.TextDisabled("forgets Balamb's record only - the game is untouched");
        if (ArmedButton($"forget:{record.Key.BindingKey(bed.PatchOrdinal)}:{bed.BedSlot}",
                "Forget this record", "Forget - sure?", small: true))
        {
            Plugin.Garden.Census.Abandon(bed);
            Plugin.Garden.Save();
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
```

- [ ] **Step 2: Hang it off the name cell**

In `DrawBedGrid`'s name cell, the row's name becomes a selectable (a text item cannot own a context popup), with the menu attached:

```csharp
            ImGui.TableNextColumn();
            ImGui.Selectable(bed.IsPot ? $"Pot {bed.MapKey}" : $"Bed {bed.BedSlot + 1}");
            DrawForgetMenu(record, bed);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("right-click to forget this record");
```

(keep the existing "in reach" green tag rendering after this, unchanged, for outdoor rows).

Note the hover tooltip belongs to the Selectable; place the `IsItemHovered` check immediately after `Selectable` and before any other item is drawn, or attach it before `DrawForgetMenu` - implementer's choice, but the tooltip must anchor to the name cell.

- [ ] **Step 3: Remove the buttons**

- In `DrawBedVerbs`: delete the `DrawAbandonButton(record, bed);` call and the `ImGui.SameLine();` that precedes it; the method's doc comment loses its Abandon sentence.
- In `DrawDriftRow`: replace `DrawAbandonButton(record, bed)` with an inline armed button using the new copy (this row's whole purpose is forgetting, so it stays a button):

```csharp
        var key = $"forget:{record.Key.BindingKey(bed.PatchOrdinal)}:{bed.BedSlot}";
        if (ArmedButton(key, "Forget record", "Forget - sure?", small: true))
        {
            Plugin.Garden.Census.Abandon(bed);
            Plugin.Garden.Save();
        }
```

- Delete `DrawAbandonButton` entirely. Search the file for `Abandon` - remaining hits must be only the Engine API call `Census.Abandon` (twice: menu + drift row).

- [ ] **Step 4: Self-review and commit**

Read the diff; verify the two `Census.Abandon` call sites and zero user-facing "Abandon" strings.

```bash
git add BalambGarden/Windows/MainWindow.cs
git commit -m "Forgetting a record moves behind a right-click; abandon vocabulary retires"
```

---

### Task 5: Exception-first water + species ripe lines + honest pot copy (plugin + Engine comments, NO plugin BUILD)

**Files:**
- Modify: `BalambGarden/Windows/MainWindow.cs`
- Modify: `BalambGarden.Engine/Derivations/Rollup.cs` (comment only)
- Modify: `BalambGarden.Engine/Derivations/PatchStrip.cs` (comment only)

**Interfaces:**
- Consumes: `PatchRollup.RipeBySpecies` (Task 2), `Plugin.Tables.SpeciesName(ushort)`.

- [ ] **Step 1: Drop the Water column, surface exceptions inline**

In `DrawBedGrid`: change the table to 5 columns, delete the `Water` `TableSetupColumn` line and the `ImGui.TableNextColumn(); DrawWaterCell(bed, crop, now);` pair. Delete `DrawWaterCell`. In `DrawDriftRow`, remove one `ImGui.TableNextColumn();` so the column count still matches.

In the name cell (after the Selectable + forget menu + tooltip from Task 4, before the "in reach" tag), surface only a state that is asking for something:

```csharp
            var water = bed.IsPot ? WaterState.NotApplicable
                : crop is null ? WaterState.Unknown
                : Plugin.Garden.Wilt.StateFor(bed, crop, now);
            if (water is WaterState.Due or WaterState.Overdue or WaterState.Danger)
            {
                ImGui.SameLine();
                ImGui.TextColored(water == WaterState.Danger ? Red : Amber,
                    water == WaterState.Danger ? "· DANGER - water now" : "· thirsty");
            }
```

(`crop` and `latest` are computed before the cells today; hoist that computation above the name cell if it is not already.)

The steady state moves to the rollup line - in `DrawRollupSummary`, after the count line, add:

```csharp
        var thirsty = rollup.Due + rollup.Overdue + rollup.Danger;
        if (!rollup.IsPots && rollup.Claimed > 0 && thirsty == 0 && rollup.Unknown == 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("· all watered");
        }
```

(then the existing `thirsty > 0` block follows, reusing the same `thirsty` local - do not compute it twice).

- [ ] **Step 2: Species ripe lines on the rollup**

Still in `DrawRollupSummary`, replace the `NextRipe` block (everything from `if (rollup.NextRipe is not { } window)` to the end of the method) with:

```csharp
        foreach (var species in rollup.RipeBySpecies)
        {
            ImGui.SameLine();
            var range = WindowFormat.Range(
                species.Window.Earliest.ToLocalTime(), species.Window.Latest.ToLocalTime());
            ImGui.TextDisabled($"· {Plugin.Tables.SpeciesName(species.SpeciesIndex)} ~{range}");
            ImGui.SameLine();
            ImGui.TextDisabled(WindowFormat.Mark(species.Window.Provenance));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(WindowFormat.MarkMeaning(species.Window.Provenance));
        }
```

- [ ] **Step 3: Stop asserting pot immortality**

Copy and comments only; zero behavior change, `WaterState.NotApplicable` stays until the twins labs report.

- `MainWindow.cs`: if any "Water (pigment)" button label or "Pot flowers never wilt" tooltip text survives Tasks 3-4 anywhere in the file, replace with the Task 3 wording ("Water" / "unverified - the twins lab will say"). Search for `pigment` and `never wilt`.
- `Rollup.cs` comment: replace the "flowerpots cannot wilt" comment block above the water-state expression with:

```csharp
                    // A pot's water state is deliberately out of the thirst counts: no pot
                    // has ever been SEEN to wilt, but the evidence base is flower seeds
                    // only - whether that is a pot mechanic or a flower oddity is exactly
                    // what the dry-vs-watered twins labs are running to decide (08-15).
                    // Until they report, NotApplicable asserts nothing either way.
```

- `PatchStrip.cs` comment: same softening - the parenthetical claiming pot flowers never wilt becomes "(pot wilt is unverified - the labs will say; until then pots assert nothing)".

- [ ] **Step 4: Run the Engine suite (comments touched Engine files)**

Run: `dotnet test BalambGarden.Engine.Tests`
Expected: PASS, all green (comment-only Engine edits).

- [ ] **Step 5: Self-review and commit**

Diff checklist: column count 5 everywhere (grid + drift row); no `DrawWaterCell`; no user-facing pigment/never-wilt claims; `RipeBySpecies` rendering handles the empty list (loop simply draws nothing).

```bash
git add BalambGarden/Windows/MainWindow.cs BalambGarden.Engine/Derivations/Rollup.cs BalambGarden.Engine/Derivations/PatchStrip.cs
git commit -m "Exception-first water, ripe windows per species, pot copy stops overclaiming"
```

---

## Checkpoint (Fable, not agents)

After Task 5: Fable builds the plugin on Sam's explicit "go" (`dotnet build BalambGarden -c Debug -p:Platform=x64`), verifies green, hot-load lands live. Shakeout script (Sam's hands): one Pots section per indoor tab with verbs on rows; empty pot shows Plant; Plant panel opens/fills/plants; right-click forget works and no Abandon buttons remain; a thirsty bed reads amber inline; a steady patch reads "all watered"; a two-species patch shows both ripe windows.
