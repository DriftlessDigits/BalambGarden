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
/// The dashboard: the estate roster this ledger has actually visited, current estate
/// pinned first and open, each one carrying its patch rollups and - expanded - the beds
/// behind them. Every number states how old it is and what kind of claim it is making;
/// a bed the map now reads empty says so in prose instead of pretending to be data.
/// </summary>
public class MainWindow : Window, IDisposable
{
    private static readonly Vector4 Green = new(0.4f, 1f, 0.4f, 1f);
    private static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Amber = new(1f, 0.78f, 0.35f, 1f);

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

        DrawRoster(here, now);
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

    // ------------------------------------------------------------------ roster

    /// <summary>The roster is the ledger's, not the sensor's: an estate is on this list
    /// because we stood on it once. Current estate first (it is the only one with verbs),
    /// then most recently visited - the rest are memory, and say how old that memory is.</summary>
    private void DrawRoster(EstateKey? here, DateTimeOffset now)
    {
        var estates = Plugin.Garden.Ledger.Estates
            .OrderByDescending(e => e.Key == here)
            .ThenByDescending(e => e.LastVisited)
            .ToList();

        if (estates.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No estates visited yet - walk onto one and it joins the roster.");
            return;
        }

        foreach (var record in estates)
        {
            using var id = ImRaii.PushId(record.Key.BindingKey(0));

            var isHere = record.Key == here;
            var beds = Plugin.Garden.Census.LedgerBeds.Where(b => b.Estate == record.Key).ToList();
            var staleness = isHere ? "" : $" · seen {WindowFormat.Ago(record.LastVisited, now)}";
            var flags = isHere ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;

            var open = ImGui.CollapsingHeader(
                $"{record.DisplayName} - {beds.Count} claimed{staleness}###estate", flags);
            ImGui.SameLine();
            DrawRenameControl(record);

            if (!open)
                continue;

            using var indent = ImRaii.PushIndent();
            DrawEstate(record, beds, isHere, now);
        }
    }

    /// <summary>A nickname is the one piece of an estate the player authors. The pencil
    /// swaps the header's tail for a field; the write lands when the field loses focus,
    /// so a half-typed name never becomes the estate's name.</summary>
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

    private void DrawEstate(
        EstateRecord record, List<ClaimedBed> beds, bool isHere, DateTimeOffset now)
    {
        var rollups = Rollups.ForEstate(
            record.Key, Plugin.Garden.Census.LedgerBeds, Plugin.Tables, Plugin.Garden.Wilt, now);

        // Objects only exist where the player is standing. Everything else on this row is
        // memory, and memory never grows a verb.
        var patches = isHere ? ObjectSensor.Patches() : new List<PatchGroup>();

        if (isHere)
        {
            DrawUnclaimedLine(patches, beds);
            DrawTendAll(patches);
        }

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

    private void DrawTendAll(List<PatchGroup> patches)
    {
        var inReach = patches.Where(p => p.InReach).ToList();
        var totalBeds = inReach.Sum(p => p.Beds.Count);
        using (ImRaii.Disabled(plugin.AnyChainBusy || inReach.Count == 0))
        {
            if (ImGui.Button($"Tend All ({totalBeds} beds, {inReach.Count} patches)"))
            {
                plugin.TendChain.TendAll(inReach);
                plugin.Launched(plugin.TendChain);
            }
        }
    }

    // ------------------------------------------------------------------ rollups

    /// <summary>One patch (or the pot group) as a line: what is claimed, what wants
    /// attention, and when the next thing ripens - with the marker that says how much
    /// that window is really claiming. Open it for the beds behind the numbers.</summary>
    private void DrawRollupRow(
        EstateRecord record, PatchRollup rollup, List<PatchGroup> patches,
        bool isHere, DateTimeOffset now)
    {
        using var id = ImRaii.PushId($"{(rollup.IsPots ? "pots" : "patch")}{rollup.PatchOrdinal}");

        // Ordinals are stored raw 0-based; +1 only in display strings.
        var line = rollup.IsPots
            ? $"Pots: {rollup.Claimed} claimed"
            : $"Patch {rollup.PatchOrdinal + 1}: {rollup.Claimed}/8 claimed";

        var thirsty = rollup.Due + rollup.Overdue + rollup.Danger;
        if (thirsty > 0)
            line += $" · {thirsty} thirsty";
        if (rollup.Ripe > 0)
            line += $" · {rollup.Ripe} ripe";
        if (rollup.Unknown > 0)
            line += $" · {rollup.Unknown} unknown";
        if (rollup.NextRipe is { } window)
            line += $" · ripe ~{WindowFormat.Range(window.Earliest.ToLocalTime(), window.Latest.ToLocalTime())}";

        var open = ImGui.TreeNodeEx($"{line}###row");

        if (rollup.NextRipe is { } marked)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(WindowFormat.Mark(marked.Provenance));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(WindowFormat.MarkMeaning(marked.Provenance));
        }

        var patch = rollup.IsPots
            ? null
            : patches.FirstOrDefault(p => p.Ordinal == rollup.PatchOrdinal);
        if (patch is not null)
            DrawPatchVerbs(record, patch);

        if (open)
        {
            DrawBedGrid(record, rollup, patch, isHere, now);
            ImGui.TreePop();
        }

        if (patch is not null && cyclePatch == (record.Key, rollup.PatchOrdinal))
            DrawCyclePanel(patch);
    }

