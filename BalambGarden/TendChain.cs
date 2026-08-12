using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BalambGarden;

/// <summary>
/// Drives a single tend interaction: target + interact with a bed, pick "Tend Crop"
/// from its menu, click through the resulting dialogue. Tri-state steps in the
/// Scrooge GameNavigation idiom: true = done, false = not ready yet (retry).
///
/// <para>Recon-first: every addon this chain meets gets its string values logged
/// verbatim with their indices - that text is the census payload (plant name via the
/// dialogue, patch/bed via the menu description) the tracking layer will later parse,
/// and the indices are unknowns until a live tend maps them.</para>
/// </summary>
internal sealed unsafe class TendChain : IDisposable
{
    private readonly TaskManager _taskManager = new()
    {
        TimeLimitMS = 10000,
        AbortOnTimeout = true,
    };

    private readonly Random _random = new();

    // Pacing gate: no two chain actions inside one jittered gap. Without it the
    // chain runs at frame tempo (the first live tend did the whole conversation
    // in ~130ms, including double-clicking one Talk box in 16ms).
    private DateTime _nextActionAt = DateTime.MinValue;

    // Census state for the bed currently being tended: the status Talk names the
    // plant, the menu names the bed, the object's position names the patch.
    private string _currentPlant = "";
    private System.Numerics.Vector3 _currentBedPos;

    private bool PaceReady()
        => DateTime.UtcNow >= _nextActionAt;

    private void Acted()
        => _nextActionAt = DateTime.UtcNow.AddMilliseconds(ApplyJitter(Plugin.Configuration.TendPaceMS));

    /// <summary>Base +/- uniform jitter, floored (Scrooge ApplyJitter shape).</summary>
    private int ApplyJitter(int baseMS)
        => ApplyJitter(baseMS, Plugin.Configuration.JitterMS);

    private int ApplyJitter(int baseMS, int jitterMS)
    {
        if (!Plugin.Configuration.EnableJitter || jitterMS <= 0)
            return baseMS;

        var offset = (int)(((_random.NextDouble() * 2.0) - 1.0) * jitterMS);
        return Math.Max(250, baseMS + offset);
    }

    internal bool Busy
        => _taskManager.IsBusy;

    internal string LastOutcome { get; private set; } = "idle";

    /// <summary>Per-bed outcomes of the last run, in tend order.</summary>
    internal List<string> Report { get; } = [];

    // Run telemetry for the progress panel (the Scrooge run-log treatment):
    // start stamp + totals feed elapsed, n-of-m, and a done-rate ETA.
    internal DateTime RunStartUtc { get; private set; }
    internal int TotalBeds { get; private set; }

    internal TimeSpan Elapsed
        => Busy ? DateTime.UtcNow - RunStartUtc : TimeSpan.Zero;

    internal TimeSpan? Eta
    {
        get
        {
            if (!Busy || Report.Count == 0 || TotalBeds == 0)
                return null;

            var perBed = (DateTime.UtcNow - RunStartUtc) / Report.Count;
            return perBed * (TotalBeds - Report.Count);
        }
    }

    public void Dispose()
        => _taskManager.Abort();

    internal void Abort()
    {
        _taskManager.Abort();
        LastOutcome = "aborted";
    }

    internal void TendOne(IGameObject bed)
        => Tend([bed]);

    internal void TendPatch(PatchSighting patch)
        => Tend(patch.Beds.Select(b => b.Object).ToList());

    internal void TendAll(IEnumerable<PatchSighting> patches)
        => Tend(patches.SelectMany(p => p.Beds).Select(b => b.Object).ToList());

    private void Tend(List<IGameObject> beds)
    {
        if (_taskManager.IsBusy || beds.Count == 0)
            return;

        Report.Clear();
        RunStartUtc = DateTime.UtcNow;
        TotalBeds = beds.Count;
        LastOutcome = beds.Count == 1 ? "tending bed..." : $"watering {beds.Count} beds...";

        for (var i = 0; i < beds.Count; i++)
        {
            var bed = beds[i];
            // First bed reacts at button tempo; every later bed waits out the previous
            // watering animation.
            _taskManager.DelayNext(i == 0
                ? ApplyJitter(Plugin.Configuration.TendPaceMS)
                : ApplyJitter(Plugin.Configuration.PostTendDelayMS, Plugin.Configuration.PostTendJitterMS));
            _taskManager.Enqueue(() => Interact(bed), $"interact {i}");
            // A growing crop opens with a status Talk ("X is doing well") BEFORE the
            // menu - the plant name arrives here, then the menu. Click dialogue until
            // the menu shows.
            _taskManager.Enqueue(AdvanceToMenu, $"advance {i}");
            _taskManager.Enqueue(TendOrQuit, $"tend {i}");
            _taskManager.Enqueue(FinishDialogue, $"finish {i}");
        }

        var total = beds.Count;
        _taskManager.Enqueue(() =>
        {
            var tended = Report.Count(r => r.EndsWith("- tended", StringComparison.Ordinal));
            LastOutcome = $"done: {tended}/{total} tended";
            foreach (var line in Report)
                Plugin.Log.Information($"[TendChain] report: {line}");
            Plugin.Configuration.Save();
            return true;
        }, "report");
    }

    /// <summary>Target-then-interact, the game's own flow (Scrooge GameSafe pattern).</summary>
    private bool? Interact(IGameObject bed)
    {
        // An object handle from before a zone change is a pointer into a world that
        // no longer exists (Scrooge's lesson) - skip dead beds instead of touching them.
        if (!bed.IsValid())
        {
            Report.Add("(bed vanished): skipped");
            LastOutcome = "aborted: bed list went stale (zone change?)";
            _taskManager.Abort();
            return true;
        }

        var targets = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
        if (targets == null)
            return false;

        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)bed.Address;
        if (native == null)
            return false;

