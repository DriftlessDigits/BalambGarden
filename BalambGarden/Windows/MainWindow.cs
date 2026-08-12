using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace BalambGarden.Windows;

/// <summary>
/// Recon window: lists nearby event objects (the kinds housing interactables come in)
/// with kind, DataId, distance, and reach - and offers a per-row Tend test that runs
/// the chain on that object. This is the instrument for the live session at the
/// garden; the real gardening UI grows out of what it teaches us.
/// </summary>
public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("Balamb Garden##BalambGardenMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 320),
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

        ImGui.SameLine();
        ImGui.TextDisabled($"| chain: {plugin.TendChain.LastOutcome}");
        if (plugin.TendChain.Busy)
        {
            ImGui.SameLine();
            if (ImGui.Button("Abort"))
                plugin.TendChain.Abort();
        }

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

        ImGui.Spacing();

        using var table = ImRaii.Table("sightings", 6,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY);
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
            ImGui.TextColored(
                s.InReach ? new Vector4(0.4f, 1f, 0.4f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f),
                s.InReach ? "yes" : "no");
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
