using System;
using System.Collections.Generic;
using System.Linq;
using BalambGarden.Engine.Census;
using BalambGarden.Game;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using ECommons;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BalambGarden.Chains;

/// <summary>
/// Drives tend interactions: target + interact with a bed, pick "Tend Crop" from its
/// menu, click through the resulting dialogue. Tri-state steps in the Scrooge
/// GameNavigation idiom: true = done, false = not ready yet (retry).
///
/// <para>Acting IS censusing (spec, 08-13): the menu names the bed ("2nd Bed, 1st
/// Patch", index mapped live 08-11) and the status Talk names the plant, so every
/// completed tend is a receipt routed through <see cref="CensusPump"/> - the only
/// path that binds a patch or claims a bed.</para>
/// </summary>
internal sealed unsafe class TendChain : ChainBase
{
    // Census state for the bed currently being tended: the status Talk names the
    // plant, the menu names the bed. The patch identity rides the receipt header,
    // never the object's position (the POC's positional ledger is gone).
    private string _currentPlant = "";

    // The tend receipt, held between the menu selection and the end of the bed's
    // dialogue. Selecting "Tend Crop" is the FIRE; the conversation going quiet is the
    // CONFIRMATION (spec: a status surface never claims what did not complete). A bed
    // whose dialogue never finishes leaves this unfired, so nothing reaches the ledger
    // and the ETA anchors at the true bed boundary.
    private Func<string>? _pendingReceipt;

    internal void TendOne(BedObject bed) => Tend([bed]);
    internal void TendPatch(PatchGroup patch) => Tend(patch.Beds);
    internal void TendAll(IEnumerable<PatchGroup> patches)
        => Tend(patches.SelectMany(p => p.Beds).ToList());

    private void Tend(List<BedObject> beds)
    {
        if (!BeginRun(beds.Count,
                beds.Count == 1 ? "tending bed..." : $"watering {beds.Count} beds..."))
            return;

        for (var i = 0; i < beds.Count; i++)
        {
            var bed = beds[i];
            // First bed reacts at button tempo; every later bed waits out the previous
            // watering animation.
            TaskManager.DelayNext(i == 0
                ? ApplyJitter(Plugin.Configuration.TendPaceMS)
                : ApplyJitter(Plugin.Configuration.PostTendDelayMS, Plugin.Configuration.PostTendJitterMS));
            var label = $"bed {i + 1}/{beds.Count}";
            TaskManager.Enqueue(() => CheckStop(label), $"gate {i}");
            TaskManager.Enqueue(() => Interact(bed.Object), $"interact {i}");
            // A growing crop opens with a status Talk ("X is doing well") BEFORE the
            // menu - the plant name arrives here, then the menu. Click dialogue until
            // the menu shows.
            TaskManager.Enqueue(AdvanceToMenu, $"advance {i}");
            TaskManager.Enqueue(TendOrQuit, $"tend {i}");
            TaskManager.Enqueue(FinishDialogue, $"finish {i}");
        }

        var total = beds.Count;
        TaskManager.Enqueue(() =>
        {
            var tended = Report.Count(r => r.Contains("- done", StringComparison.Ordinal));
            LastOutcome = $"done: {tended}/{total} tended";
            foreach (var line in Report)
                Plugin.Log.Information($"[TendChain] report: {line}");
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
            RecordOutcome("(bed vanished): skipped");
            Abort("bed list went stale (zone change?)");
            return true;
        }

        var targets = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
        if (targets == null)
            return false;

        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)bed.Address;
        if (native == null)
            return false;

        _currentPlant = "";
        // A receipt never survives into the next bed: an unfired one belongs to a
        // conversation that did not finish, and dropping it is the honest outcome.
        _pendingReceipt = null;

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

    /// <summary>Clicks through whatever dialogue follows the action; done when quiet.
    /// Quiet is the confirmation: the pending receipt routes HERE, not at selection.</summary>
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

        if (_pendingReceipt is { } receipt)
        {
            _pendingReceipt = null;
            RecordOutcome(receipt());
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
        var hasHarvest = false;
        foreach (var entry in menu.Entries)
        {
            if (entry.Text.Contains("Tend", StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.Information($"[TendChain] selecting '{entry.Text}' for {header} ({_currentPlant})");
                entry.Select();
                Acted();
                // The receipt IS the census event: header + plant route through the pump,
                // which binds the patch (if unbound) and claims the bed. Held until the
                // dialogue goes quiet - selecting is firing, not finishing.
                var plant = _currentPlant;
                _pendingReceipt = () => CensusPump.OnBedReceipt(ReceiptVerb.Tend, header, plant);
                return true;
            }

            if (entry.Text.Contains("Harvest", StringComparison.OrdinalIgnoreCase))
                hasHarvest = true;
        }

        // No tend on offer: quit the menu honestly and keep going - one odd bed must
        // not strand the rest of the patch. A Harvest entry with no Tend means ripe,
        // which is itself a stage-4 sighting worth recording.
        foreach (var entry in menu.Entries)
        {
            if (entry.Text.Contains("Quit", StringComparison.OrdinalIgnoreCase))
            {
                entry.Select();
                Acted();
                RecordOutcome(hasHarvest
                    ? CensusPump.OnRipeSkip(header, _currentPlant)
                    : $"{header}: skipped (no tend option - empty or no rights?)");
                return true;
            }
        }

        // A menu with neither Tend nor Quit is not a garden bed conversation at all.
        RecordOutcome($"{header}: unrecognized menu - stopped");
        Abort("unrecognized menu");
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