        _currentPlant = "";
        _currentBedPos = bed.Position;

        targets->Target = native;
        targets->InteractWithObject(native, false);
        return true;
    }

    /// <summary>Clicks through any status dialogue until the bed's menu is up.</summary>
    private bool? AdvanceToMenu()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var menu)
            && GenericHelpers.IsAddonReady(menu))
            return true;

        if (PaceReady()
            && GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var talk)
            && GenericHelpers.IsAddonReady(talk))
        {
            DumpStrings(talk, "Talk");
            CapturePlantName(talk);
            new AddonMaster.Talk((nint)talk).Click();
            Acted();
        }

        return false;
    }

    /// <summary>The status Talk's first line is the plant name ("Curiel Root\n...").</summary>
    private void CapturePlantName(AtkUnitBase* addon)
    {
        var text = ReadStringValue(addon, 0);
        if (text.Length == 0)
            return;

        var newline = text.IndexOf('\n');
        var plant = (newline > 0 ? text[..newline] : text).Trim();
        if (plant.Length > 0)
            _currentPlant = plant;
    }

    /// <summary>Clicks through whatever dialogue follows the action; done when quiet.</summary>
    private bool? FinishDialogue()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var talk)
            && GenericHelpers.IsAddonReady(talk))
        {
            if (PaceReady())
            {
                DumpStrings(talk, "Talk");
                new AddonMaster.Talk((nint)talk).Click();
                Acted();
            }

            return false;
        }

        return true;
    }

    private bool? TendOrQuit()
    {
        if (!PaceReady())
            return false;

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
            return false;

        DumpStrings(addon, "SelectString");

        // AtkValues[2] = the bed identity ("2nd Bed, 1st Patch") - index mapped live 2026-08-11.
        var header = ReadBedHeader(addon);

        var menu = new AddonMaster.SelectString(addon);
        foreach (var entry in menu.Entries)
        {
            if (entry.Text.Contains("Tend", StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.Information($"[TendChain] selecting '{entry.Text}' for {header} ({_currentPlant})");
                entry.Select();
                Acted();
                RecordTend(header);
                Report.Add($"{header}: {(_currentPlant.Length > 0 ? _currentPlant : "?")} - tended");
                return true;
            }
        }

        // No tend on offer (empty bed, ripe crop, or no permission): quit the menu
        // honestly and keep going - one odd bed must not strand the rest of the patch.
        foreach (var entry in menu.Entries)
        {
            if (entry.Text.Contains("Quit", StringComparison.OrdinalIgnoreCase))
            {
                entry.Select();
                Acted();
                Report.Add($"{header}: skipped (no tend option - empty, ripe, or no rights?)");
                return true;
            }
        }

        // A menu with neither Tend nor Quit is not a garden bed conversation at all.
        Report.Add($"{header}: unrecognized menu - stopped");
        LastOutcome = "aborted: unrecognized menu";
        _taskManager.Abort();
        return true;
    }

    private static string ReadBedHeader(AtkUnitBase* addon)
    {
        var text = ReadStringValue(addon, 2);
        return text.Length > 0 ? text : "(unknown bed)";
    }

    /// <summary>Reads one string-typed AtkValue defensively; empty string when absent.</summary>
    private static string ReadStringValue(AtkUnitBase* addon, int index)
    {
        try
        {
            if (addon->AtkValuesCount > index
                && addon->AtkValues[index].Type is AtkValueType.String or AtkValueType.ConstString
                    or AtkValueType.WideString or AtkValueType.ManagedString
                && addon->AtkValues[index].String.Value != null)
                return MemoryHelper.ReadSeStringNullTerminated((nint)addon->AtkValues[index].String.Value).GetText();
        }
        catch
        {
            // unreadable value = absent value
        }

        return "";
    }

    /// <summary>Upserts this bed's ledger record: same territory + patch centre + bed label.</summary>
    private void RecordTend(string bedLabel)
    {
        var territory = Plugin.ClientState.TerritoryType;
        var ledger = Plugin.Configuration.Ledger;
        var record = ledger.FirstOrDefault(r =>
            r.Territory == territory
            && r.Bed == bedLabel
            && Math.Abs(r.PatchX - _currentBedPos.X) < 0.5f
            && Math.Abs(r.PatchZ - _currentBedPos.Z) < 0.5f);

        if (record == null)
        {
            record = new BedRecord
            {
                Territory = territory,
                PatchX = _currentBedPos.X,
                PatchZ = _currentBedPos.Z,
                Bed = bedLabel,
            };
            ledger.Add(record);
        }

        if (_currentPlant.Length > 0)
            record.Plant = _currentPlant;
        record.LastTendedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Recon instrument: logs every string-typed AtkValue with its index so live tends
    /// map which index carries what. Read-only; never throws into the chain.
    /// </summary>
    private static void DumpStrings(AtkUnitBase* addon, string tag)
    {
        try
        {
            for (var i = 0; i < addon->AtkValuesCount; i++)
            {
                var value = addon->AtkValues[i];
                if (value.Type is not (AtkValueType.String or AtkValueType.ConstString
                    or AtkValueType.WideString or AtkValueType.ManagedString))
                    continue;
                if (value.String.Value == null)
                    continue;

                var text = MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).GetText();
                if (text.Length > 0)
                    Plugin.Log.Information($"[TendChain] {tag} AtkValues[{i}] = '{text}'");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[TendChain] {tag} string dump failed: {ex.Message}");
        }
    }
}
