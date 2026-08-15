using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BalambGarden.Chains;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
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
    /// all, only this invitation.</summary>
    private const string UntrackedTag = "not tracked - act on it through Balamb and it joins the ledger";

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

    // 0 = no expectation; the pot chain then reports what the confirmation named instead
    // of judging it. Both are also the auto-fill's order form: a 0 here means there is
    // nothing to fill that slot with and the picker stays the player's.
    private uint potSeedId;
    private uint potSoilId;

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
                // A tab means "my garden", not "somewhere I once stood" (Sam's ruling
                // 08-15, emphatic). Visits are still recorded underneath - the tab
                // appears the moment a first claim lands here. The one exception is the
                // estate we are standing AT: its tab must exist unclaimed, because it is
                // where the act-to-claim invitations live; walk away without claiming
                // and it vanishes behind you.
                foreach (var record in estates
                             .Where(e => e.Key == here
                                 || Plugin.Garden.Census.LedgerBeds.Any(b => b.Estate == e.Key))
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
            w => WindowFormat.Range(w.Earliest.ToLocalTime(), w.Latest.ToLocalTime()));

        ImGui.Text(verdict.Text);

        if (verdict.Window is not { } window)
            return;

        ImGui.SameLine();
        ImGui.TextDisabled(WindowFormat.Mark(window.Provenance));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(WindowFormat.MarkMeaning(window.Provenance));
    }

    /// <summary>The two things the verdict cannot say: that no estate has a tab yet, and
    /// that we are standing somewhere the ledger has not finished writing down. Both are
    /// silent when they do not apply.</summary>
    private static void DrawLocatorNotes(List<EstateRecord> estates, EstateKey? here)
    {
        if (estates.Count == 0)
            ImGui.TextDisabled("No estates visited yet - walk onto one and it joins the roster.");
        else if (here is { } key && estates.All(e => e.Key != key))
            // The ledger writes an estate on arrival. If that write has not landed yet,
            // say where we are rather than inventing a tab for it.
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

        ImGui.Spacing();
        DrawEstateHeader(record, beds, isHere, now);
        DrawEstateSections(record, beds, isHere, now);
    }

    private void DrawEstateHeader(
        EstateRecord record, List<ClaimedBed> beds, bool isHere, DateTimeOffset now)
    {
        if (isHere)
            ImGui.TextDisabled("you are here");
        else
            // Memory says how old it is, every time. A count with no age is a count
            // pretending to be current.
            ImGui.TextDisabled(
                $"{beds.Count} claimed · last visited {WindowFormat.Ago(record.LastVisited, now)}");

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
        EstateRecord record, List<ClaimedBed> beds, bool isHere, DateTimeOffset now)
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
            DrawOutdoorSection(record, outdoorRollups, patches, beds, isHere, now);
        }

        if (hasIndoor)
        {
            SectionHeader("Indoor");
            DrawIndoorSection(record, potRollups, pots, isHere, now);
        }

        if (hasOutdoor || hasIndoor)
            return;

        // A place you are standing in with nothing to show gets an invitation, not a row
        // of dead controls.
        ImGui.TextDisabled(isHere
            ? "Nothing claimed here yet - tend a bed and it appears."
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
        List<ClaimedBed> beds, bool isHere, DateTimeOffset now)
    {
        DrawUnclaimedLine(patches, beds);
        DrawTendAll(patches);

        foreach (var rollup in rollups)
            DrawRollupRow(record, rollup, patches, isHere, now);

        // A patch standing right there that the ledger has nothing for at all: it still
        // needs a row, or a fresh ledger could never be bootstrapped (tending is the only
        // thing that claims).
        foreach (var patch in patches.Where(p => rollups.All(r => r.PatchOrdinal != p.Ordinal)))
            DrawUnclaimedPatchRow(patch);
    }

    /// <summary>Tracked pots first, from the ledger, and they render whether or not you are
    /// standing here - a claimed pot is memory like any other bed. The block under them is
    /// pure sensor: what is within arm's reach right now.</summary>
    private void DrawIndoorSection(
        EstateRecord record, List<PatchRollup> rollups, List<PotObject> pots,
        bool isHere, DateTimeOffset now)
    {
        foreach (var rollup in rollups)
            DrawRollupRow(record, rollup, [], isHere, now);

        // How many planted pots in this room no ledger row claims. Counted off the MAP, not
        // off the pot objects: a pot object cannot be matched back to a map key (that is the
        // whole reason the chain has to diff for it), but the map knows exactly which keys
        // are occupied and the ledger knows exactly which ones are claimed. It goes quiet
        // the moment the last one binds, which is how a bind becomes visible here.
        var untracked = isHere && EstateSensor.IsInside()
            ? CensusPump.LastIndoor.Keys.Count(key => !Plugin.Garden.Census.LedgerBeds.Any(
                b => b.Estate == record.Key && b.IsPot && b.MapKey == key))
            : 0;

        DrawPots(record.Key, pots, untracked);
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
    private void DrawTendAll(List<PatchGroup> patches)
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
        using (ImRaii.Disabled(plugin.AnyChainBusy))
        {
            if (ImGui.Button($"Tend All ({totalBeds} beds, {inReach.Count} patches)"))
            {
                plugin.TendChain.TendAll(inReach);
                plugin.Launched(plugin.TendChain);
            }
        }

        BusyTip();
    }

    // ------------------------------------------------------------------ rollups

    /// <summary>One patch as a line: name, the strip, then the counts. The strip is the
    /// census at a glance; the counts and the grid under it are the same census in words,
    /// which is the copy that carries the claim.</summary>
    private void DrawRollupRow(
        EstateRecord record, PatchRollup rollup, List<PatchGroup> patches,
        bool isHere, DateTimeOffset now)
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

        // Pots have no eight-slot shape (they are keyed by map key, one plant apiece), so
        // there is no strip to draw for them - only outdoor patches get one.
        if (!rollup.IsPots)
        {
            ImGui.SameLine();
            DrawStrip(beds, isHere, now);
        }

        ImGui.SameLine();
        DrawRollupSummary(rollup);

        if (patch is not null)
            DrawPatchVerbs(record, patch);

        if (open)
        {
            DrawBedGrid(record, rollup, beds, patch, isHere, now);
            ImGui.TreePop();
        }

        if (patch is not null && cyclePatch == (record.Key, rollup.PatchOrdinal))
            DrawCyclePanel(patch);
    }

    private static List<ClaimedBed> BedsOf(EstateKey estate, PatchRollup rollup)
    {
        return Plugin.Garden.Census.LedgerBeds
            .Where(b => b.Estate == estate
                        && b.IsPot == rollup.IsPots
                        && b.PatchOrdinal == rollup.PatchOrdinal)
            .OrderBy(b => b.BedSlot)
            .ToList();
    }

    /// <summary>The counts, quiet by default. Only the two things that want a decision now
    /// - ripe and thirst - come off TextDisabled, and thirst goes red only when the wilt
    /// clock is actually in the danger band.</summary>
    private static void DrawRollupSummary(PatchRollup rollup)
    {
        ImGui.TextDisabled(rollup.IsPots
            ? $"{rollup.Claimed} claimed"
            : $"{rollup.Claimed}/{PatchStrip.Slots}");

        var thirsty = rollup.Due + rollup.Overdue + rollup.Danger;
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

        if (rollup.NextRipe is not { } window)
            return;

        ImGui.SameLine();
        ImGui.TextDisabled(
            $"· ripe ~{WindowFormat.Range(window.Earliest.ToLocalTime(), window.Latest.ToLocalTime())}");
        ImGui.SameLine();
        ImGui.TextDisabled(WindowFormat.Mark(window.Provenance));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(WindowFormat.MarkMeaning(window.Provenance));
    }

    // ------------------------------------------------------------------ patch strip

    /// <summary>Eight cells, one per bed slot: fill says stage, the under-bar says the bed
    /// wants water. Nothing is drawn for a state that means nothing (a watered bed, a pot,
    /// a slot we cannot judge) - a bar that is always there is a bar nobody reads.</summary>
    private static void DrawStrip(List<ClaimedBed> beds, bool isHere, DateTimeOffset now)
    {
        var cells = PatchStrip.ForPatch(beds, Plugin.Tables, Plugin.Garden.Wilt, now);
        var draw = ImGui.GetWindowDrawList();
        var side = ImGui.GetTextLineHeight();
        var size = new Vector2(side, side);

        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0f, CellGap);

            var cell = cells[i];
            var bed = beds.FirstOrDefault(b => b.BedSlot == cell.Slot);
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
                ImGui.SetTooltip(CellTooltip(cell, bed, drifted, now));
        }
    }

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

    /// <summary>The bed's whole line, on hover. The strip is a picture; this is the
    /// sentence behind it, and it says the same thing the grid row says.</summary>
    private static string CellTooltip(
        StripCell cell, ClaimedBed? bed, bool drifted, DateTimeOffset now)
    {
        if (bed is null)
            return $"Bed {cell.Slot + 1}: {UntrackedTag}";
        if (drifted)
            return $"Bed {cell.Slot + 1}: reads empty now - replanted without me?";

        var latest = bed.Latest;
        if (latest is null)
            return $"Bed {cell.Slot + 1}: claimed, nothing seen in it yet";

        var line = $"Bed {cell.Slot + 1}: {Plugin.Tables.SpeciesName(latest.SpeciesIndex)}"
                   + $"\nstage {latest.Stage} · seen {WindowFormat.Ago(latest.At, now)}"
                   + $"\nwater {WindowFormat.Water(cell.Water)}";

        if (latest.Stage >= 4)
            return $"{line}\nripe now";

        var crop = Plugin.Tables.CropBySpeciesIndex(latest.SpeciesIndex);
        if (crop is null || StageModel.RipeWindow(bed.Ring, crop.GrowHours) is not { } window)
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
    private void DrawPatchVerbs(EstateRecord record, PatchGroup patch)
    {
        using var indent = ImRaii.PushIndent();

        if (!patch.InReach)
        {
            ImGui.TextDisabled($"{patch.Distance:F1}y away - walk closer to tend it");
            return;
        }

        using (ImRaii.Disabled(plugin.AnyChainBusy))
        {
            if (ImGui.SmallButton("Water Patch"))
            {
                plugin.TendChain.TendPatch(patch);
                plugin.Launched(plugin.TendChain);
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
    }

    /// <summary>A patch in front of you with nothing claimed in it. No rollup can exist
    /// for it (rollups read the ledger), but a verb has to, or nothing here is reachable.</summary>
    private void DrawUnclaimedPatchRow(PatchGroup patch)
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

        using (ImRaii.Disabled(plugin.AnyChainBusy))
        {
            if (ImGui.SmallButton("Tend to claim"))
            {
                plugin.TendChain.TendPatch(patch);
                plugin.Launched(plugin.TendChain);
            }
        }

        BusyTip();
    }

    // ------------------------------------------------------------------ bed grid

    private void DrawBedGrid(
        EstateRecord record, PatchRollup rollup, List<ClaimedBed> beds, PatchGroup? patch,
        bool isHere, DateTimeOffset now)
    {
        if (beds.Count == 0)
            return;

        using var table = ImRaii.Table($"beds{rollup.PatchOrdinal}", 6,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Bed", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("Plant", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Stage", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Water", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("Ripe", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("##verbs", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableHeadersRow();

        foreach (var bed in beds)
        {
            using var id = ImRaii.PushId(bed.BedSlot);
            ImGui.TableNextRow();

            if (ReadsEmptyNow(bed, isHere))
            {
                DrawDriftRow(record, bed);
                continue;
            }

            var bedObject = patch?.Beds.FirstOrDefault(b => b.Gimmick.BedIndex == bed.BedSlot);
            var latest = bed.Latest;
            var crop = latest is null ? null : Plugin.Tables.CropBySpeciesIndex(latest.SpeciesIndex);

            ImGui.TableNextColumn();
            ImGui.Text(bed.IsPot ? $"pot key {bed.MapKey}" : $"Bed {bed.BedSlot + 1}");
            if (bedObject is { InReach: true })
            {
                ImGui.SameLine();
                ImGui.TextColored(Green, "in reach");
            }

            ImGui.TableNextColumn();
            ImGui.Text(latest is null ? "?" : Plugin.Tables.SpeciesName(latest.SpeciesIndex));

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
            DrawWaterCell(bed, crop, now);

            ImGui.TableNextColumn();
            DrawRipeCell(bed, crop, latest, now);

            ImGui.TableNextColumn();
            DrawBedVerbs(record, bed, bedObject);
        }
    }

    /// <summary>State as text plus a dot in the state's colour - the text carries the
    /// claim on its own, the dot is only there to find it fast.</summary>
    private static void DrawWaterCell(ClaimedBed bed, Engine.Domain.Crop? crop, DateTimeOffset now)
    {
        // Pot flowers have never been seen to wilt (08-15: every flowerpot seed in the
        // third-party table carries no wilt time, and our own sunflower went seed-to-ripe
        // unwatered). Whether a normal CROP in a pot wilts is still unknown - a lab is
        // running - so this cell prints exactly what the Engine reports and asserts
        // nothing more.
        var state = bed.IsPot ? WaterState.NotApplicable
            : crop is null ? WaterState.Unknown
            : Plugin.Garden.Wilt.StateFor(bed, crop, now);

        var color = state switch
        {
            WaterState.Watered => Green,
            WaterState.Due => Amber,
            WaterState.Overdue => Amber,
            WaterState.Danger => Red,
            _ => Dim,
        };

        ImGui.TextColored(color, "●");
        ImGui.SameLine();
        ImGui.Text(WindowFormat.Water(state));
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

        if (crop is null || StageModel.RipeWindow(bed.Ring, crop.GrowHours) is not { } window)
        {
            ImGui.TextDisabled("?");
            return;
        }

        ImGui.Text(WindowFormat.Range(window.Earliest.ToLocalTime(), window.Latest.ToLocalTime()));
        ImGui.SameLine();
        ImGui.TextDisabled(WindowFormat.Mark(window.Provenance));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(WindowFormat.MarkMeaning(window.Provenance));
    }

    /// <summary>Tend appears only for a bed that is actually in reach. Abandon is always
    /// there: forgetting a claim is a ledger act, and the ledger is readable from anywhere.</summary>
    private void DrawBedVerbs(EstateRecord record, ClaimedBed bed, BedObject? bedObject)
    {
        if (bedObject is { InReach: true } target)
        {
            using (ImRaii.Disabled(plugin.AnyChainBusy))
            {
                if (ImGui.SmallButton("Tend"))
                {
                    plugin.TendChain.TendOne(target);
                    plugin.Launched(plugin.TendChain);
                }
            }

            BusyTip();
            ImGui.SameLine();
        }

        DrawAbandonButton(record, bed);
    }

    /// <summary>Forgetting a bed is manual and deliberate (spec): the ledger only ever
    /// loses a claim because a human said so, twice.</summary>
    private void DrawAbandonButton(EstateRecord record, ClaimedBed bed)
    {
        var key = $"abandon:{record.Key.BindingKey(bed.PatchOrdinal)}:{bed.BedSlot}";
        if (!ArmedButton(key, "Abandon", "Abandon - sure?", small: true))
            return;

        Plugin.Garden.Census.Abandon(bed);
        Plugin.Garden.Save();
    }

    /// <summary>Drift: the ledger remembers a plant here, the map read a moment ago says
    /// the bed is empty. That is a sentence about the world, not a data point - so it
    /// replaces the row rather than corrupting it, and the only button is the honest one.</summary>
    private void DrawDriftRow(EstateRecord record, ClaimedBed bed)
    {
        ImGui.TableNextColumn();
        ImGui.Text($"Bed {bed.BedSlot + 1}");
        ImGui.TableNextColumn();
        ImGui.TextColored(Amber, $"Bed {bed.BedSlot + 1} reads empty now - replanted without me?");
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        DrawAbandonButton(record, bed);
    }

    /// <summary>True only when a fresh read of THIS estate's map shows the slot vacant.
    /// Away from the estate there is no read at all, and "I cannot see it" is never
    /// evidence that something is gone.</summary>
    private static bool ReadsEmptyNow(ClaimedBed bed, bool isHere)
    {
        if (!isHere || bed.IsPot)
            return false;
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

    /// <summary>The pipeline reader's advisory lines, in their own tab. The count only
    /// appears when there is one - a permanent "Tips (0)" is filler, and filler trains
    /// people to stop reading the panel that matters.</summary>
    private static void DrawTipsTab(DateTimeOffset now)
    {
        var tips = PipelineReader.Tips(Plugin.Garden.Census.LedgerBeds, Plugin.Tables, now);

        var label = tips.Count > 0 ? $"Tips ({tips.Count})###tips" : "Tips###tips";
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
            ImGui.TextDisabled(tag);
            ImGui.SameLine();
            ImGui.TextWrapped(tip.Text);
        }
    }

    // ------------------------------------------------------------------ pots

    /// <summary>Indoor pots in front of you. Watering a pot is the PIGMENT mechanic, not
    /// a drink - pot flowers have never been seen to wilt (08-15) - so the verb says so
    /// on its face. A pot out of reach is a dim line, not a row of dead buttons.
    ///
    /// <para>These rows are presence first: they exist because you are standing near them.
    /// A row can also carry identity, but only once a chain run has paired this pot object
    /// with a map key (see <see cref="PotIdentity"/>) - a pot object has no map key written
    /// on it, so acting on one is what settles which is which. <paramref name="untracked"/>
    /// is how many planted pots in this room the ledger has no row for at all, counted off
    /// the map, and it is a room-level count for the same reason.</para></summary>
    private void DrawPots(EstateKey estate, List<PotObject> pots, int untracked)
    {
        if (pots.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.Text($"Pots in reach ({pots.Count} nearby)");
        if (untracked > 0)
            ImGui.TextDisabled(UntrackedTag);

        using var indent = ImRaii.PushIndent();
        DrawPotSoilPicker();
        DrawPotSeedPicker();

        foreach (var pot in pots)
        {
            using var id = ImRaii.PushId((int)pot.Object.EntityId);

            if (!pot.InReach)
            {
                ImGui.TextDisabled($"{pot.Name} · {pot.Distance:F1}y away - walk closer");
                DrawPotIdentity(estate, pot);
                continue;
            }

            using (ImRaii.Disabled(plugin.AnyChainBusy))
            {
                if (ImGui.Button("Water (pigment)"))
                {
                    plugin.PotChain.Water(pot);
                    plugin.Launched(plugin.PotChain);
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Applies pigment. Pot flowers never wilt - this is colour, not water.");

                ImGui.SameLine();
                if (ImGui.Button("Harvest"))
                {
                    plugin.PotChain.Harvest(pot);
                    plugin.Launched(plugin.PotChain);
                }

                ImGui.SameLine();
                if (ImGui.Button("Plant"))
                {
                    plugin.PotChain.Plant(pot, potSoilId, potSeedId);
                    plugin.Launched(plugin.PotChain);
                }
            }

            ImGui.SameLine();
            ImGui.TextColored(Green, $"{pot.Name} - {pot.Distance:F1}y");
            DrawPotIdentity(estate, pot);
        }
    }

    /// <summary>What this particular pot is, when that can be said honestly. It can be said
    /// when a chain run this session paired the object with a map key AND a ledger row still
    /// claims that key; anything else is untracked, including a pot the ledger remembers
    /// perfectly well but cannot point at.
    ///
    /// <para>Fail-closed on purpose (Sam's ruling): after a plugin reload the pairings are
    /// gone, so two claimed pots can read untracked here while the rollup above still says
    /// one is claimed. That gap is the truth - the ledger knows the key, this surface knows
    /// objects - and inventing a pairing to close it would eventually put somebody else's
    /// plant on the wrong pot.</para></summary>
    private static void DrawPotIdentity(EstateKey estate, PotObject pot)
    {
        using var indent = ImRaii.PushIndent();

        var key = PotIdentity.KeyFor(estate, pot.Object.EntityId);
        var bed = key is null
            ? null
            : Plugin.Garden.Census.LedgerBeds.FirstOrDefault(
                b => b.Estate == estate && b.IsPot && b.MapKey == key.Value);
        if (bed is null)
        {
            ImGui.TextDisabled(UntrackedTag);
            return;
        }

        // The map is the fresher of the two when it has something to say; an emptied pot
        // reads unoccupied there and the ledger's last observation is the honest fallback.
        var sighted = CensusPump.LastIndoor.GetValueOrDefault(bed.MapKey);
        var species = sighted is { Occupied: true }
            ? sighted.SpeciesIndex : bed.Latest?.SpeciesIndex ?? (ushort)0;
        var stage = sighted is { Occupied: true }
            ? sighted.Stage : bed.Latest?.Stage ?? (byte)0;

        ImGui.TextDisabled($"{Plugin.Tables.SpeciesName(species)} · stage {stage} · claimed");
    }

    /// <summary>
    /// Which soil Plant should put in the left slot. There is NO soil table to draw this
    /// from - the shipped Soils.json is the nine outdoor topsoils, and "Potting Soil", which
    /// is what a flowerpot actually takes, is not a topsoil and is nowhere in our data. So
    /// this is not a table at all: it is what is in the bags right now whose name the GAME
    /// says ends in "soil". Nothing invented, nothing hardcoded, and a soil we have never
    /// heard of shows up the moment the player buys one.
    ///
    /// <para>"Whatever's in the picker" stays the default. Naming a soil is what lets the
    /// chain fill the slot; declining to name one is a real answer that costs two clicks,
    /// and the sow check keeps its null soil expectation either way (a prompt naming potting
    /// soil must never be refused for not being a topsoil).</para>
    /// </summary>
    private void DrawPotSoilPicker()
    {
        var soils = SoilsInBag();
        var chosen = soils.FirstOrDefault(s => s.ItemId == potSoilId);
        var label = potSoilId == 0 || chosen.ItemId == 0
            ? "Whatever's in the picker"
            : $"{chosen.Name} ({chosen.Count})";

        ImGui.SetNextItemWidth(260f);
        using var combo = ImRaii.Combo("Soil", label);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Name a soil and the chain fills that slot for you.\n"
                + "Leave it on \"whatever's in the picker\" and you fill it by hand,\n"
                + "same as before. Either way the confirmation is read before it's answered.");
        if (!combo.Success)
            return;

        if (ImGui.Selectable("Whatever's in the picker", potSoilId == 0))
            potSoilId = 0;

        foreach (var soil in soils)
        {
            if (ImGui.Selectable($"{soil.Name} ({soil.Count})", soil.ItemId == potSoilId))
                potSoilId = soil.ItemId;
        }
    }

    /// <summary>Every soil the bags hold, by the game's own item names. Read live rather
    /// than tabled on purpose - see <see cref="DrawPotSoilPicker"/>.</summary>
    private static unsafe List<(uint ItemId, string Name, int Count)> SoilsInBag()
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
                if (!name.EndsWith("soil", StringComparison.OrdinalIgnoreCase))
                    continue;

                found.Add((slot->ItemId, name, inventory->GetInventoryItemCount(slot->ItemId)));
            }
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        return found;
    }

    /// <summary>What Plant will hold the confirmation to, and - when it names a seed the
    /// chain can find in the bags - what it fills the right-hand slot with. "Whatever I pick
    /// in game" is the default on purpose: the flowerpot flowers most pots hold are absent
    /// from the crop table entirely, so demanding a table seed here would refuse the most
    /// common pot planting there is; it just means those two clicks stay yours.</summary>
    private void DrawPotSeedPicker()
    {
        var label = potSeedId == 0
            ? "Whatever I pick in game"
            : Plugin.Tables.CropBySeedId(potSeedId)?.SeedName ?? $"seed {potSeedId}";

        ImGui.SetNextItemWidth(260f);
        using var combo = ImRaii.Combo("Verify seed", label);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Name a seed and the chain picks it for you; leave it and you pick in game.\n"
                + "Either way this is what it checks the confirmation against before it "
                + "presses Yes.");
        if (!combo.Success)
            return;

        if (ImGui.Selectable("Whatever I pick in game", potSeedId == 0))
            potSeedId = 0;

        foreach (var crop in Plugin.Tables.Crops)
        {
            var have = InventoryCount(crop.SeedId);
            if (have == 0)
                continue;
            if (ImGui.Selectable($"{crop.SeedName} ({have})", crop.SeedId == potSeedId))
                potSeedId = crop.SeedId;
        }
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

        // Sow-flow recon: Sam plants by hand with this on, the log records what the
        // addons actually held. A recording, not a snapshot - so it keeps its own switch.
        var watching = PlantFlow.Watching;
        if (ImGui.Checkbox("Watch plant flow", ref watching))
        {
            if (watching)
                PlantFlow.StartWatching();
            else
                PlantFlow.StopWatching();
        }

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
