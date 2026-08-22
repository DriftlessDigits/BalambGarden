# CLAUDE.md - Balamb Garden

Dalamud plugin for FFXIV gardening: census, timers, and one-press tend/harvest/replant chains for the household's estates. Command: `/garden`. This doc is the machine - how the code works. Product direction and design rationale live in the vault (Rebuild Spec is canonical); this file gets reviewed at every merge to main.

## Build laws (read first)

- **Always `-p:Platform=x64`.** An unflagged build produces a stale/mismatched DLL that Dalamud loads anyway. This has bitten before.
- **Agents NEVER build the plugin.** Dalamud hot-loads the output DLL - any build is a live deploy into a running game. Builds happen only on the player's explicit "go", never while they may be in-game or testing.
- **Tests are safe anytime**: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj` - the Engine has zero game dependency.
- Feature branches only; merge to main = save point (this doc's review rides every merge - the merge gate enforces it).
- Repo is quasi-public. The maintainer persona is **Drift** (GitHub: DriftlessDigits); no real names in code, comments, commits, or docs. No AI co-authorship lines in commits.

## Solution layout

| Project | Target | Role |
|---------|--------|------|
| `BalambGarden/` | Dalamud.NET.Sdk 15 (x64) | The plugin: game sensors, chains, UI, wiring |
| `BalambGarden.Engine/` | net9.0, no game refs | All logic: census, ledger, derivations, decode |
| `BalambGarden.Engine.Tests/` | xUnit | Desk tests; captured probe logs drive the decode paths |
| `ECommons/` | submodule | Chain UI automation (ClickAddonButton etc.) |
| `Data/` | embedded at build | Frozen domain tables (see below) |

## Architecture: five layers, one-way flow

Each layer knows only the layer below. **Acting IS censusing**: chains emit dialogue receipts that flow back in through the pump.

1. **Sensing** (`Engine/Sensing/` + `BalambGarden/Game/`) - stateless readers.
   - `MapSensor`: housing DataMap decode - outdoor 48-byte entries (8 beds per patch key), indoor per-pot sub-entry 0. Formats in `Engine/Sensing/MapFormat.cs` (bytes 4-5 are allocator junk - never read them).
   - `ObjectSensor` / `GimmickId`: nearby bed objects - GimmickId bytes carry [bed index][patch ordinal][patch-id u16]; reach verdicts at full precision.
   - Pot identity is a **direct read** off the pot object: `HousingFurnitureIndex` + `BaseId` (= HousingFurniture sheet row). `FlowerpotSheet` derives the 3 flowerpot rows by name scan, fails loud on drift.
   - `RosterSensor` / `EstateSensor`: permission from real game data - HouseId direct equality decides "ours"; unfittable rows fail CLOSED and log raw.
   - `PotPigment`: indoor byte 2 = (pigment<<4)|stage; only receipted nibbles are interpreted, a color is never guessed.
2. **Census** (`Engine/Census/`) - the join + claim brain. `JoinShortlist` proposes patch<->key candidates from diffs; **only a receipt binds** (`JoinConfirm` - exactly one survivor or nothing). **There is no Claim() method anywhere** - claiming happens only through a completed receipted interaction (`ReceiptEvent`: Tend / Harvest / Plant / PotWater). `AccessRoster` scopes the census: not covered = not tracked.
3. **Ledger** (`Engine/Ledger/`) - `LedgerStore` persists claimed beds + bindings. `ClaimedBed` = current state + an 8-observation ring (what brackets, wilt clocks, and provenance consume). `ClaimedBed.Rebase()` is the reconcile path: **the game wins on content mismatch** (species change / stage regression / reads-empty rebase; a matching read keeps anchors). `DebugTrail` (trail.jsonl) is append-only evidence the engine never reads back.
4. **Derivations** (`Engine/Derivations/`) - pure functions. Every timer is an `EtaWindow` (never a point) with `Provenance` (anchored / bracketed / estimated). `ClockWiltSource`: Watered -> Due -> Overdue -> Danger from last-tend + crop wilt hours; `DiesAt` = tend + witherHours; live stage 4 (ripe) forces NotApplicable - fully grown cannot wilt. `PipelineReader` = tips: advisory sentences over claimed plantings + CrossbreedPairs, never a planner. `PotCyclePlanner` builds full plans or refuses (never a half-plan); soil rule: exactly one soil in bags = the soil, else picker.
5. **Verbs & UI** (`BalambGarden/Chains/` + `Windows/`) - `ChainBase`: paced dialogue chains (human tempo + jitter, occupied-player guard, clean stop with stated reason; stops land during human-wait steps). `TendChain`, `CycleChain` (harvest + replant beds), `PotChain` (pot cycle incl. full-auto plant), with `PlantFlow`/`PlantFill` doing the read-and-click (no pacing, no ledger writes). `MainWindow` = the dashboard (tab per covered estate, patch strips, exception-first water column); `RunLogWindow`; `ConfigWindow`.

**`CensusPump` (`Game/CensusPump.cs`) is the heartbeat**: sensors read, receipts route, the ledger learns. `RefreshDisplayOnly` is the UI-read-without-recording path (post-run settle poll - do not add observations from it). `ReconcileAbsentPots`: a vanished pot entry = harvest happened unseen (gated on actually having seen the housing objects that frame).

## Storage (plugin config directory)

- `ledger-v2.json` - the LedgerStore. **Fail closed**: an unreadable ledger is parked as `.unreadable-<stamp>`, never overwritten. `LedgerMigration.NormalizeEstates` runs idempotently at load (pre-08-15 files held one physical plot as two records).
- `trail.jsonl` - append-only receipt trail (evidence, not memory).
- The POC ledger (inside `Configuration`) is **never read** - fresh start by spec.

## Domain data (`Data/`, frozen since 2020, embedded at build)

`Crops.json` (81 crops: grow/wilt/wither hours, seed joins) · `CrossbreedPairs.json` (3,886 pairs) · `SpeciesIndex.json` (DataMap species u16 -> names) · `Soils.json`. `DomainTables` also carries `TalkAliases` - receipts-only name aliases ("Royal Kukuru Bean" -> "Royal Kukuru"); **reveal-never-invent**: a new alias entry requires a receipted string. Standing reopen trigger: the field contradicting a table value.

## Failure signatures (field-tested)

- Stale behavior after build -> the x64 flag was missing.
- `PotObject` is a record **struct** - copies mutate silently if you forget.
- UI automation: HousingGardening component nodes are Type >= 1000 (a plain node scan never sees them); buttons take `ButtonClick`, not `MouseClick`; the winning plant path is OpenForItemSlot -> context-menu Use by name -> ClickAddonButton, with settle beat + empty-slot icon baseline `0xFFFFFFFF`.
- Colored Talk names strip to base; an unresolvable Talk name lands receipts as unknown -> check `TalkAliases`.
- Outdoor wilt has **no client-side state anywhere** (DataMap + EventObject + HousingObjectManager all snapshot-diffed across a live watering: zero bytes). Prose at tend time is the only channel. Do not go hunting again without a new lead - the empty search is receipted.
- Last chain step has no successor to trigger a map re-read -> displays race; that's what the post-run settle poll (`RefreshDisplayOnly`) is for.

## Release

Version lives in `BalambGarden/BalambGarden.csproj` (`<Version>`). Ships through DriftlessDigits/DalamudPluginRepo - the pluginmaster regenerates itself on push (SHOEGAZEssb fork; never hand-edit it; `gh` workflow dispatch 403s). Dev loads read `images/icon.png` beside the DLL; `IconUrl` is fetched only for repo installs; the game caches the icon image across restarts.

## Debug-only instrumentation

`ReconProbe` (debug builds): raw hex dumped verbatim beside every decode so a decoder bug is visible, not hidden. `PlantFlow` watcher is `#if DEBUG`.
