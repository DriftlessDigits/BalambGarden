using System;
using System.Linq;
using System.Numerics;
using BalambGarden.Chains;
using BalambGarden.Engine.Census;
using BalambGarden.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace BalambGarden.Windows;

/// <summary>
/// The gardening desk, v2 minimal: which estate we're standing on, the patch buttons
/// that do the work, and the beds this estate has actually claimed (claims come from
/// receipts alone - a bed appears here because we touched it). Recon lives in a
/// collapsed section for plumbing sessions. Stage 3 rewrites this properly.
/// </summary>
public class MainWindow : Window, IDisposable
{
    private static readonly Vector4 Green = new(0.4f, 1f, 0.4f, 1f);
    private static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);

    private readonly Plugin plugin;

    // Cycle launcher state: which patch's panel is open, its editable plan, and whether
    // the launch button is on its second press (relabel-not-modal, no undo).
    private ushort? cyclePatchId;
    private ReplantPlan? cyclePlan;
    private bool cycleArmed;

    // 0 = no expectation; the pot chain then reports what the confirmation named instead
    // of judging it.
    private uint potSeedId;

    public MainWindow(Plugin plugin)
        : base("Balamb Garden##BalambGardenMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 340),
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

        var estate = EstateSensor.Current();
        DrawHeader(estate);
        DrawClaimToggle();
        DrawPatches();
        DrawPots();
        DrawClaimedBeds(estate);
        DrawRecon();
    }

    /// <summary>Estate line. Read-only: visiting is what writes the roster (the pump
    /// upserts on arrival), so Draw never touches the ledger.</summary>
    private static void DrawHeader(EstateKey? estate)
    {
        if (estate is null)
        {
            ImGui.Text("Not at a housing estate.");
            return;
        }

        var name = Plugin.Garden.Ledger.Estates.FirstOrDefault(e => e.Key == estate)?.DisplayName
                   ?? estate.DisplayWardPlot();
        var unreadable = MapSensor.UnreadableCount > 0
            ? $" - {MapSensor.UnreadableCount} unreadable"
            : "";
        ImGui.Text($"{name}{unreadable}");
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

    /// <summary>The working patch list: the capped sweep only (Sam's 08-14 ruling), so
    /// a neighbour's garden never grows a verb here. Recon keeps the full 40y view.</summary>
    private void DrawPatches()
    {
        var patches = ObjectSensor.Patches();
        if (patches.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.Text("Patches");

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

        foreach (var patch in patches)
        {
            using var patchId = ImRaii.PushId(patch.PatchId);
            using (ImRaii.Disabled(plugin.AnyChainBusy || !patch.InReach))
            {
                // Ordinal is raw 0-based; +1 only here, at the surface.
                if (ImGui.Button($"Water Patch {patch.Ordinal + 1} ({patch.Beds.Count} beds)"))
                {
                    plugin.TendChain.TendPatch(patch);
                    plugin.Launched(plugin.TendChain);
                }

                ImGui.SameLine();
                if (ImGui.Button(cyclePatchId == patch.PatchId ? "Cycle (close)" : "Cycle..."))
                {
                    if (cyclePatchId == patch.PatchId)
                    {
                        cyclePatchId = null;
                    }
                    else
                    {
                        cyclePatchId = patch.PatchId;
                        cyclePlan = EstateSensor.Current() is { } here
                            ? ReplantPlan.DefaultFor(here, patch.Ordinal)
                            : new ReplantPlan();
                        cycleArmed = false;
                    }
                }
            }

            ImGui.SameLine();
            ImGui.TextColored(
                patch.InReach ? Green : Red,
                $"{patch.Distance:F1}y {(patch.InReach ? "- in reach" : "- walk closer")}");

            if (cyclePatchId == patch.PatchId)
                DrawCyclePanel(patch);
        }
    }

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
            cycleArmed = false;
        }

        using (ImRaii.Disabled(plugin.AnyChainBusy || refusal is not null))
        {
            var label = cycleArmed
                ? $"Run cycle: {plan.Seeds.Count} beds - sure?"
                : $"Run cycle ({plan.Seeds.Count} beds)";
            if (ImGui.Button(label))
            {
                if (cycleArmed)
                {
                    plugin.CycleChain.Run(patch, plan);
                    plugin.Launched(plugin.CycleChain);
                    cycleArmed = false;
                    cyclePatchId = null;
                }
                else
                {
                    cycleArmed = true;
                }
            }
        }
    }

    /// <summary>Indoor pots. Watering a pot is the PIGMENT mechanic, not a drink -
    /// flowerpots cannot wilt (08-15) - so the verb says so on its face.</summary>
    private void DrawPots()
    {
        if (!EstateSensor.IsInside())
            return;

        var pots = ObjectSensor.NearbyPots();
        if (pots.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.Text($"Pots ({pots.Count} nearby)");

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
                    ImGui.SetTooltip("Applies pigment. Flowerpots never wilt - this is colour, not water.");

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

    /// <summary>What this estate has claimed. Water reads "?" whenever the crop or the
    /// tend clock is missing - an unknown state is stated, never guessed - and "-" for
    /// pots, which cannot wilt at all.</summary>
    private static void DrawClaimedBeds(EstateKey? estate)
    {
        if (estate is null)
            return;

        var beds = Plugin.Garden.Census.LedgerBeds
            .Where(b => b.Estate == estate)
            .OrderBy(b => b.IsPot).ThenBy(b => b.PatchOrdinal).ThenBy(b => b.BedSlot)
            .ToList();
        if (beds.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No beds claimed here yet - tend one and it appears.");
            return;
        }

        ImGui.Spacing();
        if (!ImGui.CollapsingHeader($"Beds ({beds.Count})###claimed", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        using var table = ImRaii.Table("claimed", 5,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY,
            new Vector2(0, 200));
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Bed", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Plant", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Stage", ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableSetupColumn("Water", ImGuiTableColumnFlags.WidthFixed, 75f);
        ImGui.TableSetupColumn("Last seen", ImGuiTableColumnFlags.WidthFixed, 85f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var now = DateTimeOffset.UtcNow;
        foreach (var bed in beds)
        {
            var latest = bed.Latest;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            // Ordinals and slots are stored raw 0-based; +1 only in these display strings.
            ImGui.Text(bed.IsPot
                ? $"pot key {bed.MapKey}"
                : $"Patch {bed.PatchOrdinal + 1} bed {bed.BedSlot + 1}");
            ImGui.TableNextColumn();
            ImGui.Text(latest is null ? "?" : Plugin.Tables.SpeciesName(latest.SpeciesIndex));
            ImGui.TableNextColumn();
            ImGui.Text(latest is null ? "?" : latest.Stage.ToString());
            ImGui.TableNextColumn();
            var crop = latest is null ? null : Plugin.Tables.CropBySpeciesIndex(latest.SpeciesIndex);
            // Flowerpots cannot wilt (08-15 finding: third-party table shows every pot seed
            // at 1-day grow with no wilt time, plus our own unwatered sunflower receipt) -
            // indoor watering is the pigment mechanic, cosmetic only. "-" says the column
            // does not apply here; "?" would claim we merely don't know.
            ImGui.Text(bed.IsPot ? "-"
                : crop is null ? "?"
                : Plugin.Garden.Wilt.StateFor(bed, crop, now).ToString());
            ImGui.TableNextColumn();
            ImGui.Text(latest is null ? "?" : Ago(latest.At));
        }
    }

    private static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        // Clock skew clamps to "just now", never a negative age (Scrooge ruling).
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours}h ago"
            : $"{(int)span.TotalDays}d ago";
    }

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
