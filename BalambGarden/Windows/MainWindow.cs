using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BalambGarden.Chains;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using BalambGarden.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace BalambGarden.Windows;

/// <summary>
/// The dashboard, modelled on Scrooge's Gil Dashboard: one verdict line about the whole
/// garden above the bar, then a tab per place. An estate's tab splits into the Outdoor
/// section (patches, strips, verbs, the cycle launcher) and the Indoor section (pots), and
/// a section only exists when it has something in it - an estate you have never been
/// inside grows no Indoor half rather than an empty one.
///
/// <para>The tab for the estate you are standing on selects itself once, on arrival, and
/// then leaves your clicking alone. Apartments and private rooms have no yard, so their tab
/// is indoor-only; both are real estates the sensor mints from live HouseId receipts
/// (08-15), and a private room is its own tab rather than a corner of its house.</para>
///
/// <para>Hierarchy still comes from space and brightness rather than chrome: full
/// brightness is reserved for the few things that matter now (ripe, danger, a refusal),
/// ages and counts sit at TextDisabled, and colour is semantic only. The patch strip is
/// the one bold element - eight cells, one per bed - and the grid and tooltips under it
/// remain the reading that carries the claim, so colour is never the only channel.</para>
/// </summary>
public class MainWindow : Window, IDisposable
{
    private static readonly Vector4 Green = new(0.4f, 1f, 0.4f, 1f);
    private static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Amber = new(1f, 0.78f, 0.35f, 1f);
    private static readonly Vector4 Dim = new(0.55f, 0.55f, 0.55f, 1f);

    /// <summary>The one sentence every untracked, sensor-only surface says - word for word,
    /// everywhere it applies, so it is learned once and then recognised. Presence and
    /// tracking are two different signals: "in reach" means you can act on this right now
    /// and never means it is yours, and a row with no ledger behind it carries no data at
    /// all. On rostered ground a sighting records it (08-15), so what is left untracked is
    /// a thing whose identity has not been matched, not a thing awaiting permission.</summary>
    private const string UntrackedTag = "not identified yet - Balamb hasn't matched this to the game's own records";

    // Strip geometry. Cells are one text line tall so a strip sits on the row it labels.
    private const float CellGap = 3f;
    private const float WaterBarHeight = 2.5f;

    private readonly Plugin plugin;

    // Cycle launcher state: which patch's panel is open and its editable plan.
    private (EstateKey Estate, int Ordinal)? cyclePatch;
    private ReplantPlan? cyclePlan;

    // Nickname editing: one estate at a time, written back on deactivation.
    private EstateKey? renaming;
    private string renameBuffer = "";

    // Relabel-not-modal arming. One no-undo button may be hot at a time, and any other
    // click in the window cools it - a press that cannot be undone should never be
    // waiting patiently for a stray second click minutes later.
    private string? armedButton;
    private bool armedTouchedThisFrame;

    // The inline Plant panel: which pot's panel is open (by map key when it has one,
    // else by entity id negated to avoid collision), and its order form. 0 = "whatever
    // I pick in game" - the picker stays the player's and the chain only verifies.
    private long? plantPanelPot;
    private bool plantPanelCycle;
    private uint plantSoilId;
    private uint plantSeedId;

    // A sweep's last refusal, shown briefly beside the button that was pressed (owner
    // says which one): a refusal never starts a run, so it has no chain report to live in.
    private string sweepNotice = "";
    private string sweepNoticeOwner = "";
    private DateTime sweepNoticeUntil;

    // Arrival selection. lastHere is where we were on the previous frame; when it changes,
    // the new estate's tab takes the selection for exactly one frame. Selecting every
    // frame would fight the player every time they clicked another tab.
    private EstateKey? lastHere;
    private EstateKey? selectOnce;

