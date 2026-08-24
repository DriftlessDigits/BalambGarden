# CLAUDE.md - Balamb Garden

Dalamud plugin for FFXIV gardening: census, timers, and one-press tend/harvest/replant chains for the household's estates. Command: `/garden`. This doc is the machine - how the code works. Product direction and design rationale live in the vault (Rebuild Spec is canonical); this file gets reviewed at every merge to main.

## Build laws (read first)

- **Always `-p:Platform=x64`.** An unflagged build produces a stale/mismatched DLL that Dalamud loads anyway. This has bitten before.
- **Agents NEVER build the plugin.** Dalamud hot-loads the output DLL - any build is a live deploy into a running game. Builds happen only on the player's explicit "go", never while they may be in-game or testing.
- **Tests are safe anytime**: `dotnet test BalambGarden.Engine.Tests/BalambGarden.Engine.Tests.csproj` - the Engine has zero game dependency.
- Feature branches only; merge to main = save point (this doc's review rides every merge - the merge gate enforces it).
- Repo is quasi-public. The maintainer persona is **Drift** (GitHub: DriftlessDigits); no real names in code, comments, commits, or docs. No AI co-authorship lines in commits. A machine-local pre-commit name guard backs this up (installed per machine via personality-chip's `install-repo-hooks.sh`; untracked BY DESIGN - a tracked hook would ship the names it blocks).

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
5. **Verbs & UI** (`BalambGarden/Chains/` + `Windows/`) - `ChainBase`: paced dialogue chains (human tempo + jitter, occupied-player guard, clean stop with stated reason; stops land during human-wait steps). `TendChain`, `CycleChain` (harvest + replant beds), `PotChain` (pot cycle incl. full-auto plant, `WaterMany` = the pot Tend All - ripe pots excluded from the count, the live map's stage decides), with `PlantFlow`/`PlantFill` doing the read-and-click (no pacing, no ledger writes). `MainWindow` = the dashboard (tab per covered estate, patch strips, exception-first water column; Tend All names the patches it covers). `RunLogWindow`; `ConfigWindow`.

**`EstatePort` (`Game/EstatePort.cs`) - estate teleports** (Scrooge PortOrchestrator port). THE LAW: `Fire`'s only caller is a button click - never a timer, event, or transition. Cast gate = `ActionManager.GetActionStatus` on action 5 (the game's own refusal reasons). `UpdateAetheryteList` before every read - a session that never opened the teleport window holds an empty list and `Teleport()` on it fails SILENTLY. And the call is gated on `Player.Available`: `Telepo.Instance()` SURVIVES the zone-transition player teardown, so it is not a sufficient null gate - an ungated `UpdateAetheryteList` from the draw loop mid-loading-screen crashed the client (2026-08-23). The refresh skip does not consume the throttle, so the first frame back gets a fresh list. Estate join = `TeleportInfo.HouseId` decomposed against the census `EstateKey` with EstateSensor's corroboration idiom (exact district; ward/plot off-by-one tolerance - HouseId's 0/1-based convention is unconfirmed) and the one-survivor-or-no-button rule. Room shapes match their plot: the teleport lands at the estate entrance. Button disabled while any chain runs (a teleport would strand it).

**`CensusPump` (`Game/CensusPump.cs`) is the heartbeat**: sensors read, receipts route, the ledger learns. `RefreshDisplayOnly` is the UI-read-without-recording path (post-run settle poll - do not add observations from it). Both the new-estate arrival AND the front-door inside/outside flip retry their map read until the side underfoot answers non-empty when the ledger expects content there - the DataMap populates a beat after any zone line, and a one-shot read that races it stands stale for the whole visit (2026-08-23: every FC pot record read "harvested?" against an empty flip read). `ReconcileAbsentPots`: a vanished pot entry = harvest happened unseen - but the verdict needs corroboration, because DataMap-vs-object-table settle order is unreceipted and a wrong verdict Rebase-clears real anchors: settled objects seen, map answered non-empty this visit, a prior settled read exists, and the key absent from BOTH consecutive settled reads (union spares either). Named cost: an estate harvested bare between visits keeps its amber "harvested?" rows rather than auto-clearing.

## Sync groundwork (built on `sync-engine`, 2026-08-23; relay not yet built)

**The receipt lifecycle - two-phase, ratified** (vault: `Balamb Garden - Receipt Lifecycle - Design.md`, rulings 1-6). A chain's act OPENS a pending receipt; the game's own chat line CLOSES it. One receipt, two witnesses: the chain contributes armed identity, the game contributes the fact it happened.

- **Open at the ACT, never at delivery** (ruling 4). Every chain calls `CensusPump.OpenAtAct` at its commit moment - the option click, the sow Yes. Opening at delivery is the field-receipted 17:28 double-receipt bug: the line lands during the map settle, finds no pending, and the manual branch fires a second entry. The one exception is the diff-bind pot path (`_potKey` null): identity is born from the map diff AFTER the act, so it cannot open early and degrades honestly (late open, already delivered, closes chain-unconfirmed).
- **Close** = `CensusPump.TryCloseChain` on a classified line (same verb + container within `ReceiptLifecycle.CloseWindow` 6s) -> history entry, source `chain`. The desk banks a `RecentClose` memo so a delivery arriving AFTER its line already closed (`DeliveryAlreadyClosed`) does not re-open a duplicate - both race orderings field-proven 2026-08-23.
- **Timeout** (`SweepPending`): delivered but no line -> `chain-unconfirmed`, WARN, written (the count is the instrument that would earn the chat-as-primary rethink). Never delivered AND no line -> CANCEL, logged, nothing written (ruling 6: two absent witnesses = no fact).
- **Manual acts = the unclaimed close** (ruling 2): a recognised line with no pending and no chain driving becomes its own receipt via the chat-identity ladder, source `chat`. While `Plugin.AnyChainDriving`, the manual branch is CLOSED (ruling 5: one character, one pair of hands - the line is logged and dropped).

**`ChatReceipts` (`Game/ChatReceipts.cs`) - the closing half. NOT debug-gated; it ships.** Passive listener per the ObtainWatch law. Classification is a pure Engine function (`Engine/Sync/ChatLine.cs`): structure first - success = SystemMessage/57 + src=LocalPlayer, refusal = ErrorMessage/60 (never receipted) - then EXACT text match ("You spent 303 gil." rides the same structure; exact-match is load-bearing). Catalog: bed fertilize / pot fertilize / pot plant / bed tend / pot water (pot "Tend Crop" IS watering). NOT captured yet: harvest, bed plant - the self-capture instrument (`NoteUnmatched`) logs any success-shaped unrecognised line while a pending is open as a candidate close text; that is how the missing rows get found. Manual identity = PREVIOUS-target at line time, defended fail-closed: kind-vs-object cross-check, plus beds-only species corroboration (`FertilizeCorroboration`, 10s window) against the crop-name inspect line (57 + src=None; FIRST LINE ONLY - the message carries a second payload behind a newline, and the whole TextValue never resolves).

**Identity: wire id = `user:era:counter`.** `user` = stable slug in Configuration (mine-vs-external forever). `era` = volatile slug in the receipt log, names one counter-life; re-minted by `EraGuard` on any of three triggers: counter rewind, fresh log, machine-fingerprint change (covers the clone/migration class a rewind check can't see). `counter` = `ReceiptLog` Seq, monotonic per era. `MachineStamp` = SHA256 of registry MachineGuid, memoized, **LOCAL ONLY - it never travels**; stored hashed in Configuration. Accepted lossy edge: full wipe = new user id, own history replays as external.

**`ReceiptLog` (`receipts.json`, via `GardenService.Receipts`)** - append-only, doubles as the sync outbox (`LastSentSeq` high-water mark; outbox scoped to the current era). Entries carry kind/source/estate/identity/data; entity pseudonyms (HouseholdKeys HMAC) mint at SEND time only - the log itself is local-plaintext. Append-only is law: a duplicate produced by a since-fixed bug stays (nothing sent), it is never edited out.

**Wire types** (`Engine/Sync/SyncReceipt.cs` + `HouseholdKeys.cs`): envelope per the ratified schema doc; enum words via `WireWordPolicy` - kebab-case default with a CLOSED pin table (`potwater`, `potwilt` stay ratified single words). `chain-unconfirmed` is a source word and must be in the relay's accepted vocabulary before the relay ships. Flagged unruled for the send path: `NearestPatchId` 0 (honest-unknown patch id) - refuse-at-send vs resolve-at-send undecided.

**Build stamp**: `StampBuildInfo` csproj target embeds git commit/branch/BuildTime as AssemblyMetadata (BuildTime changes every build, which defeats incremental staleness); `BuildStamp.cs` reads it; shown on the Recon tab and logged at plugin load. "Am I on the current build" is now a read, not a guess.

## Storage (plugin config directory)

- `ledger-v2.json` - the LedgerStore. **Fail closed**: an unreadable ledger is parked as `.unreadable-<stamp>`, never overwritten. `LedgerMigration.NormalizeEstates` runs idempotently at load (pre-08-15 files held one physical plot as two records).
- `trail.jsonl` - append-only receipt trail (evidence, not memory).
- `receipts.json` - the `ReceiptLog` (history + sync outbox; see Sync groundwork). Same fail-closed park-on-unreadable rule as the ledger.
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
- Game-state singletons outlive their contents: `Telepo.Instance()` is non-null while the local player is destroyed mid-zone - gate native calls on `Player.Available`, not on the instance pointer.
- One-shot reads of settle-async state (DataMap, object table, teleport list) are race bugs that test clean - every such read needs a retry-until-answered or corroboration pattern, and "worked in the field lap" is not evidence against a race.

## Release

The runbook is `Book Of Holding/Reference/Runbooks/Balamb Release.md` - run it, don't improvise. The short shape: version lives in `BalambGarden/BalambGarden.csproj` (`<Version>`), user-facing changelog in `BalambGarden/BalambGarden.json` (`Changelog`); bump both on the feature branch, merge --no-ff to main, tag `vN.N.N.N`, push tag. **The tag push triggers `.github/workflows/create_release.yml`** (thin caller into DalamudPluginRepo's reusable workflow, same as Scrooge): CI builds Release, creates the GitHub release with `latest.zip`, and pushes the version-stamped manifest to the plugin repo, whose pluginmaster regenerates itself (never hand-edit it). Requires the `PAT` secret on this repo. Dev loads read `images/icon.png` beside the DLL; `IconUrl` is fetched only for repo installs; the game caches the icon image across restarts.

## Debug-only instrumentation

`ReconProbe` (debug builds): raw hex dumped verbatim beside every decode so a decoder bug is visible, not hidden. `PlantFlow` watcher is `#if DEBUG`. `ChatRecon` (`Game/ChatRecon.cs`, whole file `#if DEBUG`): passive chat listener for manual-action capture - per line: LogKind, source/target relation kinds, sender, verbatim text, payload shape, and hard/soft/previous target decoded to bed gimmick or pot key. Manual toggle on the Recon tab ("Watch chat"); survives zone lines; never handles or suppresses a message. Key capture receipts (2026-08-23, `captures/2026-08-23-fertilize-recon.log`): manual-verb success lines are SystemMessage/57 with src=LocalPlayer, refusals are ErrorMessage/60 with src=None (wording varies, structure does not); the acted object rides PREVIOUS-target at line time (hard target is cleared by then).
