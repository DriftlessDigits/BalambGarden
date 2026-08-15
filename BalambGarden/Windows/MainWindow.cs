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
/// The dashboard, read top to bottom: the estate you are standing on gets the whole
/// room - strips, counts, verbs - and every other estate is one dim line of memory that
/// expands read-only when you ask it to. Hierarchy comes from space and brightness, not
/// chrome: full brightness is reserved for the few things that matter now (ripe, danger,
/// a refusal), ages and counts sit at TextDisabled, and colour is semantic only.
///
/// <para>The patch strip is the one bold element: eight cells, one per bed, fill for stage
/// and an under-bar for water. It is a summary layer - the grid and the tooltips remain
/// the reading that carries the claim, so colour is never the only channel.</para>
/// </summary>
public class MainWindow : Window, IDisposable
{
    private static readonly Vector4 Green = new(0.4f, 1f, 0.4f, 1f);
    private static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Amber = new(1f, 0.78f, 0.35f, 1f);
    private static readonly Vector4 Dim = new(0.55f, 0.55f, 0.55f, 1f);

    // Strip geometry. Cells are one text line tall so a strip sits on the row it labels.
    private const float CellGap = 3f;
    private const float WaterBarHeight = 2.5f;

    private readonly Plugin plugin;

    // Cycle launcher state: which patch's panel is open and its editable plan.
    private (EstateKey Estate, int Ordinal)? cyclePatch;
    private ReplantPlan? cyclePlan;

    // Which remembered estate is opened up. One at a time: memory is a list you glance at,
    // not a stack of drawers left hanging open.
    private EstateKey? expanded;

    // Nickname editing: one estate at a time, written back on deactivation.
    private EstateKey? renaming;
    private string renameBuffer = "";

    // Relabel-not-modal arming. One no-undo button may be hot at a time, and any other
    // click in the window cools it - a press that cannot be undone should never be
    // waiting patiently for a stray second click minutes later.
    private string? armedButton;
    private bool armedTouchedThisFrame;

    // 0 = no expectation; the pot chain then reports what the confirmation named instead
    // of judging it.
    private uint potSeedId;

