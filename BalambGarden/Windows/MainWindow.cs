using System;
using System.Linq;
using System.Numerics;
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
        using (ImRaii.Disabled(plugin.TendChain.Busy || inReach.Count == 0))
        {
            if (ImGui.Button($"Tend All ({totalBeds} beds, {inReach.Count} patches)"))
            {
                plugin.TendChain.TendAll(inReach);
                plugin.RunLogWindow.IsOpen = true;
            }
        }

        foreach (var patch in patches)
        {
            using var patchId = ImRaii.PushId(patch.PatchId);
            using (ImRaii.Disabled(plugin.TendChain.Busy || !patch.InReach))
            {
                // Ordinal is raw 0-based; +1 only here, at the surface.
                if (ImGui.Button($"Water Patch {patch.Ordinal + 1} ({patch.Beds.Count} beds)"))
                {
                    plugin.TendChain.TendPatch(patch);
                    plugin.RunLogWindow.IsOpen = true;
                }
            }

            ImGui.SameLine();
            ImGui.TextColored(
                patch.InReach ? Green : Red,
                $"{patch.Distance:F1}y {(patch.InReach ? "- in reach" : "- walk closer")}");
        }
    }

    /// <summary>What this estate has claimed. Water reads "?" whenever the crop or the
    /// tend clock is missing - an unknown state is stated, never guessed.</summary>
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
            ImGui.Text(crop is null ? "?" : Plugin.Garden.Wilt.StateFor(bed, crop, now).ToString());
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
            using (ImRaii.Disabled(plugin.TendChain.Busy || !b.InReach))
            {
                if (ImGui.Button("Tend"))
                {
                    plugin.TendChain.TendOne(b);
                    plugin.RunLogWindow.IsOpen = true;
                }
            }
        }
    }
}