    public MainWindow(Plugin plugin)
        : base("Balamb Garden##BalambGardenMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;

        // The dashboard is the front door; settings live behind the cog (Scrooge's idiom).
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = Dalamud.Interface.FontAwesomeIcon.Cog,
            IconOffset = new Vector2(2, 1),
            Click = _ => plugin.ToggleConfigUi(),
            ShowTooltip = () =>
            {
                ImGui.BeginTooltip();
                ImGui.Text("Balamb Garden settings");
                ImGui.EndTooltip();
            },
        });
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (!Plugin.PlayerState.IsLoaded)
        {
            ImGui.Text("Not logged in.");
            return;
        }

        var here = EstateSensor.Current();
        var now = DateTimeOffset.UtcNow;

        armedTouchedThisFrame = false;

        if (here != lastHere)
        {
            selectOnce = here;
            lastHere = here;
        }

        var estates = Plugin.Garden.Ledger.Estates.ToList();

        DrawVerdict(estates, now);
        DrawLocatorNotes(estates, here);

        ImGui.Spacing();
        using (var bar = ImRaii.TabBar("##BalambTabs"))
        {
            if (bar.Success)
            {
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

                DrawTipsTab(now);
#if DEBUG
                DrawReconTab();
#endif
            }
        }

        selectOnce = null;

        // Anything else the player clicked disarms the hot button.
        if (armedButton is not null && !armedTouchedThisFrame
            && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            armedButton = null;
    }

    // ------------------------------------------------------------------ verdict

    /// <summary>The Gil-Dashboard summary line, in garden terms: across every estate the
    /// ledger knows, the worst thing that wants a human - or, when nothing does, the next
    /// window instead of an invented errand. The Engine owns the sentence; this only prints
    /// it, and carries the provenance marker for any window it quoted.</summary>
    private static void DrawVerdict(List<EstateRecord> estates, DateTimeOffset now)
    {
        var verdict = Verdicts.ForGarden(
            estates, Plugin.Garden.Census.LedgerBeds, Plugin.Tables, Plugin.Garden.Wilt, now,
            w => WindowFormat.Coarse(w.Earliest.ToLocalTime(), w.Latest.ToLocalTime(), now.ToLocalTime()));

        ImGui.Text(verdict.Text);

        if (verdict.Window is not { } window)
            return;

        ImGui.SameLine();
        ImGui.TextDisabled(WindowFormat.Mark(window.Provenance));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(WindowTooltip(window));
    }

    /// <summary>The hover behind every day-part window: the exact range the model holds
    /// (precision ruling 2026-08-16 - the surface speaks in day-parts, the hover keeps
    /// the hours) plus what kind of claim the marker is making.</summary>
    private static string WindowTooltip(EtaWindow window)
        => WindowFormat.Range(window.Earliest.ToLocalTime(), window.Latest.ToLocalTime())
           + $"\n{WindowFormat.MarkMeaning(window.Provenance)}";

    /// <summary>The spoken day-part window, clamped at now - and the "~" approx marker
    /// only while there is still a future range to approximate ("~any time now" would be
    /// hedging a hedge). The hover keeps the raw math either way.</summary>
    private static string SpokenWindow(EtaWindow w, DateTimeOffset now, bool approxMark = false)
    {
        var local = now.ToLocalTime();
        var phrase = WindowFormat.Coarse(w.Earliest.ToLocalTime(), w.Latest.ToLocalTime(), local);
        return approxMark && local < w.Earliest.ToLocalTime() ? "~" + phrase : phrase;
    }

    /// <summary>The two things the verdict cannot say: that no estate has a tab yet, and
    /// that we are standing somewhere the ledger has not finished writing down. Both are
    /// silent when they do not apply.</summary>
    private static void DrawLocatorNotes(List<EstateRecord> estates, EstateKey? here)
    {
        if (estates.Count == 0)
            // "Roster" now names the game's own grant list (08-15), so this line says
            // what it actually means: nothing has been written down yet.
            ImGui.TextDisabled("No estates recorded yet - stand in a garden the game grants you and it fills in.");
        else if (here is { } key && estates.All(e => e.Key != key) && CensusPump.CoveredHere)
            // The ledger writes an estate on arrival, but only where the roster covers us
            // (08-15). Unrostered ground never gets that write, so promising one here would
            // be a wait that never ends - its tab carries the display-only banner instead.
            ImGui.TextDisabled($"{key.DisplayLabel()} - reading the estate...");

        if (MapSensor.UnreadableCount > 0)
            ImGui.TextColored(Amber, $"{MapSensor.UnreadableCount} map entries here are unreadable");
    }

    /// <summary>A press with no undo: the button relabels itself and wants a second click
    /// (UI ruling 11 - relabel, never a modal). Returns true only on that second click.</summary>
    private bool ArmedButton(string key, string label, string sureLabel, bool small = false)
    {
        var armed = armedButton == key;
        var text = armed ? sureLabel : label;
        var pressed = small ? ImGui.SmallButton(text) : ImGui.Button(text);
        if (!pressed)
            return false;

        armedTouchedThisFrame = true;
        if (armed)
        {
            armedButton = null;
            return true;
        }

        armedButton = key;
        return false;
    }

    /// <summary>The one tooltip a busy chain owes every verb it greys out.</summary>
    private void BusyTip()
    {
        if (plugin.AnyChainBusy && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("a run is going - one chain at a time");
    }

    /// <summary>The one tooltip a verb owes when it is dead because the ground under it is
    /// not on the game's roster. Same sentence everywhere, like the untracked tag.</summary>
    private static void UnrosteredTip(bool actionable)
    {
        if (!actionable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Balamb doesn't act here - not on your teleport list");
    }

    // ------------------------------------------------------------------ estate tabs

    /// <summary>One place, one tab. Verbs only exist where the player is standing - objects
    /// do not travel - so every other tab is memory that reads but never acts.</summary>
    private void DrawEstateTab(EstateRecord record, EstateKey? here, DateTimeOffset now)
    {
        var isHere = record.Key == here;
        var flags = selectOnce is { } arrival && arrival == record.Key
            ? ImGuiTabItemFlags.SetSelected
            : ImGuiTabItemFlags.None;

        // Label is the nickname, identity is the key: renaming an estate must not read as
        // a different tab appearing.
        var label = $"{record.DisplayName}###{record.Key.BindingKey(0)}";
        using var tab = ImRaii.TabItem(label, flags);
        if (!tab.Success)
            return;

        using var id = ImRaii.PushId(record.Key.BindingKey(0));

        var beds = Plugin.Garden.Census.LedgerBeds.Where(b => b.Estate == record.Key).ToList();

        // Verbs only ever exist on the tab we are standing on, and standing somewhere the
        // game does not grant us is display-only (spec 2026-08-15): Balamb can SEE a
        // stranger's garden, it does not act in it and it does not keep it.
        var actionable = !isHere || CensusPump.CoveredHere;

        ImGui.Spacing();
        if (!actionable)
        {
            ImGui.TextColored(Amber, "not on your teleport list - Balamb doesn't track here");
            ImGui.TextDisabled("what you can see below is live sensing only; nothing is recorded");
        }

        DrawEstateHeader(record, beds, isHere, now);
        DrawEstateSections(record, beds, isHere, actionable, now);
    }

    private void DrawEstateHeader(
        EstateRecord record, List<ClaimedBed> beds, bool isHere, DateTimeOffset now)
    {
        if (isHere)
            ImGui.TextDisabled("you are here");
        else if (record.LastVisited == default)
            // A rostered estate we have never stood on: the grant is the whole content of
            // the tab, and an age computed off a default timestamp would be a lie in days.
            ImGui.TextDisabled("access granted - not visited yet");
        else
            // Memory says how old it is, every time. A count with no age is a count
            // pretending to be current.
            ImGui.TextDisabled(
                $"{beds.Count} recorded · last visited {WindowFormat.Ago(record.LastVisited, now)}");

        ImGui.SameLine();
        DrawRenameControl(record);
    }

    /// <summary>A nickname is the one piece of an estate the player authors. The button
    /// swaps itself for a field; the write lands when the field loses focus, so a
    /// half-typed name never becomes the estate's name.</summary>
    private void DrawRenameControl(EstateRecord record)
    {
        if (renaming == record.Key)
        {
            ImGui.SetNextItemWidth(160f);
            ImGui.InputText("##nickname", ref renameBuffer, 48);
            if (ImGui.IsItemDeactivated())
            {
                record.Nickname = renameBuffer.Trim();
                Plugin.Garden.Save();
                renaming = null;
            }
            return;
        }

        if (!ImGui.SmallButton("rename"))
            return;
        renaming = record.Key;
        renameBuffer = record.Nickname;
    }

    /// <summary>Outdoor and Indoor, each drawn only when it holds something. An estate with
    /// a yard you have walked and a living room you never have shows one section, not one
    /// section and an empty promise.</summary>
    private void DrawEstateSections(
        EstateRecord record, List<ClaimedBed> beds, bool isHere, bool actionable,
        DateTimeOffset now)
    {
        var rollups = Rollups.ForEstate(
            record.Key, Plugin.Garden.Census.LedgerBeds, Plugin.Tables, Plugin.Garden.Wilt, now);
        var outdoorRollups = rollups.Where(r => !r.IsPots).ToList();
        var potRollups = rollups.Where(r => r.IsPots).ToList();

        // An apartment or a private room is four walls with no yard, so its tab has an
        // Indoor half and nothing else - an Outdoor section there would be a promise about
        // a garden that cannot exist (Sam's ruling 08-15). Both shapes are live now: the
        // sensor mints them from the 08-15 HouseId receipts.
        var isIndoorOnly = record.Key.IsIndoorOnly;

        // Objects only exist where the player is standing. Everything else is memory, and
        // memory never grows a verb.
        var inside = isHere && EstateSensor.IsInside();
        var patches = isHere && !inside && !isIndoorOnly
            ? ObjectSensor.Patches()
            : new List<PatchGroup>();
        var pots = inside ? ObjectSensor.NearbyPots() : new List<PotObject>();

        var hasOutdoor = !isIndoorOnly && (outdoorRollups.Count > 0 || patches.Count > 0);
        var hasIndoor = potRollups.Count > 0 || pots.Count > 0;

        if (hasOutdoor)
        {
            SectionHeader("Outdoor");
            DrawOutdoorSection(record, outdoorRollups, patches, beds, isHere, actionable, now);
        }

        if (hasIndoor)
        {
            SectionHeader("Indoor");
            DrawIndoorSection(record, potRollups, pots, isHere, actionable, now);
        }

        if (hasOutdoor || hasIndoor)
            return;

        // A place you are standing in with nothing to show gets an invitation, not a row
        // of dead controls.
        ImGui.TextDisabled(isHere
            ? "Nothing recorded here yet - garden here once (or just stand near a known patch) and it appears."
            : "Nothing remembered here yet.");
    }

    private static void SectionHeader(string text)
    {
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            ImGui.Text(text);
        ImGui.Separator();
    }

    private void DrawOutdoorSection(
        EstateRecord record, List<PatchRollup> rollups, List<PatchGroup> patches,
        List<ClaimedBed> beds, bool isHere, bool actionable, DateTimeOffset now)
    {
        DrawUnclaimedLine(patches, beds);
        DrawTendAll(patches, actionable);

        foreach (var rollup in rollups)
            DrawRollupRow(record, rollup, patches, isHere, actionable, now);

        // A patch standing right there that the ledger has nothing for at all: it still
        // needs a row, or a patch whose identity has not been matched yet would be
        // unreachable from the window.
        foreach (var patch in patches.Where(p => rollups.All(r => r.PatchOrdinal != p.Ordinal)))
            DrawUnclaimedPatchRow(patch, actionable);
    }

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
                // The opener toggles - open, it reads as the way back out (the Cycle
                // (close) pattern), so "Plant" never appears twice for one pot.
                if (ImGui.SmallButton(plantPanelPot == PanelKey(pot) ? "Plant (close)" : "Plant..."))
                    TogglePlantPanel(pot);
            }
            BusyTip();
            UnrosteredTip(actionable);
            DrawPlantPanel(pot);
        }
    }

    private static void DrawUnclaimedLine(List<PatchGroup> patches, List<ClaimedBed> beds)
    {
        var sensed = patches.Sum(p => p.Beds.Count);
        var claimed = beds.Count(b => !b.IsPot);
        if (sensed <= claimed)
            return;

        ImGui.TextColored(Amber, $"{sensed - claimed} beds here are untracked");
        ImGui.TextDisabled(UntrackedTag);
    }

    /// <summary>Tend All, or - when nothing is in reach - the sentence that says what to
    /// do about it. A greyed-out "Tend All (0 beds, 0 patches)" is a dead control taking
    /// the best seat in the window; prose in its place actually helps.</summary>
    private void DrawTendAll(List<PatchGroup> patches, bool actionable)
    {
        if (patches.Count == 0)
            return;

        var inReach = patches.Where(p => p.InReach).ToList();
        if (inReach.Count == 0)
        {
            ImGui.TextDisabled("No beds in reach - walk to a patch.");
            return;
        }

        var totalBeds = inReach.Sum(p => p.Beds.Count);
        using (ImRaii.Disabled(plugin.AnyChainBusy || !actionable))
        {
            if (ImGui.Button($"Tend All ({totalBeds} beds, {inReach.Count} patches)"))
            {
                plugin.TendChain.TendAll(inReach);
                plugin.Launched(plugin.TendChain);
            }
        }

        BusyTip();
        UnrosteredTip(actionable);
    }

    // ------------------------------------------------------------------ rollups

    /// <summary>One patch as a line: name, the strip, then the counts. The strip is the
    /// census at a glance; the counts and the grid under it are the same census in words,
    /// which is the copy that carries the claim.</summary>
    private void DrawRollupRow(
        EstateRecord record, PatchRollup rollup, List<PatchGroup> patches,
        bool isHere, bool actionable, DateTimeOffset now)
    {
        using var id = ImRaii.PushId($"{(rollup.IsPots ? "pots" : "patch")}{rollup.PatchOrdinal}");

        var patch = rollup.IsPots
            ? null
            : patches.FirstOrDefault(p => p.Ordinal == rollup.PatchOrdinal);
        var beds = BedsOf(record.Key, rollup);

        ImGui.Spacing();

        // Ordinals are stored raw 0-based; +1 only in display strings.
        var title = rollup.IsPots ? "Pots" : $"Patch {rollup.PatchOrdinal + 1}";
        var open = ImGui.TreeNodeEx($"{title}###row");

        // Pots have no eight-slot shape (keyed by map key, one plant apiece), so their
        // strip is one cell per recorded pot rather than eight fixed slots.
        if (beds.Count > 0 || !rollup.IsPots)
        {
            ImGui.SameLine();
            DrawStrip(beds, rollup.IsPots, isHere, now);
        }

        ImGui.SameLine();
        DrawRollupSummary(rollup, now);

        if (rollup.IsPots && isHere)
            DrawPotVerbs(beds, actionable);

        if (patch is not null)
            DrawPatchVerbs(record, patch, actionable);

        if (open)
        {
            DrawBedGrid(record, rollup, beds, patch, isHere, actionable, now);
            ImGui.TreePop();
        }

        if (patch is not null && cyclePatch == (record.Key, rollup.PatchOrdinal))
            DrawCyclePanel(patch);
    }

    /// <summary>The beds behind one rollup row. A pot rollup is the whole estate's pots
    /// (Rollups.PotsOrdinal), so pots ignore the ordinal entirely and order by map key -
    /// the one number a pot actually has.</summary>
    private static List<ClaimedBed> BedsOf(EstateKey estate, PatchRollup rollup)
    {
        var beds = Plugin.Garden.Census.LedgerBeds
            .Where(b => b.Estate == estate && b.IsPot == rollup.IsPots);
        return rollup.IsPots
            ? beds.OrderBy(b => b.MapKey).ToList()
            : beds.Where(b => b.PatchOrdinal == rollup.PatchOrdinal)
                  .OrderBy(b => b.BedSlot).ToList();
    }

    /// <summary>The counts, quiet by default. Only the two things that want a decision now
    /// - ripe and thirst - come off TextDisabled, and thirst goes red only when the wilt
    /// clock is actually in the danger band.</summary>
    private static void DrawRollupSummary(PatchRollup rollup, DateTimeOffset now)
    {
        ImGui.TextDisabled(rollup.IsPots
            ? $"{rollup.Claimed} recorded"
            : $"{rollup.Claimed}/{PatchStrip.Slots}");

        // The steady state lives here, once per patch, instead of in a column that repeats
        // it per bed: "all watered" is only sayable when nothing is thirsty AND nothing is
        // unjudgeable, so it is a claim about the whole patch rather than a shrug.
        var thirsty = rollup.Due + rollup.Overdue + rollup.Danger;
        if (!rollup.IsPots && rollup.Claimed > 0 && thirsty == 0 && rollup.Unknown == 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("· all watered");
        }

        if (thirsty > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(rollup.Danger > 0 ? Red : Amber, $"· {thirsty} thirsty");
        }

        if (rollup.Ripe > 0)
        {
            ImGui.SameLine();
            ImGui.Text($"· {rollup.Ripe} ripe");
        }

        if (rollup.Unknown > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"· {rollup.Unknown} unknown");
        }

        // The collapsed line answers "when next" with the EARLIEST species only - every
        // bed row under it carries its own window now, so the full per-species answer
        // lives one click away instead of stretching the rollup across the window
        // (08-16 Sam: "the time estimates make this very wide"). The other species are
        // one hover away, never hidden.
        if (rollup.RipeBySpecies.FirstOrDefault() is { } next)
        {
            ImGui.SameLine();
            var range = SpokenWindow(next.Window, now, approxMark: true);
            ImGui.TextDisabled($"· {Plugin.Tables.SpeciesName(next.SpeciesIndex)} {range}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(WindowTooltip(next.Window));
            ImGui.SameLine();
            ImGui.TextDisabled(WindowFormat.Mark(next.Window.Provenance));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(WindowTooltip(next.Window));

            var rest = rollup.RipeBySpecies.Skip(1).ToList();
            if (rest.Count > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"+{rest.Count} more");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(string.Join("\n", rest.Select(s =>
                        $"{Plugin.Tables.SpeciesName(s.SpeciesIndex)} "
                        + SpokenWindow(s.Window, now, approxMark: true)
                        + $" {WindowFormat.Mark(s.Window.Provenance)}")));
            }
        }
    }

    // ------------------------------------------------------------------ patch strip

    /// <summary>Eight cells, one per bed slot: fill says stage, the under-bar says the bed
    /// wants water. Nothing is drawn for a state that means nothing (a watered bed, a pot,
    /// a slot we cannot judge) - a bar that is always there is a bar nobody reads.</summary>
    private static void DrawStrip(List<ClaimedBed> beds, bool isPots, bool isHere, DateTimeOffset now)
    {
        var cells = isPots
            ? PatchStrip.ForPots(beds, WiltingPotKeys())
            : PatchStrip.ForPatch(beds, Plugin.Tables, Plugin.Garden.Wilt, now);
        var draw = ImGui.GetWindowDrawList();
        var side = ImGui.GetTextLineHeight();
        var size = new Vector2(side, side);

        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0f, CellGap);

            var cell = cells[i];
            var bed = isPots
                ? beds.FirstOrDefault(b => b.MapKey == cell.Slot)
                : beds.FirstOrDefault(b => b.BedSlot == cell.Slot);
            var drifted = bed is not null && ReadsEmptyNow(bed, isHere);

            // An invisible button owns the rect: it reserves the layout space AND gives
            // the cell a hover target, so the drawlist only has to paint inside it.
            ImGui.InvisibleButton($"cell{cell.Slot}", size);
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();

            draw.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(CellFillColor(cell, drifted)), 2f);
            draw.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(CellOutlineColor(cell, drifted)), 2f);

            if (WaterBar(cell.Water) is { } bar)
                draw.AddRectFilled(
                    new Vector2(min.X + 1f, max.Y - WaterBarHeight),
                    new Vector2(max.X - 1f, max.Y),
                    ImGui.ColorConvertFloat4ToU32(bar));

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(CellTooltip(cell, bed, isPots, drifted, now));
        }
    }

    /// <summary>Map keys of pots the live map says are wilting RIGHT NOW (b4=1) - the only
    /// water claim a pot cell makes is the game's own, observed, never predicted.</summary>
    private static HashSet<int> WiltingPotKeys()
        => CensusPump.LastIndoor
            .Where(kv => kv.Value.Wilt == 1)
            .Select(kv => kv.Key)
            .ToHashSet();

    private static Vector4 CellFillColor(StripCell cell, bool drifted)
    {
        if (drifted)
            return new Vector4(0.22f, 0.17f, 0.09f, 1f);

        return cell.Fill switch
        {
            CellFill.Unclaimed => new Vector4(0.13f, 0.13f, 0.13f, 1f),
            CellFill.Unknown => new Vector4(0.30f, 0.30f, 0.30f, 1f),
            CellFill.Ripe => new Vector4(0.86f, 0.72f, 0.26f, 1f),
            _ => GrowShade(cell.Stage),
        };
    }

    /// <summary>Stage 0-3 as a dark-to-bright green ramp. The stage number is in the grid
    /// and in this cell's own tooltip, so the shade is a summary and never the claim.</summary>
    private static Vector4 GrowShade(byte stage)
    {
        var t = Math.Clamp(stage / 3f, 0f, 1f);
        return new Vector4(0.13f + 0.10f * t, 0.28f + 0.44f * t, 0.16f + 0.12f * t, 1f);
    }

    private static Vector4 CellOutlineColor(StripCell cell, bool drifted)
    {
        if (drifted)
            return Amber;
        return cell.Fill == CellFill.Ripe
            ? new Vector4(1f, 0.88f, 0.45f, 1f)
            : new Vector4(0.32f, 0.32f, 0.32f, 1f);
    }

    /// <summary>Water only paints when it is asking for something. Watered, unknown and
    /// not-applicable all paint nothing - three different silences the text surfaces
    /// already tell apart.</summary>
    private static Vector4? WaterBar(WaterState state) => state switch
    {
        WaterState.Due => Amber,
        WaterState.Overdue => Amber,
        WaterState.Danger => Red,
        _ => null,
    };

    /// <summary>A pot's pigment lives in the live map read (b2 high nibble, 08-16), never
    /// the ledger: a pot in front of us can say "Blue Lupins"; a remembered one says
    /// "Lupins". Only receipted pigment names render - an unnamed nibble is no prefix,
    /// not a guess.</summary>
    private static string PlantLabel(ClaimedBed bed, Observation latest)
    {
        var name = Plugin.Tables.SpeciesName(latest.SpeciesIndex);
        return bed.IsPot
               && CensusPump.LastIndoor.TryGetValue(bed.MapKey, out var live)
               && live.SpeciesIndex == latest.SpeciesIndex
               && PotPigment.Name(live.Color) is { } color
            ? $"{color} {name}"
            : name;
    }

    /// <summary>The bed's whole line, on hover. The strip is a picture; this is the
    /// sentence behind it, and it says the same thing the grid row says.</summary>
    private static string CellTooltip(
        StripCell cell, ClaimedBed? bed, bool isPots, bool drifted, DateTimeOffset now)
    {
        // A pot's Slot IS its map key; a bed's is 0-based and displays +1.
        var name = isPots ? $"Pot {cell.Slot}" : $"Bed {cell.Slot + 1}";

        if (bed is null)
            return $"{name}: {UntrackedTag}";
        if (drifted)
            return $"{name}: reads empty now - replanted without me?";

        var latest = bed.Latest;
        if (latest is null)
            return $"{name}: recorded, nothing seen in it yet";

        var line = $"{name}: {Plugin.Tables.SpeciesName(latest.SpeciesIndex)}"
                   + $"\nstage {latest.Stage} · seen {WindowFormat.Ago(latest.At, now)}";
        line += isPots
            ? cell.Water == WaterState.Danger ? "\nwilting - water now" : ""
            : $"\nwater {WindowFormat.Water(cell.Water)}";

        if (latest.Stage >= 4)
            return $"{line}\nripe now";

        if (Plugin.Tables.GrowHours(latest.SpeciesIndex) is not { } growHours
            || StageModel.RipeWindow(bed.Ring, growHours) is not { } window)
            return line;

        var range = WindowFormat.Range(
            window.Earliest.ToLocalTime(), window.Latest.ToLocalTime());
        return $"{line}\nripe ~{range} {WindowFormat.Mark(window.Provenance)}"
               + $"\n{WindowFormat.MarkMeaning(window.Provenance)}";
    }

    // ------------------------------------------------------------------ patch verbs

    /// <summary>The verbs for a patch, on their own indented line under it. Out of reach
    /// there are no buttons at all - the distance and what to do about it is the whole
    /// content of that line.</summary>
    private void DrawPatchVerbs(EstateRecord record, PatchGroup patch, bool actionable)
    {
        using var indent = ImRaii.PushIndent();

        if (!patch.InReach)
        {
            ImGui.TextDisabled($"{patch.Distance:F1}y away - walk closer to tend it");
            return;
        }

        using (ImRaii.Disabled(plugin.AnyChainBusy || !actionable))
        {
            if (ImGui.SmallButton("Water Patch"))
            {
                plugin.TendChain.TendPatch(patch);
                plugin.Launched(plugin.TendChain);
            }

            // The pots' one-press sweep, outdoors (08-16 Sam: "make outdoor match").
            // Same gate as the pot row: it exists only when something ripe is here to act
            // on. Beds still growing are left out of the plan, not a reason to refuse.
            var anyRipe = Plugin.Garden.Census.LedgerBeds.Any(b =>
                b.Estate == record.Key && !b.IsPot && b.PatchOrdinal == patch.Ordinal
                && b.Latest?.Stage == 4);
            if (anyRipe)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Replant ripe"))
                    LaunchPatchReplant(record, patch);
                if (actionable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Harvest + replant every ripe bed with the same crop."
                        + "\nSeeds from the ledger, topsoil from your bags. Beds still"
                        + "\ngrowing are left alone.");
            }

            ImGui.SameLine();
            var openHere = cyclePatch == (record.Key, patch.Ordinal);
            if (ImGui.SmallButton(openHere ? "Cycle (close)" : "Cycle..."))
            {
                if (openHere)
                {
                    cyclePatch = null;
                }
                else
                {
                    cyclePatch = (record.Key, patch.Ordinal);
                    cyclePlan = ReplantPlan.DefaultFor(record.Key, patch.Ordinal);
                }
            }
        }

        BusyTip();
        UnrosteredTip(actionable);
        DrawSweepNotice($"patch{patch.Ordinal}");
    }

    /// <summary>The patch twin of the pot sweep, one press: same-as-harvested plan off
    /// the ledger, ripe-or-empty beds only (a bed mid-growth is left out, exactly as the
    /// pot sweep leaves unripe pots alone - never a refusal), topsoil from the bags under
    /// the same "the bag is the plan" rule. Everything else is the existing patch cycle:
    /// CycleChain's own pre-flight still guards the run, so a shortage refuses in the
    /// player's terms before the first click.</summary>
    private unsafe void LaunchPatchReplant(EstateRecord record, PatchGroup patch)
    {
        var owner = $"patch{patch.Ordinal}";

        // The freshest possible read: ripeness decides which beds are in, same as pots.
        CensusPump.SightNow();

        var plan = ReplantPlan.DefaultFor(record.Key, patch.Ordinal);
        if (Plugin.Garden.Census.BoundKey(record.Key, patch.Ordinal) is { } mapKey
            && CensusPump.LastOutdoor.TryGetValue(mapKey, out var readings))
        {
            foreach (var slot in plan.Seeds.Keys.ToList())
            {
                if (readings.FirstOrDefault(r => r.Slot == slot) is { Occupied: true, Stage: < 4 })
                    plan.Seeds.Remove(slot);
            }
        }

        // DefaultFor pre-fills FIRST topsoil found - right for a panel a human confirms,
        // not for a press nobody reviews. The one-press path holds the stricter rule.
        var bags = new BagInventory();
        var soils = Plugin.Tables.Soils
            .Select(s => (s.ItemId, s.Name, Count: bags.CountOf(s.ItemId)))
            .Where(s => s.Count > 0)
            .ToList();
        if (soils.Count == 0)
        {
            SweepNotice(owner, "no topsoil in bags - nothing to replant with");
            return;
        }

        if (soils.Count > 1)
        {
            SweepNotice(owner, "more than one topsoil in bags ("
                + string.Join(", ", soils.Select(s => s.Name)) + ") - use Cycle... to pick one");
            return;
        }

        plan.SoilItemId = soils[0].ItemId;
        if (CycleChain.PreflightError(patch, plan) is { } refusal)
        {
            SweepNotice(owner, refusal);
            return;
        }

        plugin.CycleChain.Run(patch, plan);
        plugin.Launched(plugin.CycleChain);
    }

    /// <summary>A patch in front of you with nothing recorded in it. No rollup can exist
    /// for it (rollups read the ledger), but a verb has to, or nothing here is reachable.</summary>
    private void DrawUnclaimedPatchRow(PatchGroup patch, bool actionable)
    {
        using var id = ImRaii.PushId($"unclaimed{patch.PatchId}");

        ImGui.Spacing();
        ImGui.TextDisabled($"Patch {patch.Ordinal + 1} · {patch.Beds.Count} beds here");
        ImGui.TextDisabled(UntrackedTag);

        using var indent = ImRaii.PushIndent();
        if (!patch.InReach)
        {
            ImGui.TextDisabled($"{patch.Distance:F1}y away - walk closer");
            return;
        }

        using (ImRaii.Disabled(plugin.AnyChainBusy || !actionable))
        {
            if (ImGui.SmallButton("Water Patch"))
            {
                plugin.TendChain.TendPatch(patch);
                plugin.Launched(plugin.TendChain);
            }
        }

        BusyTip();
        UnrosteredTip(actionable);
    }

    // ------------------------------------------------------------------ bed grid

    private void DrawBedGrid(
        EstateRecord record, PatchRollup rollup, List<ClaimedBed> beds, PatchGroup? patch,
        bool isHere, bool actionable, DateTimeOffset now)
    {
        if (beds.Count == 0)
            return;

        // The object read for the whole grid, once. A pot row matches its object by map
        // key, so one sweep answers every row - a scan per row would be an object-table
        // walk per row per frame. It is swept before the table so the Plant panel below
        // can find its pot in the same read the rows used.
        List<PotObject> pots = rollup.IsPots && isHere && EstateSensor.IsInside()
            ? ObjectSensor.NearbyPots()
            : [];

        // The verbs column only exists when some row will actually put a button in it -
        // out of reach, a declared-but-empty fifth column reads as a ghost stripe down
        // the table's edge (08-16 Sam: "why is there a weird empty column"). Drift rows
        // always carry Forget, so any drift forces the column too.
        var hasVerbs = beds.Any(b => ReadsEmptyNow(b, isHere))
            || pots.Any(p => p.InReach)
            || (patch?.Beds.Any(b => b.InReach) ?? false);

        // SizingFixedFit: every column hugs its own content and the table is exactly as
        // wide as its data - no stretch arithmetic to blow a column past the window edge
        // (08-15: one stretch column ate an ultrawide and shoved Stage/Ripe offscreen).
        // A wider window is quiet space on the right, and nothing ever clips.
        // NoHostExtendX: without it the OUTER width still fills the window, so the last
        // column drags every row's stripe to the far edge as one long empty tail.
        using (var table = ImRaii.Table($"beds{rollup.PatchOrdinal}", hasVerbs ? 5 : 4,
                   ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit
                   | ImGuiTableFlags.NoHostExtendX))
        {
            if (!table.Success)
                return;

            ImGui.TableSetupColumn("Bed");
            ImGui.TableSetupColumn("Plant");
            ImGui.TableSetupColumn("Stage");
            ImGui.TableSetupColumn("Ripe");
            if (hasVerbs)
                ImGui.TableSetupColumn("##verbs");
            ImGui.TableHeadersRow();

            DrawBedRows(record, beds, pots, patch, isHere, actionable, hasVerbs, now);
        }

        // The Plant panel is a pair of 260f combos; a 150f verbs cell cannot hold it, so it
        // renders below the whole grid at full width - the way the cycle panel renders under
        // its patch row rather than inside it.
        if (!rollup.IsPots || plantPanelPot is null)
            return;

        // Only pots that own a ROW in this grid panel here - an unrecorded (empty) pot
        // draws its own panel beside its own line, and drawing it in both places put two
        // identical forms on screen at once (08-16 Sam: "confusing and clunky").
        var open = pots.FindIndex(p => PanelKey(p) == plantPanelPot
            && beds.Any(b => b.IsPot && b.MapKey == p.MapKey));
        if (open >= 0)
            DrawPlantPanel(pots[open]);
    }

    /// <summary>The rows themselves, inside the caller's table scope.</summary>
    private void DrawBedRows(
        EstateRecord record, List<ClaimedBed> beds, List<PotObject> pots, PatchGroup? patch,
        bool isHere, bool actionable, bool hasVerbs, DateTimeOffset now)
    {
        foreach (var bed in beds)
        {
            // Every pot in an estate rolls up together and they all sit at BedSlot 0, so
            // the map key is what makes a pot row's widgets its own.
            using var id = ImRaii.PushId(bed.IsPot ? bed.MapKey : bed.BedSlot);
            ImGui.TableNextRow();

            if (ReadsEmptyNow(bed, isHere))
            {
                DrawDriftRow(record, bed, pots, actionable);
                continue;
            }

            var bedObject = patch?.Beds.FirstOrDefault(b => b.Gimmick.BedIndex == bed.BedSlot);
            var latest = bed.Latest;
            var crop = latest is null ? null : Plugin.Tables.CropBySpeciesIndex(latest.SpeciesIndex);

            ImGui.TableNextColumn();
            ImGui.Selectable(bed.IsPot ? $"Pot {bed.MapKey}" : $"Bed {bed.BedSlot + 1}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("right-click to forget this record");
            DrawForgetMenu(record, bed);

            // Water is exception-first now (UI ruling 2026-08-15): a whole column that
            // mostly reads "watered" spends the grid's widest real estate on the state
            // nobody has to act on. Only a bed actually asking for water says anything
            // here; the steady state is one word on the rollup line above.
            //
            // Pots are OBSERVED, not predicted: the map itself says wilting (byte[4]=1,
            // 08-16 Papa's twins - pots DO wilt for normal crops; the flower no-wilt
            // evidence stands separately). No clock model, just what the game showed us.
            if (bed.IsPot)
            {
                if (CensusPump.LastIndoor.TryGetValue(bed.MapKey, out var livePot)
                    && livePot.Wilt == 1)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Amber, "· wilting - water now");
                }
            }
            else
            {
                var water = crop is null ? WaterState.Unknown
                    : Plugin.Garden.Wilt.StateFor(bed, crop, now);
                if (water is WaterState.Due or WaterState.Overdue or WaterState.Danger)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(water == WaterState.Danger ? Red : Amber,
                        water == WaterState.Danger ? "· DANGER - water now" : "· thirsty");
                }
            }

            if (bedObject is { InReach: true })
            {
                ImGui.SameLine();
                ImGui.TextColored(Green, "in reach");
            }

            ImGui.TableNextColumn();
            ImGui.Text(latest is null ? "?" : PlantLabel(bed, latest));

            // Staleness rides beside the numbers it qualifies: a stage read two days ago
            // is a different sentence from the same stage read a minute ago.
            ImGui.TableNextColumn();
            ImGui.Text(latest is null ? "?" : latest.Stage.ToString());
            if (latest is not null)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(WindowFormat.Ago(latest.At, now));
            }

            ImGui.TableNextColumn();
            DrawRipeCell(bed, crop, latest, now);

            if (hasVerbs)
            {
                ImGui.TableNextColumn();
                if (bed.IsPot)
                    DrawPotRowVerbs(bed, pots, actionable);
                else
                    DrawBedVerbs(bedObject, actionable);
            }
        }
    }

    /// <summary>A bed at stage 4 IS ripe - it is a sighting, not a forecast - so it says
    /// "ripe now" with the age of that sighting, and carries no provenance marker: there
    /// is no claim about the future left to qualify.</summary>
    private static void DrawRipeCell(
        ClaimedBed bed, Engine.Domain.Crop? crop, Observation? latest, DateTimeOffset now)
    {
        if (latest?.Stage == 4)
        {
            ImGui.Text("ripe now");
            ImGui.SameLine();
            ImGui.TextDisabled(WindowFormat.Ago(latest.At, now));
            return;
        }

        if (Plugin.Tables.GrowHours(latest?.SpeciesIndex ?? 0) is not { } growHours
            || StageModel.RipeWindow(bed.Ring, growHours) is not { } window)
        {
            ImGui.TextDisabled("?");
            return;
        }

        // Quiet steady state: a forecast repeated down eight rows is texture, not news -
        // it reads disabled, and only "ripe now" (above) is bright. No per-row provenance
        // marker either: the rollup line carries the claim once per species, and the
        // hover here keeps the exact hours plus what kind of claim it is.
        ImGui.TextDisabled(SpokenWindow(window, now));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(WindowTooltip(window));
    }

    /// <summary>Tend appears only for a bed that is actually in reach - and it is the only
    /// verb a bed row carries, because forgetting the record now lives on the row's name
    /// under a right-click rather than beside every Tend.</summary>
    private void DrawBedVerbs(BedObject? bedObject, bool actionable)
    {
        if (bedObject is not { InReach: true } target)
            return;

        using (ImRaii.Disabled(plugin.AnyChainBusy || !actionable))
        {
            if (ImGui.SmallButton("Tend"))
            {
                plugin.TendChain.TendOne(target);
                plugin.Launched(plugin.TendChain);
            }
        }

        BusyTip();
        UnrosteredTip(actionable);
    }

    /// <summary>The pot verbs row, shaped like a patch's (08-16 Sam: the sweep had
    /// drifted inline onto the rollup while every outdoor verb lives indented under its
    /// header). "Replant ripe" = harvest + replant every ripe pot with what it already
    /// grows, one press: seed from the species table (flowers carry the join like crops,
    /// 08-16), soil from the bags ("the bag is the plan"). Underivable pots are skipped
    /// by name in the run report; a refusal (no soil, two soils, no bag room) does
    /// nothing and says why beside the button.</summary>
    private void DrawPotVerbs(List<ClaimedBed> beds, bool actionable)
    {
        // In reach is the whole gate: standing outside, the indoor pot objects are not
        // in the object table at all, so the row honestly vanishes rather than offering
        // a press that cannot act (08-16 Sam: "don't show it if I'm not in range").
        var pots = ObjectSensor.AllPots();
        var ripeInReach = beds.Any(b =>
            b.IsPot && b.Latest?.Stage == 4
            && pots.FindIndex(p => p.MapKey == b.MapKey && p.InReach) >= 0);
        if (!ripeInReach)
            return;

        using var indent = ImRaii.PushIndent();
        using (ImRaii.Disabled(plugin.AnyChainBusy || !actionable))
        {
            // "Replant", not "Cycle": Cycle is the verb where you PICK the seed; this one
            // puts the same plant back. The header keeps the row buttons' vocabulary.
            if (ImGui.SmallButton("Replant ripe"))
                LaunchPotSweep(beds);
        }

        if (actionable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Harvest + replant every ripe pot with the same plant."
                + "\nSeed comes from its species, soil from your bags.");
        BusyTip();
        UnrosteredTip(actionable);

        DrawSweepNotice("pots");
    }

    private unsafe void LaunchPotSweep(List<ClaimedBed> beds)
    {
        // The freshest possible read: ripeness decides who is in the sweep, and a stale
        // stage would build the wrong queue (same rule as the patch cycle).
        CensusPump.SightNow();

        var pots = ObjectSensor.AllPots();
        var candidates = beds
            .Where(b => b.IsPot && b.Latest?.Stage == 4)
            .Select(b =>
            {
                var found = pots.FindIndex(p => p.MapKey == b.MapKey);
                return new PotCycleCandidate(b.MapKey, b.Latest?.SpeciesIndex,
                    found >= 0 && pots[found].InReach);
            })
            .ToList();

        var plan = PlanPotCycles(candidates);
        if (plan.Refusal is { } refusal)
        {
            SweepNotice("pots", refusal);
            return;
        }

        if (plan.Jobs.Count == 0)
        {
            SweepNotice("pots", plan.Skips.Count > 0 ? plan.Skips[0] : "nothing ripe to cycle");
            return;
        }

        var jobs = plan.Jobs
            .Select(j => (Pot: pots[pots.FindIndex(p => p.MapKey == j.Key)], j.SeedId))
            .ToList();
        plugin.PotChain.CycleMany(jobs, plan.SoilItemId, plan.Skips);
        plugin.Launched(plugin.PotChain);
    }

    /// <summary>One planner call for sweep and single-row Replant alike - same rules,
    /// same words, whichever button was pressed.</summary>
    private static unsafe PotCyclePlan PlanPotCycles(List<PotCycleCandidate> candidates)
    {
        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        var free = inventory == null ? 0 : (int)inventory->GetEmptySlotsInBag();
        var soils = SoilsInBag().Select(s => new BagSoil(s.ItemId, s.Name, s.Count)).ToList();
        return PotCyclePlanner.Plan(candidates, soils, new BagInventory(), free, Plugin.Tables);
    }

    private void SweepNotice(string owner, string text)
    {
        sweepNotice = text;
        sweepNoticeOwner = owner;
        sweepNoticeUntil = DateTime.UtcNow.AddSeconds(6);
    }

    private void DrawSweepNotice(string owner)
    {
        if (sweepNoticeOwner != owner || sweepNotice.Length == 0
            || DateTime.UtcNow >= sweepNoticeUntil)
            return;
        ImGui.SameLine();
        ImGui.TextColored(Amber, sweepNotice);
    }

    /// <summary>One pot's one-click cycle-with-itself: the sweep's rules applied to a
    /// single candidate. Anything the planner cannot derive opens the Cycle picker
    /// instead, with the reason shown - never a guess, never a dead click.</summary>
    private void ReplantOne(ClaimedBed bed, PotObject pot)
    {
        var plan = PlanPotCycles(
            [new PotCycleCandidate(bed.MapKey, bed.Latest?.SpeciesIndex, pot.InReach)]);

        if (plan.Jobs.Count == 1)
        {
            plugin.PotChain.Cycle(pot, plan.SoilItemId, plan.Jobs[0].SeedId);
            plugin.Launched(plugin.PotChain);
            return;
        }

        SweepNotice("pots", plan.Refusal
            ?? (plan.Skips.Count > 0 ? plan.Skips[0] : "could not derive a replant"));
        if (plantPanelPot != PanelKey(pot) || !plantPanelCycle)
            TogglePlantPanel(pot, cycle: true);
    }

    /// <summary>A pot row's verbs, lit only when the pot object itself is in reach.
    /// Identity is the object's own key (HousingFurnitureIndex, 08-16), so matching the
    /// row to the object is a lookup, not a diff. The list is swept once for the whole
    /// grid and handed down. No Plant here: an occupied pot cannot take a seed, so the
    /// verb would be a dead button (08-16 Sam: "like 4 different Plant buttons") - Plant
    /// lives on empty pots and on a pot row that reads empty (post-harvest).</summary>
    private void DrawPotRowVerbs(ClaimedBed bed, List<PotObject> pots, bool actionable)
    {
        // PotObject is a struct, so "not found" is an index of -1 rather than a null -
        // a default PotObject would read as a pot at 0y with no key.
        var found = pots.FindIndex(p => p.MapKey == bed.MapKey);
        if (found < 0)
        {
            ImGui.TextDisabled("-");
            return;
        }

        var pot = pots[found];
        if (!pot.InReach)
        {
            ImGui.TextDisabled($"{pot.Distance:F1}y");
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
                    "Sets the flower's colour, and clears wilt on a thirsty crop"
                    + "\n(both receipted 08-16 - pots DO wilt on normal crops).");
            UnrosteredTip(actionable);

            ImGui.SameLine();
            if (ImGui.SmallButton("Harvest"))
            {
                plugin.PotChain.Harvest(pot);
                plugin.Launched(plugin.PotChain);
            }
            UnrosteredTip(actionable);

            // Replant only exists where it can act: a ripe pot. Anywhere else the press
            // would just be a slower way to hit the harvest refusal.
            if (bed.Latest?.Stage == 4)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Replant"))
                    ReplantOne(bed, pot);
                if (actionable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Harvest, then replant the same plant - seed from its"
                        + "\nspecies, soil from your bags. If either can't be derived,"
                        + "\nthe Cycle picker opens instead.");
                UnrosteredTip(actionable);
            }

            ImGui.SameLine();
            var cycleOpen = plantPanelPot == PanelKey(pot) && plantPanelCycle;
            if (ImGui.SmallButton(cycleOpen ? "Cycle (close)" : "Cycle..."))
                TogglePlantPanel(pot, cycle: true);
            if (actionable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Harvest, then replant with the soil and seed you pick below.");
            UnrosteredTip(actionable);
        }

        BusyTip();
    }

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

    /// <summary>Drift: the ledger remembers a plant here, the map read a moment ago says
    /// the bed is empty. That is a sentence about the world, not a data point - so it
    /// replaces the row rather than corrupting it, and the only button is the honest one.</summary>
    private void DrawDriftRow(EstateRecord record, ClaimedBed bed, List<PotObject> pots, bool actionable)
    {
        var label = bed.IsPot ? $"Pot {bed.MapKey}" : $"Bed {bed.BedSlot + 1}";
        ImGui.TableNextColumn();
        ImGui.Text(label);
        ImGui.TableNextColumn();
        // A pot's entry vanishing is what a HARVEST looks like (08-15 receipt) - normal
        // life, not suspicion; a bed going empty behind our back is the odd one out.
        ImGui.TextColored(Amber, bed.IsPot
            ? $"{label} reads empty now - harvested?"
            : $"{label} reads empty now - replanted without me?");
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();

        // Replanting is exactly what an emptied pot wants next, so Plant lives here too.
        var potIndex = bed.IsPot ? pots.FindIndex(p => p.MapKey == bed.MapKey) : -1;
        if (potIndex >= 0 && pots[potIndex].InReach)
        {
            using (ImRaii.Disabled(plugin.AnyChainBusy || !actionable))
            {
                if (ImGui.SmallButton(plantPanelPot == PanelKey(pots[potIndex])
                        ? "Plant (close)" : "Plant..."))
                    TogglePlantPanel(pots[potIndex]);
            }
            BusyTip();
            UnrosteredTip(actionable);
            ImGui.SameLine();
        }

        var key = $"forget:{record.Key.BindingKey(bed.PatchOrdinal, bed.IsPot)}:{bed.BedSlot}";
        if (ArmedButton(key, "Forget record", "Forget - sure?", small: true))
        {
            Plugin.Garden.Census.Abandon(bed);
            Plugin.Garden.Save();
        }
    }

    /// <summary>True only when a fresh read of THIS estate's map shows the slot vacant.
    /// Away from the estate there is no read at all, and "I cannot see it" is never
    /// evidence that something is gone. Pots (08-16): the settled-object guard stands in
    /// for the outdoor TryGetValue - an unsettled world answers false, never "empty".</summary>
    private static bool ReadsEmptyNow(ClaimedBed bed, bool isHere)
    {
        if (!isHere)
            return false;
        if (bed.IsPot)
            return EstateSensor.IsInside()
                && ObjectSensor.SawHousingObjects
                && !CensusPump.LastIndoor.ContainsKey(bed.MapKey);
        if (!CensusPump.LastOutdoor.TryGetValue(bed.MapKey, out var readings))
            return false;
        return readings.FirstOrDefault(r => r.Slot == bed.BedSlot) is { Occupied: false };
    }

    // ------------------------------------------------------------------ cycle

    /// <summary>
    /// The cycle launcher: what will be replanted where, and a pre-flight line re-checked
    /// every frame. Nothing here is a modal - the launch button relabels itself and wants a
    /// second press, because a cycle spends seeds and a growth cycle and cannot be undone.
    ///
    /// <para>The soil and seed columns are the order form: the chain fills the game's picker
    /// with them and presses Confirm. When the picker isn't what the driver expects it stops
    /// clicking and you fill it by hand, exactly as before - the run does not end over it.
    /// Either way the confirmation is checked against this plan before anything is planted,
    /// which is now the check on our own fill as much as on yours.</para>
    /// </summary>
    private void DrawCyclePanel(PatchGroup patch)
    {
        if (cyclePlan is not { } plan)
            return;

        // The pre-flight reads stages off the map; keep that read fresh while the panel
        // is open (throttled inside), or the line would answer with arrival-time data.
        CycleChain.RefreshForPlanning();

        using var indent = ImRaii.PushIndent();

        var soilName = Plugin.Tables.SoilByItemId(plan.SoilItemId)?.Name ?? "(none chosen)";
        ImGui.SetNextItemWidth(260f);
        using (var combo = ImRaii.Combo("Soil", soilName))
        {
            if (combo.Success)
            {
                foreach (var soil in Plugin.Tables.Soils)
                {
                    var have = InventoryCount(soil.ItemId);
                    if (have == 0 && soil.ItemId != plan.SoilItemId)
                        continue;
                    if (ImGui.Selectable($"{soil.Name} ({have})", soil.ItemId == plan.SoilItemId))
                        plan.SoilItemId = soil.ItemId;
                }
            }
        }

        foreach (var slot in PlannableSlots(patch))
            DrawSeedCombo(plan, slot);

        var anchor = plan.AnchorTendRound;
        if (ImGui.Checkbox("Anchor tend round (tend every bed after planting)", ref anchor))
            plan.AnchorTendRound = anchor;

        var refusal = CycleChain.PreflightError(patch, plan);
        if (refusal is not null)
        {
            ImGui.TextColored(Red, refusal);
            if (armedButton == "cycle")
                armedButton = null;
        }

        using (ImRaii.Disabled(plugin.AnyChainBusy || refusal is not null))
        {
            if (ArmedButton("cycle",
                    $"Run cycle ({plan.Seeds.Count} beds)",
                    $"Run cycle: {plan.Seeds.Count} beds - sure?"))
            {
                plugin.CycleChain.Run(patch, plan);
                plugin.Launched(plugin.CycleChain);
                cyclePatch = null;
            }
        }
    }

    /// <summary>Every claimed bed in this patch can be planned, including one the plan
    /// defaulted past because the ledger has no species for it - the default declines to
    /// guess, but the player is allowed to say.</summary>
    private static IEnumerable<int> PlannableSlots(PatchGroup patch)
    {
        if (EstateSensor.Current() is not { } estate)
            return [];
        return Plugin.Garden.Census.LedgerBeds
            .Where(b => b.Estate == estate && !b.IsPot && b.PatchOrdinal == patch.Ordinal)
            .Select(b => b.BedSlot)
            .OrderBy(slot => slot)
            .ToList();
    }

    /// <summary>What goes back into one bed. "leave empty" is a real answer: a bed with
    /// no seed is simply not part of the cycle, harvested or not.</summary>
    private static void DrawSeedCombo(ReplantPlan plan, int slot)
    {
        using var id = ImRaii.PushId(slot);
        var chosen = plan.Seeds.GetValueOrDefault(slot);
        var label = chosen == 0
            ? "(leave empty)"
            : $"{Plugin.Tables.CropBySeedId(chosen)?.SeedName ?? $"seed {chosen}"} ({InventoryCount(chosen)} in bag)";

        ImGui.SetNextItemWidth(260f);
        using var combo = ImRaii.Combo($"bed {slot + 1}", label);
        if (!combo.Success)
            return;

        if (ImGui.Selectable("(leave empty)", chosen == 0))
            plan.Seeds.Remove(slot);

        foreach (var crop in Plugin.Tables.Crops.OrderBy(c => c.SeedName))
        {
            var have = InventoryCount(crop.SeedId);
            // Seeds you do not have are not offered - the pre-flight would only refuse
            // them a second later. The one already chosen stays visible either way.
            if (have == 0 && crop.SeedId != chosen)
                continue;
            if (ImGui.Selectable($"{crop.SeedName} ({have})", crop.SeedId == chosen))
                plan.Seeds[slot] = crop.SeedId;
        }
    }

    // ------------------------------------------------------------------ tips

    private static readonly Game.BagInventory BagReader = new();

    /// <summary>The pipeline reader's advisory lines, in their own tab. "(!)" only
    /// appears when a line wants reading - permanent decoration on a tab label is
    /// filler, and filler trains people to stop reading the panel that matters.</summary>
    private static void DrawTipsTab(DateTimeOffset now)
    {
        // Tips speak the names the tabs wear - a renamed estate is "Papa's Place" in
        // every sentence, never its ward-plot serial number.
        var tips = PipelineReader.Tips(Plugin.Garden.Census.LedgerBeds, Plugin.Tables, now,
            key => Plugin.Garden.Ledger.Estates.FirstOrDefault(e => e.Key == key)?.DisplayName
                   ?? key.DisplayLabel(),
            BagReader,
            (lo, hi) => WindowFormat.Coarse(lo.ToLocalTime(), hi.ToLocalTime(), now.ToLocalTime()));

        // The label stays plain (ruling 2026-08-16 rev: no badge at all - the tab is a
        // place you go, not a place that calls you). Attention still colors the tags
        // inside so the line worth reading is findable at a glance.
        const string label = "Tips###tips";
        using var tab = ImRaii.TabItem(label);
        if (!tab.Success)
            return;

        ImGui.Spacing();
        if (tips.Count == 0)
        {
            ImGui.TextDisabled("Nothing to flag right now.");
            return;
        }

        foreach (var tip in tips)
        {
            var tag = tip.Kind switch
            {
                TipKind.Stock => "[stock]",
                TipKind.Bottleneck => "[bottleneck]",
                _ => "[anomaly]",
            };
            if (tip.Attention)
                ImGui.TextColored(Amber, tag);
            else
                ImGui.TextDisabled(tag);
            ImGui.SameLine();
            ImGui.TextWrapped(tip.Text);
        }
    }

    // ------------------------------------------------------------------ pots

    /// <summary>Which pot a Plant panel belongs to: its map key when the furniture read
    /// named one, else its entity id negated so a keyed pot and an unkeyed one can never
    /// answer to the same number.</summary>
    private static long PanelKey(PotObject pot)
        => pot.MapKey is { } key ? key : -(long)pot.Object.EntityId;

    private void TogglePlantPanel(PotObject pot, bool cycle = false)
    {
        var key = PanelKey(pot);
        if (plantPanelPot == key && plantPanelCycle == cycle)
        {
            plantPanelPot = null;
            return;
        }
        plantPanelPot = key;
        plantPanelCycle = cycle;
        plantSoilId = 0;
        plantSeedId = 0;
    }

    /// <summary>The order form for one pot, open only while a Plant is being set up
    /// (UI ruling 2026-08-15: pickers exist only when a Plant press needs them). Soil is
    /// read live off the bags by name - there is no potting-soil table, on purpose (the
    /// full rationale lives on <see cref="SoilsInBag"/>). Naming soil/seed
    /// lets the chain fill the game's picker; leaving either on its default keeps those
    /// clicks the player's. The confirmation is checked against this form either way.</summary>
    private void DrawPlantPanel(PotObject pot)
    {
        if (plantPanelPot != PanelKey(pot))
            return;

        using var indent = ImRaii.PushIndent();

        // Real things first, the manual fallback last and in plain words (08-16 Sam:
        // "Whatever's in the picker" as the default face read as debug text). Leaving a
        // combo on the fallback keeps that slot's clicks the player's - same behavior,
        // said like a sentence.
        const string manualOption = "I'll pick at the game's menu";

        var soils = SoilsInBag();
        var chosenSoil = soils.FirstOrDefault(s => s.ItemId == plantSoilId);
        var soilLabel = plantSoilId == 0 || chosenSoil.ItemId == 0
            ? manualOption
            : $"{chosenSoil.Name} ({chosenSoil.Count} in bag)";
        ImGui.SetNextItemWidth(260f);
        using (var combo = ImRaii.Combo("Soil", soilLabel))
        {
            if (combo.Success)
            {
                foreach (var soil in soils)
                {
                    if (ImGui.Selectable($"{soil.Name} ({soil.Count} in bag)", soil.ItemId == plantSoilId))
                        plantSoilId = soil.ItemId;
                }
                if (ImGui.Selectable(manualOption, plantSoilId == 0))
                    plantSoilId = 0;
            }
        }

        var seedLabel = plantSeedId == 0
            ? manualOption
            : Plugin.Tables.CropBySeedId(plantSeedId)?.SeedName ?? PlantFlow.ItemName(plantSeedId);
        ImGui.SetNextItemWidth(260f);
        using (var combo = ImRaii.Combo("Seed", seedLabel))
        {
            if (combo.Success)
            {
                foreach (var crop in Plugin.Tables.Crops)
                {
                    var have = InventoryCount(crop.SeedId);
                    if (have == 0 && crop.SeedId != plantSeedId)
                        continue;
                    if (ImGui.Selectable($"{crop.SeedName} ({have} in bag)", crop.SeedId == plantSeedId))
                        plantSeedId = crop.SeedId;
                }
                // Flowerpot flower seeds: not in the crop table (outdoor data), so they
                // come off the bags by the game's own names - see ExtraSeedsInBag.
                foreach (var seed in ExtraSeedsInBag())
                {
                    if (ImGui.Selectable($"{seed.Name} ({seed.Count} in bag)", seed.ItemId == plantSeedId))
                        plantSeedId = seed.ItemId;
                }
                if (ImGui.Selectable(manualOption, plantSeedId == 0))
                    plantSeedId = 0;
            }
        }

        using (ImRaii.Disabled(plugin.AnyChainBusy))
        {
            if (ImGui.SmallButton(plantPanelCycle ? "Harvest + Plant" : "Plant"))
            {
                if (plantPanelCycle)
                    plugin.PotChain.Cycle(pot, plantSoilId, plantSeedId);
                else
                    plugin.PotChain.Plant(pot, plantSoilId, plantSeedId);
                plugin.Launched(plugin.PotChain);
                plantPanelPot = null;
            }
        }
        BusyTip();
    }

    /// <summary>Every soil the bags hold, by the game's own item names. Read live rather
    /// than tabled on purpose: there is NO soil table to draw a flowerpot's soil from -
    /// the shipped Soils.json is the nine outdoor topsoils, and "Potting Soil", which is
    /// what a flowerpot actually takes, is not a topsoil and is nowhere in our data. So
    /// this is not a table at all: it is what is in the bags right now whose name the GAME
    /// says ends in "soil". Nothing invented, nothing hardcoded, and a soil we have never
    /// heard of shows up the moment the player buys one.</summary>
    private static unsafe List<(uint ItemId, string Name, int Count)> SoilsInBag()
        => BagItemsWhere(static name => name.EndsWith("soil", StringComparison.OrdinalIgnoreCase));

    /// <summary>Seed items in the bags that the crop table does NOT know - flowerpot
    /// flower seeds, mostly (the crop table is outdoor gardening; flowerpot flowers are a
    /// separate game system with their own seeds). Read live by the game's own item names,
    /// same reasoning as <see cref="SoilsInBag"/>: nothing invented, and the sow
    /// verification receipts the name at plant time either way.</summary>
    private static List<(uint ItemId, string Name, int Count)> ExtraSeedsInBag()
    {
        var known = Plugin.Tables.Crops.Select(c => c.SeedId).ToHashSet();
        return BagItemsWhere(name => name.EndsWith(" seeds", StringComparison.OrdinalIgnoreCase))
            .Where(s => !known.Contains(s.ItemId))
            .ToList();
    }

    private static unsafe List<(uint ItemId, string Name, int Count)> BagItemsWhere(
        Func<string, bool> nameMatches)
    {
        var found = new List<(uint, string, int)>();
        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inventory == null)
            return found;

        var seen = new HashSet<uint>();
        foreach (var bag in new[]
                 {
                     FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1,
                     FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory2,
                     FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory3,
                     FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4,
                 })
        {
            var container = inventory->GetInventoryContainer(bag);
            if (container == null)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0 || !seen.Add(slot->ItemId))
                    continue;

                var name = PlantFlow.ItemName(slot->ItemId);
                if (!nameMatches(name))
                    continue;

                found.Add((slot->ItemId, name, inventory->GetInventoryItemCount(slot->ItemId)));
            }
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        return found;
    }

    private static unsafe int InventoryCount(uint itemId)
    {
        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        return inventory == null ? 0 : inventory->GetInventoryItemCount(itemId);
    }

    // ------------------------------------------------------------------ recon

