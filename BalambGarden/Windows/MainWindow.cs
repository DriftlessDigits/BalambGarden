using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using BalambGarden.Game;
using Lumina.Excel.Sheets;

namespace BalambGarden.Windows;

/// <summary>
/// The gardening desk: patch buttons up top, the run panel while the chain works
/// (elapsed / n-of-m / ETA / per-bed lines - the Scrooge run-log treatment), the
/// ledger of every bed we know, and the recon table tucked away in a collapsed
/// section for plumbing sessions.
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

        ImGui.TextDisabled($"Engine v2 loaded - {Plugin.Tables.SpeciesName(0x24)} says hi");

        var territoryId = Plugin.ClientState.TerritoryType;
        var territoryName = Plugin.DataManager.GetExcelSheet<TerritoryType>()
            .TryGetRow(territoryId, out var territoryRow)
            ? territoryRow.PlaceName.Value.Name.ToString()
            : "unknown";
        ImGui.Text($"Zone: ({territoryId}) {territoryName}");

        DrawPatches();
        DrawLedger(territoryId);
        DrawRecon(territoryId, territoryName);
    }

    /// <summary>Bridges a sensor patch to the POC chain's shape - Task 7 retires this.</summary>
    private static PatchSighting AsSighting(PatchGroup patch)
        => new(patch.Center, patch.Beds, patch.Distance);

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
                plugin.TendChain.TendAll(inReach.Select(AsSighting));
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
                    plugin.TendChain.TendPatch(AsSighting(patch));
                    plugin.RunLogWindow.IsOpen = true;
                }
            }

            ImGui.SameLine();
            ImGui.TextColored(
                patch.InReach ? Green : Red,
                $"{patch.Distance:F1}y {(patch.InReach ? "- in reach" : "- walk closer")}");
        }
    }

    private static void DrawLedger(uint territoryId)
    {
        var records = Plugin.Configuration.Ledger
            .Where(r => r.Territory == territoryId)
            .OrderBy(r => r.PatchX).ThenBy(r => r.PatchZ).ThenBy(r => r.Bed, StringComparer.Ordinal)
            .ToList();
        if (records.Count == 0)
            return;

        ImGui.Spacing();
        if (!ImGui.CollapsingHeader($"Ledger ({records.Count} beds)###ledger", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        using var table = ImRaii.Table("ledger", 3,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY,
            new Vector2(0, 180));
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Bed", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Plant", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Watered", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var record in records)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(record.Bed);
            ImGui.TableNextColumn();
            ImGui.Text(record.Plant.Length > 0 ? record.Plant : "?");
            ImGui.TableNextColumn();
            ImGui.Text(Ago(record.LastTendedUtc));
        }
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        // Clock skew clamps to "just now", never a negative age (Scrooge ruling).
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours}h ago"
            : $"{(int)span.TotalDays}d ago";
    }

    private void DrawRecon(uint territoryId, string territoryName)
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Recon###recon"))
            return;

        // The sensor filters to beds by DataId, so the old "beds only" toggle is gone -
        // what the table shows now IS the bed set, identified by the game's own gimmick.
        var beds = ObjectSensor.NearbyBeds();
        if (ImGui.Button("Log snapshot"))
        {
            Plugin.Log.Information($"[Recon] zone ({territoryId}) {territoryName}, {beds.Count} beds in 40y:");
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
                    plugin.TendChain.TendOne(b.Object);
                    plugin.RunLogWindow.IsOpen = true;
                }
            }
        }
    }
}
