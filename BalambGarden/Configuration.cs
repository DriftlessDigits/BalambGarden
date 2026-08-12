using Dalamud.Configuration;
using System;

namespace BalambGarden;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;

    // Pacing: the chain acts at human tempo, not frame tempo. Every action waits
    // TendPaceMS +/- JitterMS (uniform) since the previous one.
    public bool EnableJitter { get; set; } = true;
    public int TendPaceMS { get; set; } = 750;
    public int JitterMS { get; set; } = 400;

    // Between beds: the watering animation outlives the dialogue, so the gap from one
    // bed's tend to the next bed's reach is its own, much longer delay (Sam's ruling
    // 2026-08-11: 8s +/- 1s).
    public int PostTendDelayMS { get; set; } = 8000;
    public int PostTendJitterMS { get; set; } = 1000;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