#if DEBUG
    /// <summary>Hover text for the instrument buttons - what each one writes to the log,
    /// so a probe run is a deliberate act rather than a mystery button.</summary>
    private static void ReconTip(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    /// <summary>The instrument, quarantined in its own tab and DEBUG-only: a Release plugin
    /// has no probe in it at all (the whole ReconProbe file is #if DEBUG), so this tab
    /// cannot exist without one. Nothing in here is product - it writes to the log and
    /// mints capture fixtures - so it keeps reading as an instrument inside its own tab.
    ///
    /// <para>One button, on purpose (Sam's ruling 08-15): a capture that missed the one
    /// dump you needed costs another whole trip out to the estate, so it fires everything
    /// at once and catches too much rather than not enough. Watch-plant-flow stays a
    /// separate toggle - it is a recording that runs while you play, not a snapshot.</para>
    /// </summary>
    private void DrawReconTab()
    {
        using var tab = ImRaii.TabItem("Recon###recon");
        if (!tab.Success)
            return;

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            ImGui.TextWrapped("Instrument, not product. Everything here writes to the Dalamud log.");

        // The sensor filters to beds by DataId, so the old "beds only" toggle is gone -
        // what the table shows now IS the bed set, identified by the game's own gimmick.
        // Recon keeps the fixed 40y sweep: the instrument is meant to see further than
        // the app's own (now player-set) scan radius.
        var beds = ObjectSensor.NearbyBeds();

        if (ImGui.Button("Capture everything"))
            CaptureEverything(beds);
        ReconTip(
            "One press, everything at once:\n"
            + "  · housing location (ward/plot/room + inside flag)\n"
            + "  · housing records (furniture vector + gardening DataMap, raw hex + decode)\n"
            + "  · bed/pot struct dumps and the nearby-object sweep\n"
            + "  · the bed snapshot (zone, patch ids, distances, targetable)\n"
            + "Catch too much rather than not enough.");

        ImGui.SameLine();

        // Sow-flow recon, SCOPED (Sam's ruling 08-15): the checkbox is a persisted intent
        // - "record when I'm gardening" - not a raw switch. Armed automatically within
        // 4.6y of a pot, disarmed on rezone (Plugin.AutoArmWatcher), so captures hold
        // gardening rather than a night of quest dialogue, and hot-loads forget nothing.
        var watchIntent = Plugin.Configuration.WatchPlantFlow;
        if (ImGui.Checkbox("Watch plant flow", ref watchIntent))
        {
            Plugin.Configuration.WatchPlantFlow = watchIntent;
            Plugin.Configuration.Save();
            if (!watchIntent)
                PlantFlow.StopWatching();
        }

        // The sanity readout (Sam's ask): the checkbox is intent, this is the actual
        // state, side by side - so "why didn't that capture?" is answered at a glance.
        ImGui.SameLine();
        if (PlantFlow.Watching)
            ImGui.TextColored(Green, "recording");
        else if (watchIntent)
            ImGui.TextDisabled($"idle - arms within {Plugin.WatcherArmRangeY:F1}y of a pot, drops on rezone");
        else
            ImGui.TextDisabled("off");

        ImGui.TextDisabled($"{beds.Count} beds within 40y");

        using var table = ImRaii.Table("sightings", 5,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY,
            new Vector2(0, 0));
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Bed", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Patch", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableSetupColumn("Reach", ImGuiTableColumnFlags.WidthFixed, 45f);
        ImGui.TableSetupColumn("##tend", ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var b in beds.OrderBy(b => b.Gimmick.PatchOrdinal).ThenBy(b => b.Gimmick.BedIndex))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            // Gimmick indices are stored raw 0-based; +1 only in this display line.
            ImGui.Text($"Patch {b.Gimmick.PatchOrdinal + 1} bed {b.Gimmick.BedIndex + 1}");
            ImGui.TableNextColumn();
            ImGui.Text($"0x{b.Gimmick.PatchId:X4}");
            ImGui.TableNextColumn();
            ImGui.Text($"{b.Distance:F1}y");
            ImGui.TableNextColumn();
            ImGui.TextColored(b.InReach ? Green : Red, b.InReach ? "yes" : "no");
            ImGui.TableNextColumn();
            using var id = ImRaii.PushId((int)b.Object.EntityId);
            using (ImRaii.Disabled(plugin.AnyChainBusy || !b.InReach))
            {
                if (ImGui.Button("Tend"))
                {
                    plugin.TendChain.TendOne(b);
                    plugin.Launched(plugin.TendChain);
                }
            }
        }
    }

    /// <summary>Every instrument, one press, fenced in the log so a capture is one findable
    /// block rather than four runs someone has to stitch together afterwards.</summary>
    private static void CaptureEverything(List<BedObject> beds)
    {
        Plugin.Log.Information("[Recon] ===== capture start =====");
        ReconProbe.LogHousingLocation();
        ReconProbe.DumpAccessRoster();
        ReconProbe.DumpHousingRecords();
        ReconProbe.DumpBedStructs();

        Plugin.Log.Information(
            $"[Recon] zone ({Plugin.ClientState.TerritoryType}), {beds.Count} beds in 40y:");
        foreach (var b in beds)
            Plugin.Log.Information(
                $"[Recon] patch 0x{b.Gimmick.PatchId:X4} ordinal {b.Gimmick.PatchOrdinal} bed {b.Gimmick.BedIndex} "
                + $"| {b.Distance:F2}y | targetable={b.Targetable} | pos={b.Object.Position:F1}");

        Plugin.Log.Information("[Recon] ===== capture end =====");
    }
#endif
}
