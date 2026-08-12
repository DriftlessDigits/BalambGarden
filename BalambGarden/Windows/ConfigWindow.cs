using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BalambGarden.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("Balamb Garden Settings###BalambGardenConfig")
    {
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;

        Size = new Vector2(320, 200);
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

        var enableJitter = configuration.EnableJitter;
        if (ImGui.Checkbox("Jitter", ref enableJitter))
        {
            configuration.EnableJitter = enableJitter;
            configuration.Save();
        }

        if (configuration.EnableJitter)
        {
            var jitter = configuration.JitterMS;
            if (ImGui.SliderInt("Jitter (+/- ms)", ref jitter, 0, 1500))
            {
                configuration.JitterMS = jitter;
                configuration.Save();
            }
        }
    }
}