    public MainWindow(Plugin plugin)
        : base("Balamb Garden##BalambGardenMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
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

        DrawClaimToggle();
        if (MapSensor.UnreadableCount > 0)
            ImGui.TextColored(Amber, $"{MapSensor.UnreadableCount} map entries here are unreadable");

        var estates = Plugin.Garden.Ledger.Estates.ToList();

        DrawHere(estates, here, now);
        DrawMemory(estates, here, now);
        DrawTips(now);
        DrawRecon();

        // Anything else the player clicked disarms the hot button.
        if (armedButton is not null && !armedTouchedThisFrame
            && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            armedButton = null;
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

    private static void DrawClaimToggle()
    {
        var claim = Plugin.Configuration.ClaimOnAction;
        if (ImGui.Checkbox("Claim as I go", ref claim))
        {
            Plugin.Configuration.ClaimOnAction = claim;
            // One flag, two homes: the engine decides claims, the config remembers.
            Plugin.Garden.Census.ClaimOnAction = claim;
            Plugin.Configuration.Save();
        }
    }

    /// <summary>The one tooltip a busy chain owes every verb it greys out.</summary>
    private void BusyTip()
    {
        if (plugin.AnyChainBusy && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("a run is going - one chain at a time");
    }

    // ------------------------------------------------------------------ current estate

    /// <summary>The hero: where you are standing, open, with every verb. Objects only
    /// exist here, so this is the only estate that can grow a button.</summary>
    private void DrawHere(List<EstateRecord> estates, EstateKey? here, DateTimeOffset now)
    {
        ImGui.Spacing();

        if (here is not { } key)
        {
            ImGui.TextDisabled("Not standing at an estate - walk onto one and it opens here.");
            return;
        }

        var record = estates.FirstOrDefault(e => e.Key == key);
        if (record is null)
        {
            // The ledger writes an estate on arrival. If that write has not landed yet,
            // say where we are rather than inventing a row for it.
            ImGui.TextDisabled($"{key.DisplayWardPlot()} - reading the estate...");
            return;
        }

        using var id = ImRaii.PushId(record.Key.BindingKey(0));

        ImGui.Text(record.DisplayName);
        ImGui.SameLine();
        ImGui.TextDisabled("you are here");
        ImGui.SameLine();
        DrawRenameControl(record);

        var beds = Plugin.Garden.Census.LedgerBeds.Where(b => b.Estate == record.Key).ToList();

        using var indent = ImRaii.PushIndent();
        DrawEstateBody(record, beds, isHere: true, now);
    }

    /// <summary>Every other estate is memory: one dim line saying what it holds and how
    /// old that memory is. Clicking opens it read-only - away from an estate there are no
    /// objects, so there is nothing there to press.</summary>
    private void DrawMemory(List<EstateRecord> estates, EstateKey? here, DateTimeOffset now)
    {
        var others = estates
            .Where(e => e.Key != here)
            .OrderByDescending(e => e.LastVisited)
            .ToList();

        if (others.Count == 0)
        {
            if (here is null)
                ImGui.TextDisabled("No estates visited yet - walk onto one and it joins the roster.");
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        foreach (var record in others)
        {
            using var id = ImRaii.PushId(record.Key.BindingKey(0));

            var beds = Plugin.Garden.Census.LedgerBeds.Where(b => b.Estate == record.Key).ToList();
            ImGui.TextDisabled(
                $"{record.DisplayName} · {beds.Count} claimed · {WindowFormat.Ago(record.LastVisited, now)}");
            if (ImGui.IsItemClicked())
                expanded = expanded == record.Key ? null : record.Key;

            if (expanded != record.Key)
                continue;

            using var indent = ImRaii.PushIndent();
            DrawRenameControl(record);
            DrawEstateBody(record, beds, isHere: false, now);
        }
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

    private void DrawEstateBody(
        EstateRecord record, List<ClaimedBed> beds, bool isHere, DateTimeOffset now)
    {
        var rollups = Rollups.ForEstate(
            record.Key, Plugin.Garden.Census.LedgerBeds, Plugin.Tables, Plugin.Garden.Wilt, now);

        // Objects only exist where the player is standing. Everything else on this row is
        // memory, and memory never grows a verb.
        var patches = isHere ? ObjectSensor.Patches() : new List<PatchGroup>();
        var inside = isHere && EstateSensor.IsInside();

        if (isHere && !inside)
        {
            DrawUnclaimedLine(patches, beds);
            DrawTendAll(patches);
        }

        // An estate you are standing on with nothing to show gets an invitation, not a
        // row of dead controls.
        if (isHere && rollups.Count == 0 && patches.Count == 0)
            ImGui.TextDisabled("Nothing claimed here yet - tend a bed and it appears.");

        foreach (var rollup in rollups)
            DrawRollupRow(record, rollup, patches, isHere, now);

        // A patch standing right there that the ledger has nothing for at all: it still
        // needs a row, or a fresh ledger could never be bootstrapped (tending is the only
        // thing that claims).
        foreach (var patch in patches.Where(p =>
                     rollups.All(r => r.IsPots || r.PatchOrdinal != p.Ordinal)))
            DrawUnclaimedPatchRow(patch);

        if (isHere)
            DrawPots();
    }

    private static void DrawUnclaimedLine(List<PatchGroup> patches, List<ClaimedBed> beds)
    {
        var sensed = patches.Sum(p => p.Beds.Count);
        var claimed = beds.Count(b => !b.IsPot);
        if (sensed > claimed)
            ImGui.TextColored(Amber, $"{sensed - claimed} unclaimed beds here - tend to claim");
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
            return $"Bed {cell.Slot + 1}: not claimed - tend it and it joins the ledger";
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
        ImGui.TextDisabled(
            $"Patch {patch.Ordinal + 1} · nothing claimed yet · {patch.Beds.Count} beds here");

        using var indent = ImRaii.PushIndent();
        if (!patch.InReach)
        {
            ImGui.TextDisabled($"{patch.Distance:F1}y away - walk closer to claim it");
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
    /// <para>Planting is hybrid by design (Sam's ruling): the chain opens the soil/seed
    /// picker and waits while you fill it, then checks the confirmation against this plan
    /// before answering. The seed column is what it will hold you to, not what it fills.</para>
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

    /// <summary>The pipeline reader's advisory lines. Hidden entirely when it has nothing
    /// to say - an empty "Tips (0)" header is filler, and filler trains people to stop
    /// reading the panel that matters.</summary>
    private static void DrawTips(DateTimeOffset now)
    {
        var tips = PipelineReader.Tips(Plugin.Garden.Census.LedgerBeds, Plugin.Tables, now);
        if (tips.Count == 0)
            return;

        ImGui.Spacing();
        if (!ImGui.CollapsingHeader($"Tips ({tips.Count})###tips"))
            return;

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
    /// on its face. A pot out of reach is a dim line, not a row of dead buttons.</summary>
    private void DrawPots()
    {
        if (!EstateSensor.IsInside())
            return;

        var pots = ObjectSensor.NearbyPots();
        if (pots.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.Text($"Pots in reach ({pots.Count} nearby)");

        using var indent = ImRaii.PushIndent();
        DrawPotSeedPicker();

        foreach (var pot in pots)
        {
            using var id = ImRaii.PushId((int)pot.Object.EntityId);

            if (!pot.InReach)
            {
                ImGui.TextDisabled($"{pot.Name} · {pot.Distance:F1}y away - walk closer");
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
                    plugin.PotChain.Plant(pot, potSeedId);
                    plugin.Launched(plugin.PotChain);
                }
            }

            ImGui.SameLine();
            ImGui.TextColored(Green, $"{pot.Name} - {pot.Distance:F1}y");
        }
    }

    /// <summary>What Plant will hold the confirmation to. "Whatever I pick in game" is the
    /// default on purpose: the chain never fills the picker, and the flowerpot flowers most
    /// pots hold are absent from the crop table entirely, so demanding a table seed here
    /// would refuse the most common pot planting there is.</summary>
    private void DrawPotSeedPicker()
    {
        var label = potSeedId == 0
            ? "Whatever I pick in game"
            : Plugin.Tables.CropBySeedId(potSeedId)?.SeedName ?? $"seed {potSeedId}";

        ImGui.SetNextItemWidth(260f);
        using var combo = ImRaii.Combo("Expected seed", label);
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
#endif

    /// <summary>The instrument, quarantined at the very bottom: dim header, closed by
    /// default, named for what it is. Nothing in here is product - it writes to the log
    /// and mints capture fixtures - so it should never read like a feature.</summary>
    private void DrawRecon()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool open;
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
        {
            open = ImGui.CollapsingHeader("Recon - instrument, not product###recon");
        }

        if (!open)
            return;

        // The sensor filters to beds by DataId, so the old "beds only" toggle is gone -
        // what the table shows now IS the bed set, identified by the game's own gimmick.
        var beds = ObjectSensor.NearbyBeds();
        var territoryId = Plugin.ClientState.TerritoryType;
        if (ImGui.Button("Log snapshot"))
        {
            Plugin.Log.Information($"[Recon] zone ({territoryId}), {beds.Count} beds in 40y:");
            foreach (var b in beds)
                Plugin.Log.Information(
                    $"[Recon] patch 0x{b.Gimmick.PatchId:X4} ordinal {b.Gimmick.PatchOrdinal} bed {b.Gimmick.BedIndex} "
                    + $"| {b.Distance:F2}y | targetable={b.Targetable} | pos={b.Object.Position:F1}");
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{beds.Count} beds within 40y");

#if DEBUG
        // The instruments. All log-only, all DEBUG-only - a Release plugin has no probe
        // in it at all (the whole ReconProbe file is #if DEBUG), so these buttons cannot
        // exist without it.
        if (ImGui.Button("Log housing"))
            ReconProbe.LogHousingLocation();
        ReconTip("Ward/plot/room + inside flag from HousingManager -> log.");

        ImGui.SameLine();
        if (ImGui.Button("Dump records"))
            ReconProbe.DumpHousingRecords();
        ReconTip(
            "Furniture vector + the gardening DataMap (raw hex beside the decode, read\n"
            + "through the same MapSensor the census uses) + bed gimmick ids -> log.");

        ImGui.SameLine();
        if (ImGui.Button("Dump bed structs"))
            ReconProbe.DumpBedStructs();
        ReconTip(
            "0x220 bytes of each nearby bed/pot object -> log. A diff instrument:\n"
            + "capture the same bed in two states and diff the hex.");

        // Sow-flow recon: Sam plants by hand with this on, the log records what the
        // addons actually held.
        var watching = Chains.PlantFlow.Watching;
        if (ImGui.Checkbox("Watch plant flow", ref watching))
        {
            if (watching)
                Chains.PlantFlow.StartWatching();
            else
                Chains.PlantFlow.StopWatching();
        }
#endif

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
}