    private void DrawPatchVerbs(EstateRecord record, PatchGroup patch)
    {
        ImGui.SameLine();
        using (ImRaii.Disabled(plugin.AnyChainBusy || !patch.InReach))
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

        if (!patch.InReach)
        {
            ImGui.SameLine();
            ImGui.TextColored(Red, $"{patch.Distance:F1}y - walk closer");
        }
    }

    /// <summary>A patch in front of you with nothing claimed in it. No rollup can exist
    /// for it (rollups read the ledger), but a verb has to, or nothing here is reachable.</summary>
    private void DrawUnclaimedPatchRow(PatchGroup patch)
    {
        using var id = ImRaii.PushId($"unclaimed{patch.PatchId}");
        ImGui.TextDisabled($"Patch {patch.Ordinal + 1}: nothing claimed yet ({patch.Beds.Count} beds here)");
        ImGui.SameLine();
        using (ImRaii.Disabled(plugin.AnyChainBusy || !patch.InReach))
        {
            if (ImGui.SmallButton("Tend to claim"))
            {
                plugin.TendChain.TendPatch(patch);
                plugin.Launched(plugin.TendChain);
            }
        }

        if (!patch.InReach)
        {
            ImGui.SameLine();
            ImGui.TextColored(Red, $"{patch.Distance:F1}y - walk closer");
        }
    }

    // ------------------------------------------------------------------ bed grid

    private void DrawBedGrid(
        EstateRecord record, PatchRollup rollup, PatchGroup? patch, bool isHere, DateTimeOffset now)
    {
        var beds = Plugin.Garden.Census.LedgerBeds
            .Where(b => b.Estate == record.Key
                        && b.IsPot == rollup.IsPots
                        && b.PatchOrdinal == rollup.PatchOrdinal)
            .OrderBy(b => b.BedSlot)
            .ToList();
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
        ImGui.TableSetupColumn("##verbs", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableHeadersRow();

        foreach (var bed in beds)
        {
            using var id = ImRaii.PushId(bed.BedSlot);
            ImGui.TableNextRow();

            if (ReadsEmptyNow(bed, isHere))
            {
                DrawDriftRow(bed);
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
            DrawBedVerbs(bedObject);
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
            _ => new Vector4(0.6f, 0.6f, 0.6f, 1f),
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

    private void DrawBedVerbs(BedObject? bedObject)
    {
        using (ImRaii.Disabled(plugin.AnyChainBusy || bedObject is not { InReach: true }))
        {
            if (ImGui.SmallButton("Tend") && bedObject is { InReach: true } target)
            {
                plugin.TendChain.TendOne(target);
                plugin.Launched(plugin.TendChain);
            }
        }
    }

    /// <summary>Drift: the ledger remembers a plant here, the map read a moment ago says
    /// the bed is empty. That is a sentence about the world, not a data point - so it
    /// replaces the row rather than corrupting it, and the only button is the honest one.</summary>
    private static void DrawDriftRow(ClaimedBed bed)
    {
        ImGui.TableNextColumn();
        ImGui.Text($"Bed {bed.BedSlot + 1}");
        ImGui.TableNextColumn();
        ImGui.TextColored(Amber, $"Bed {bed.BedSlot + 1} reads empty now - replanted without me?");
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
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

        foreach (var (slot, seedId) in plan.Seeds.OrderBy(kv => kv.Key).ToList())
        {
            using var id = ImRaii.PushId(slot);
            var crop = Plugin.Tables.CropBySeedId(seedId);
            ImGui.TextDisabled(
                $"bed {slot + 1}: {crop?.SeedName ?? $"seed {seedId}"} ({InventoryCount(seedId)} in bag)");
        }

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

    // ------------------------------------------------------------------ pots

    /// <summary>Indoor pots in front of you. Watering a pot is the PIGMENT mechanic, not
    /// a drink - pot flowers have never been seen to wilt (08-15) - so the verb says so
    /// on its face.</summary>
    private void DrawPots()
    {
        if (!EstateSensor.IsInside())
            return;

        var pots = ObjectSensor.NearbyPots();
        if (pots.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.Text($"Pots in reach ({pots.Count} nearby)");

        DrawPotSeedPicker();

        foreach (var pot in pots)
        {
            using var id = ImRaii.PushId((int)pot.Object.EntityId);
            using (ImRaii.Disabled(plugin.AnyChainBusy || !pot.InReach))
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
            ImGui.TextColored(
                pot.InReach ? Green : Red,
                $"{pot.Name} - {pot.Distance:F1}y{(pot.InReach ? "" : " - walk closer")}");
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

    private void DrawRecon()
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Recon###recon"))
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
        // Sow-flow recon: Sam plants by hand with this on, the log records what the
        // addons actually held. Debug-only - it ships with no plugin.
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
