using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
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

        var territoryId = Plugin.ClientState.TerritoryType;
        var territoryName = Plugin.DataManager.GetExcelSheet<TerritoryType>()
            .TryGetRow(territoryId, out var territoryRow)
            ? territoryRow.PlaceName.Value.Name.ToString()
            : "unknown";
        ImGui.Text($"Zone: ({territoryId}) {territoryName}");

        DrawPatches();
        DrawRunPanel();
        DrawLedger(territoryId);
        DrawRecon(territoryId, territoryName);
    }

    private void DrawPatches()
    {
        var patches = GardenScanner.NearbyPatches();
        if (patches.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.Text("Patches");

        var inReach = patches.Where(p => p.InReach).ToList();
        var totalBeds = inReach.Sum(p => p.Beds.Count);
        using (ImRaii.Disabled(plugin.TendChain.Busy || inReach.Count == 0))
        {
            if (ImGui.Button($"Tend All ({totalBeds} beds, {inReach.Count} patches)"))
                plugin.TendChain.TendAll(inReach);
        }

        foreach (var patch in patches)
        {
            using var patchId = ImRaii.PushId(patch.Position.GetHashCode());
            using (ImRaii.Disabled(plugin.TendChain.Busy || !patch.InReach))
            {
                if (ImGui.Button($"Water Patch ({patch.Beds.Count} beds)"))
                    plugin.TendChain.TendPatch(patch);
            }

            ImGui.SameLine();
            ImGui.TextColored(
                patch.InReach ? Green : Red,
                $"{patch.Distance:F1}y {(patch.InReach ? "- in reach" : "- walk closer")}");
        }
    }

    private void DrawRunPanel()
    {
        var chain = plugin.TendChain;
        if (!chain.Busy && chain.Report.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.Separator();

        if (chain.Busy)
        {
            var line = $"Watering... {chain.Report.Count}/{chain.TotalBeds} | elapsed {chain.Elapsed:mm\\:ss}";
            if (chain.Eta is { } eta)
                line += $" | ETA {eta:mm\\:ss}";
            ImGui.Text(line);
            ImGui.SameLine();
            if (ImGui.Button("Abort"))
                chain.Abort();
        }
        else
        {
            ImGui.Text($"Last run: {chain.LastOutcome}");
        }

        using var child = ImRaii.Child("runlog", new Vector2(0, 110), true);
        if (!child.Success)
            return;

        foreach (var line in chain.Report)
            ImGui.TextDisabled(line);
        if (chain.Busy)
            ImGui.SetScrollHereY(1f);
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

        var sightings = GardenScanner.NearbyEventObjects();

        if (ImGui.Button("Log snapshot"))
        {
            Plugin.Log.Information($"[Recon] zone ({territoryId}) {territoryName}, {sightings.Count} event objects in 40y:");
            foreach (var s in sightings)
                Plugin.Log.Information(
                    $"[Recon] {s.Name} | {s.Kind} | DataId {s.DataId} | {s.Distance:F2}y | targetable={s.Targetable} | pos={s.Object.Position:F1}");
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{sightings.Count} event objects within 40y");

        using var table = ImRaii.Table("sightings", 6,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY,
            new Vector2(0, 200));
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("DataId", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableSetupColumn("Reach", ImGuiTableColumnFlags.WidthFixed, 45f);
        ImGui.TableSetupColumn("##tend", ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var s in sightings)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(s.Name);
            ImGui.TableNextColumn();
            ImGui.Text(s.Kind.ToString());
            ImGui.TableNextColumn();
            ImGui.Text(s.DataId.ToString());
            ImGui.TableNextColumn();
            ImGui.Text($"{s.Distance:F1}y");
            ImGui.TableNextColumn();
            ImGui.TextColored(s.InReach ? Green : Red, s.InReach ? "yes" : "no");
            ImGui.TableNextColumn();
            using var id = ImRaii.PushId((int)s.Object.EntityId);
            using (ImRaii.Disabled(plugin.TendChain.Busy || !s.InReach))
            {
                if (ImGui.Button("Tend"))
                    plugin.TendChain.TendOne(s.Object);
            }
        }
    }
}
