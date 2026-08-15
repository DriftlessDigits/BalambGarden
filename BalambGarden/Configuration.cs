using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace BalambGarden;

/// <summary>
/// One bed's census record: what the chain last saw there and when it watered it.
/// Keyed by territory + patch centre + bed label (same bed label repeats across
/// plots; the patch centre disambiguates).
/// </summary>
[Serializable]
public class BedRecord
{
    public uint Territory { get; set; }
    public float PatchX { get; set; }
    public float PatchZ { get; set; }
    public string Bed { get; set; } = "";
    public string Plant { get; set; } = "";
    public DateTime LastTendedUtc { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool IsConfigWindowMovable { get; set; } = true;

    // Pacing: the chain acts at human tempo, not frame tempo. Every action waits
    // TendPaceMS +/- JitterMS (uniform) since the previous one. No global jitter
    // kill-switch (Scrooge ruling: pacing must not couple to a plugin-wide toggle).
    public int TendPaceMS { get; set; } = 750;
    public int JitterMS { get; set; } = 400;

    // Between beds: the watering animation outlives the dialogue, so the gap from one
    // bed's tend to the next bed's reach is its own, much longer delay (Sam's ruling
    // 2026-08-11: 8s +/- 1s).
    public int PostTendDelayMS { get; set; } = 8000;
    public int PostTendJitterMS { get; set; } = 1000;

    // v2 census behavior (spec: claim-on-action, arrival nudge, debug trail).
    public bool ClaimOnAction { get; set; } = true;
    public bool NudgeEnabled { get; set; } = true;
    public bool TrailEnabled { get; set; } = true;

    /// <summary>What the arrival nudge calls itself in chat. The chat log is the player's
    /// room; the name we speak under in it is theirs to set.</summary>
    public string NudgeLabel { get; set; } = "Balamb";

    /// <summary>How far the working patch sweep looks, in yalms. Sam's ruling 08-14 set the
    /// 20y default (the neighbour's twin ordinal sat at 37.9y, and a far own patch simply
    /// reappears when you walk toward it); it is a setting because a sprawling plot and a
    /// small one want different answers. Recon keeps its own fixed 40y view on purpose.</summary>
    public float PatchScanRadius { get; set; } = 20f;

    // The garden ledger: every tended bed's latest census record.
    public List<BedRecord> Ledger { get; set; } = [];

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
