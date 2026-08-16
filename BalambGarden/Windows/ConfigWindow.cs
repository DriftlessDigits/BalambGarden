using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace BalambGarden.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("Balamb Garden Settings###BalambGardenConfig")
    {
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;

        Size = new Vector2(400, 400);
        SizeCondition = ImGuiCond.Always;

        configuration = Plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        // Flags must be added or removed before Draw() is being called, or they won't apply
        if (configuration.IsConfigWindowMovable)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }
    }

    public override void Draw()
    {
        DrawBehaviour();
        ImGui.Separator();
        DrawSensing();
        ImGui.Separator();
        ImGui.Text("Tend pacing");

        var pace = configuration.TendPaceMS;
        if (ImGui.SliderInt("Pace (ms)", ref pace, 250, 3000))
        {
            configuration.TendPaceMS = pace;
            configuration.Save();
        }

        var postTend = configuration.PostTendDelayMS;
        if (ImGui.SliderInt("Between beds (ms)", ref postTend, 3000, 15000))
        {
            configuration.PostTendDelayMS = postTend;
            configuration.Save();
        }

        var postTendJitter = configuration.PostTendJitterMS;
        if (ImGui.SliderInt("Between-beds jitter (+/- ms)", ref postTendJitter, 0, 3000))
        {
            configuration.PostTendJitterMS = postTendJitter;
            configuration.Save();
        }

        var jitter = configuration.JitterMS;
        if (ImGui.SliderInt("Jitter (+/- ms)", ref jitter, 0, 1500))
        {
            configuration.JitterMS = jitter;
            configuration.Save();
        }
    }

    /// <summary>How far the dashboard's patch sweep reaches. Wider finds a far corner of a
    /// big plot; too wide starts finding the neighbour's beds (their twin ordinal sat at
    /// 37.9y on 08-14), and a patch you cannot claim is worse than one you have to walk to.
    /// Recon's own 40y sweep is fixed and untouched by this - the instrument is supposed to
    /// see more than the app.</summary>
    private void DrawSensing()
    {
        ImGui.Text("Sensing");

        var radius = configuration.PatchScanRadius;
        if (ImGui.SliderFloat("Patch scan radius (y)", ref radius, 5f, 40f, "%.0f"))
        {
            configuration.PatchScanRadius = radius;
            configuration.Save();
        }
    }

    /// <summary>The switches that decide when the plugin speaks, what it writes down, and
    /// how much of the planting picker it fills. What gets recorded is not among them
    /// (08-15): the game-granted roster decides that, not a checkbox.</summary>
    private void DrawBehaviour()
    {
        var nudge = configuration.NudgeEnabled;
        if (ImGui.Checkbox("Arrival nudge - one chat line when a garden needs you", ref nudge))
        {
            configuration.NudgeEnabled = nudge;
            configuration.Save();
        }

        // The nudge is the plugin's only unprompted line, so the name it announces itself
        // under belongs to the player, not to a string literal in the derivation.
        using (ImRaii.Disabled(!configuration.NudgeEnabled))
        {
            var label = configuration.NudgeLabel;
            ImGui.SetNextItemWidth(140f);
            ImGui.InputText("Nudge prefix", ref label, 24);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                configuration.NudgeLabel = label.Trim();
                configuration.Save();
            }
        }

        var trail = configuration.TrailEnabled;
        if (ImGui.Checkbox("Debug trail - append receipts to trail.jsonl", ref trail))
        {
            configuration.TrailEnabled = trail;
            configuration.Save();
        }

        var autoFill = configuration.AutoFillPicker;
        if (ImGui.Checkbox("Fill the planting picker for me - soil, seed, Confirm", ref autoFill))
        {
            configuration.AutoFillPicker = autoFill;
            configuration.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "The chain clicks the two slots and picks the soil and seed you chose.\n"
                + "If anything about the picker isn't what it expects, it stops clicking and\n"
                + "waits for you exactly like it used to - the run keeps going either way.\n"
                + "The sow confirmation is still read and checked before anything is planted.");
    }
}
