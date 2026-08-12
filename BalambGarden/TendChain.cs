using System;
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

    internal bool Busy
        => _taskManager.IsBusy;

    internal string LastOutcome { get; private set; } = "idle";

    public void Dispose()
        => _taskManager.Abort();

    internal void Abort()
    {
        _taskManager.Abort();
        LastOutcome = "aborted";
    }

    internal void TendOne(IGameObject bed)
    {
        if (_taskManager.IsBusy)
            return;

        var name = bed.Name.TextValue;
        LastOutcome = $"tending '{name}'...";
        _taskManager.Enqueue(() => Interact(bed), "interact");
        _taskManager.Enqueue(SelectTend, "select tend");
        _taskManager.Enqueue(ClickThroughTalk, "talk");
        _taskManager.Enqueue(() => { LastOutcome = $"tended '{name}'"; return true; }, "done");
    }

    /// <summary>Target-then-interact, the game's own flow (Scrooge GameSafe pattern).</summary>
    private static bool? Interact(IGameObject bed)
    {
        var targets = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
        if (targets == null)
            return false;

        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)bed.Address;
        if (native == null)
            return false;

        targets->Target = native;
        targets->InteractWithObject(native, false);
        return true;
    }

    private bool? SelectTend()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
            return false;

        DumpStrings(addon, "SelectString");

        var menu = new AddonMaster.SelectString(addon);
        foreach (var entry in menu.Entries)
        {
            if (entry.Text.Contains("Tend", StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.Information($"[TendChain] selecting '{entry.Text}'");
                entry.Select();
                return true;
            }
        }

        // No tend on offer (empty bed, ripe crop, or no permission): report honestly
        // and stop; the menu stays for the player to act on.
        LastOutcome = "no 'Tend' option in menu (empty bed, ripe, or no rights?) - left menu open";
        _taskManager.Abort();
        return true;
    }

    private bool? ClickThroughTalk()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
            return false;

        DumpStrings(addon, "Talk");
        new AddonMaster.Talk((nint)addon).Click();
        return true;
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
