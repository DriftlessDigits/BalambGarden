using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace BalambGarden.Windows;

/// <summary>
/// The run log: pops open when a watering run starts (the buttons open it), shows
/// live progress (n-of-m, elapsed, done-rate ETA, Abort) over the per-bed feed,
/// and keeps the last run's report around until the next one replaces it.
/// </summary>
public class RunLogWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public RunLogWindow(Plugin plugin)
        : base("Balamb Garden - Run Log##BalambGardenRunLog")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 220),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var chain = plugin.TendChain;

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

        ImGui.Spacing();

        using var child = ImRaii.Child("runlog", Vector2.Zero, true);
        if (!child.Success)
            return;

        foreach (var line in chain.Report)
            ImGui.TextDisabled(line);
        if (chain.Busy)
            ImGui.SetScrollHereY(1f);
    }
}
