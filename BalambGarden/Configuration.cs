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
    // bed's tend to the next bed's reach is its own, much longer delay (Drift's ruling
    // 2026-08-11: 8s +/- 1s).
    public int PostTendDelayMS { get; set; } = 8000;
    public int PostTendJitterMS { get; set; } = 1000;

    // v2 census behavior (spec: arrival nudge, debug trail). Claim-as-I-go retired
    // 2026-08-15 - the roster decides scope, sightings do the recording.
    public bool NudgeEnabled { get; set; } = true;
    public bool TrailEnabled { get; set; } = true;

    /// <summary>What the arrival nudge calls itself in chat. The chat log is the player's
    /// room; the name we speak under in it is theirs to set.</summary>
    public string NudgeLabel { get; set; } = "Balamb";

    /// <summary>How the dashboard tells time: 12-hour with am/pm (Drift's default) or 24h.
    /// Display only - every stored timestamp stays UTC regardless.</summary>
    public bool TwelveHourClock { get; set; } = true;

    /// <summary>Whether the chain fills the planting picker's soil and seed slots itself
    /// instead of standing there while you do it by hand. It is a switch and not a promise:
    /// a fill that cannot proceed stops driving and the step becomes the hand-filled one it
    /// has always been, so turning this off costs nothing but the convenience.</summary>
    public bool AutoFillPicker { get; set; } = true;

    /// <summary>The plant-flow recon INTENT: "record when I'm gardening." Debug builds arm
    /// the watcher within 4.6y of a pot and drop it on rezone (Drift's ruling 08-15), so a
    /// capture holds gardening rather than a night of quest dialogue. Persisted because
    /// the runtime flag dies on every hot-load (two captures lost in one day to reset
    /// toggles). Release carries no watcher at all.</summary>
    public bool WatchPlantFlow { get; set; }

    /// <summary>How far the working patch sweep looks, in yalms. Drift's ruling 08-14 set the
    /// 20y default (the neighbour's twin ordinal sat at 37.9y, and a far own patch simply
    /// reappears when you walk toward it); it is a setting because a sprawling plot and a
    /// small one want different answers. Recon keeps its own fixed 40y view on purpose.</summary>
    public float PatchScanRadius { get; set; } = 20f;

    /// <summary>The household passphrase - sync's on-switch and its only secret (ruling 16 +
    /// the 2026-08-28 admin/user split). Empty = sync fully off, zero network surface. It
    /// exists ONLY here: never logged, never displayed back, never sent anywhere - HKDF
    /// derives the relay token and naming key from it at use time, and the config UI shows
    /// only the relay FINGERPRINT (one-way twice) for the admin to copy server-side. Plain
    /// JSON on disk is ruled acceptable: anyone at the keyboard is outside the threat model.</summary>
    public string SyncPassphrase { get; set; } = "";

    /// <summary>This install's identity - the user third of the wire id "user:era:counter"
    /// (identity amendment, ruled 2026-08-23). Minted once, empty until then, and never
    /// re-minted: it is what marks a receipt mine-vs-external forever, on both sides of a
    /// sync. Derived from nothing (SyncId.NewSlug), which is a privacy property - a slug that
    /// could be recomputed from a character name would identify its owner to anyone holding
    /// the name, and this travels on every receipt.</summary>
    public string SyncUserId { get; set; } = "";

    /// <summary>Config's MEMORY of the receipt log's last clean state - the era it was
    /// counting under, the number it had reached, and the machine it was on. Not a copy of
    /// the log and never read as one: it is the rewind/wipe/duplicate detector. Config and the
    /// receipt log are separate files, restored separately and copied separately, so when one
    /// travels back in time or sideways the other still holds the old truth and EraGuard sees
    /// the disagreement at load. Refreshed on every receipt save (GardenService.SaveReceipts),
    /// because a stale memory reads a healthy counter as a rewind.</summary>
    public string SyncEraRemembered { get; set; } = "";

    /// <inheritdoc cref="SyncEraRemembered"/>
    public long SyncCounterRemembered { get; set; }

    /// <summary>The machine memory of the trio above - the only signal that tells a cloned
    /// install from a healthy one, since a clone counts forward perfectly on both copies.
    /// A LOCAL fingerprint by contract: hashed at the source (see MachineStamp), never logged
    /// raw, and it NEVER rides a wire receipt. The Engine only ever compares it for equality
    /// and has no idea what it means.</summary>
    public string SyncMachineHash { get; set; } = "";

    // The garden ledger: every tended bed's latest census record.
    public List<BedRecord> Ledger { get; set; } = [];

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
