using System;
using System.Collections.Generic;
using ECommons;
using ECommons.Automation.LegacyTaskManager;

namespace BalambGarden.Chains;

/// <summary>Paced dialogue-chain framework (Scrooge lineage via the POC TendChain):
/// human tempo + jitter, telemetry for the run log, occupied-player guard, derived
/// task ceiling, clean stop with a stated reason.</summary>
internal abstract class ChainBase : IDisposable
{
    protected readonly TaskManager TaskManager = new()
    {
        TimeLimitMS = 10000,
        AbortOnTimeout = true,
    };

    private readonly Random random = new();
    private DateTime nextActionAt = DateTime.MinValue;
    private DateTime lastUnitAt;

    internal bool Busy => TaskManager.IsBusy;
    internal string LastOutcome { get; private protected set; } = "idle";
    internal List<string> Report { get; } = [];
    internal DateTime RunStartUtc { get; private set; }
    internal int TotalUnits { get; private set; }

    internal TimeSpan Elapsed => Busy ? DateTime.UtcNow - RunStartUtc : TimeSpan.Zero;

    /// <summary>Countdown ETA, pace frozen at unit boundaries (POC/Scrooge ruling -
    /// a live-clock pace bills the current unit's wait to every future unit).</summary>
    internal TimeSpan? Eta
    {
        get
        {
            if (!Busy || TotalUnits == 0)
                return null;
            var done = Report.Count;
            var remaining = TotalUnits - done;
            if (remaining <= 0)
                return null;

            double msPerUnit;
            DateTime anchor;
            if (done > 0)
            {
                msPerUnit = (lastUnitAt - RunStartUtc).TotalMilliseconds / done;
                anchor = lastUnitAt;
            }
            else
            {
                msPerUnit = SeedMsPerUnit();
                anchor = RunStartUtc;
            }

            var spent = (DateTime.UtcNow - anchor).TotalMilliseconds;
            return TimeSpan.FromMilliseconds(Math.Max(0, (msPerUnit * remaining) - spent));
        }
    }

    /// <summary>Pre-first-completion ETA seed; chains override with their own shape.</summary>
    protected virtual double SeedMsPerUnit()
        => Plugin.Configuration.PostTendDelayMS + (4.0 * Plugin.Configuration.TendPaceMS);

    protected bool PaceReady() => DateTime.UtcNow >= nextActionAt;

    protected void Acted()
        => nextActionAt = DateTime.UtcNow.AddMilliseconds(ApplyJitter(Plugin.Configuration.TendPaceMS));

    protected int ApplyJitter(int baseMS) => ApplyJitter(baseMS, Plugin.Configuration.JitterMS);

    // No global jitter kill-switch (Scrooge ruling): zeroing a slider is deliberate.
    protected int ApplyJitter(int baseMS, int jitterMS)
    {
        if (jitterMS <= 0)
            return baseMS;
        var offset = (int)(((random.NextDouble() * 2.0) - 1.0) * jitterMS);
        return Math.Max(250, baseMS + offset);
    }

    /// <summary>Occupied guard + telemetry reset + task ceiling derived above the
    /// longest tunable step. False = refused (reason already in LastOutcome).</summary>
    protected bool BeginRun(int units, string startOutcome)
    {
        if (TaskManager.IsBusy || units == 0)
            return false;
        if (GenericHelpers.IsOccupied())
        {
            LastOutcome = "can't start: you're busy (in a dialog, cutscene, or event)";
            return false;
        }

        TaskManager.TimeLimitMS = Math.Max(
            15000, Plugin.Configuration.PostTendDelayMS + Plugin.Configuration.PostTendJitterMS + 5000);
        stopRequested = false;
        Report.Clear();
        RunStartUtc = DateTime.UtcNow;
        lastUnitAt = RunStartUtc;
        TotalUnits = units;
        LastOutcome = startOutcome;
        return true;
    }

    /// <summary>One unit's outcome line: feeds the report and anchors the ETA.</summary>
    protected void RecordOutcome(string line)
    {
        Report.Add(line);
        lastUnitAt = DateTime.UtcNow;
    }

    private bool stopRequested;

    /// <summary>The user's stop: honored at the NEXT unit boundary, never mid-dialogue
    /// (spec: interruption stops clean at a bed boundary). Chains call
    /// CheckStop() as the first step of every unit.</summary>
    internal void RequestStop() => stopRequested = true;

    /// <summary>Unit-boundary gate. Enqueue as each unit's first task: true = carry on,
    /// aborts the run cleanly when a stop was requested.</summary>
    protected bool CheckStop(string unitLabel)
    {
        if (!stopRequested)
            return true;
        Abort($"stopped by user before {unitLabel}");
        return true;   // this task completed; the queue behind it is gone
    }

    /// <summary>Hard stop with a stated reason - for stale state and broken menus,
    /// where continuing would be worse than an abrupt end. User stops go through
    /// RequestStop instead.</summary>
    internal void Abort(string reason = "aborted")
    {
        TaskManager.Abort();
        LastOutcome = $"stopped at {Report.Count}/{TotalUnits} - {reason}";
    }

    public void Dispose() => TaskManager.Abort();
}
