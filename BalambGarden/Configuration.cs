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

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
