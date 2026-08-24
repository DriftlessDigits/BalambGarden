namespace BalambGarden.Engine.Sync;

/// <summary>What CONFIG remembered about the log the last time it loaded cleanly. The whole
/// instrument rests on an asymmetry: config and the receipt log are separate files, restored
/// separately, copied separately - so when one of them travels back in time or sideways, the
/// other still holds the old truth and the disagreement is visible.
///
/// <para><see cref="MachineHash"/> is opaque here by contract. It is computed and hashed
/// config-side, never leaves the machine, and never rides a wire receipt; the Engine only ever
/// compares two strings for equality and has no idea what they mean.</para></summary>
public sealed record EraMemory(string Era, long Counter, string MachineHash);

/// <summary>The ruling on an era check.</summary>
public enum EraVerdict
{
    /// <summary>The counter's life is intact - keep counting.</summary>
    Keep,

    /// <summary>Mint a fresh era before the next Append.</summary>
    MintFresh,
}

/// <summary>Pure decision logic for the three ways a counter's life can end (identity
/// amendment, ruled 2026-08-23). Wire ids are "user:era:counter"; the counter is only unique
/// within an era, so an era must be retired the instant the counter stops being a reliable
/// forward-only sequence.
///
/// <para>THREE TRIGGER CLASSES:</para>
/// <para>REWIND - a backup restore drops the log back to an older counter, so numbers already
/// spent would be handed out again. Visible as a counter lower than config remembered, or as
/// config and log naming different eras.</para>
/// <para>WIPE - the log is fresh, deleted, or parked, so the counter restarts at zero with no
/// era to restart under. Visible as an empty log era (or as config having no memory at all).</para>
/// <para>DUPLICATE - the install was cloned or migrated to another machine, and BOTH copies now
/// count forward from the same era with the same numbers. Nothing rewinds; the counter looks
/// perfect on both sides. This is why the machine hash exists - it is the only signal that
/// distinguishes a clone from a healthy install.</para>
///
/// <para>FAIL TOWARD MINTING. A spurious mint costs one random slug and a counter restart, both
/// free; a missed mint puts two different facts on the relay under one id, which is
/// unrecoverable. Every check below therefore resolves ambiguity as MintFresh - "no memory"
/// most of all, since an absent memory and a destroyed one look identical.</para></summary>
public static class EraGuard
{
    /// <summary>Rules on whether the log's era may keep counting. Order is deliberate: the
    /// cheapest, most certain evidence first, so the reason string names the real cause rather
    /// than a symptom of it.</summary>
    /// <param name="remembered">Config's memory of the last clean load, or null if it has none.</param>
    /// <param name="logEra">The era the receipt log currently carries; empty if never minted.</param>
    /// <param name="logCounter">The receipt log's current counter.</param>
    /// <param name="currentMachineHash">This machine's opaque fingerprint, hashed config-side.</param>
    public static (EraVerdict Verdict, string Reason) Check(
        EraMemory? remembered, string logEra, long logCounter, string currentMachineHash)
    {
        if (string.IsNullOrEmpty(logEra))
            return (EraVerdict.MintFresh, "fresh log");

        if (remembered is null)
            return (EraVerdict.MintFresh, "no memory");

        if (remembered.Era != logEra)
            return (EraVerdict.MintFresh, "era mismatch");

        if (remembered.MachineHash != currentMachineHash)
            return (EraVerdict.MintFresh, "machine changed");

        if (logCounter < remembered.Counter)
            return (EraVerdict.MintFresh, "counter rewound");

        return (EraVerdict.Keep, "intact");
    }
}
