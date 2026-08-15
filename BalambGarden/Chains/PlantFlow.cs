#if DEBUG
using System;
using System.Collections.Generic;
using Dalamud.Memory;
using ECommons;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BalambGarden.Chains;

/// <summary>
/// Recon instrument for the sow flow (debug builds only - the whole class is behind
/// <c>#if DEBUG</c>, so a Release plugin carries none of it).
///
/// <para>Tend is mapped; planting is not. Sowing walks a different set of addons
/// (a soil picker, a seed picker, a confirm) and we do not yet know which AtkValue
/// index carries the bed name, the item id, or the confirm button. This watcher does
/// no clicking and makes no claims - it stands next to the flow while Sam plays it by
/// hand and writes down what the addons actually held. The resulting capture, not this
/// code's guesses, is the binding authority for the sow chain's constants.</para>
///
/// <para>Read-only and defensive throughout: a recon tool that throws would break the
/// very interaction it is trying to observe.</para>
/// </summary>
internal static unsafe class PlantFlow
{
    /// <summary>Addons the sow flow is known or suspected to walk through. Ints are
    /// dumped for the two that carry structured selection state; the rest are dialogue
    /// surfaces where only the text matters.</summary>
    private static readonly string[] WatchedAddons =
        ["HousingGardening", "ContextIconMenu", "SelectYesno", "SelectString", "Talk"];

    private static readonly HashSet<string> DumpInts =
        ["HousingGardening", "ContextIconMenu"];

    /// <summary>Last dumped shape per addon. An addon that is merely still open has not
    /// said anything new, and at pump tempo it would say it hundreds of times - so a
    /// dump only repeats when the addon's shape changes (a new open, a new page, a
    /// different selection).</summary>
    private static readonly Dictionary<string, int> lastShape = [];

    internal static bool Watching { get; private set; }

    internal static void StartWatching()
    {
        Watching = true;
        // A fresh watch starts with no memory: whatever is already on screen deserves
        // one dump, not silence because a previous session saw the same shape.
        lastShape.Clear();
        Plugin.Log.Information("[PlantRecon] watching plant-flow addons");
    }

    internal static void StopWatching()
    {
        Watching = false;
        Plugin.Log.Information("[PlantRecon] stopped");
    }

    /// <summary>
    /// One sampling pass. Called from <see cref="Game.CensusPump.Tick"/> ABOVE its
    /// 2-second self-throttle: the dialogs this watches open and close inside a couple
    /// of seconds, and a 2s sampler would miss whole addons. Frame-rate sampling is safe
    /// here because the per-addon shape hash below decides what actually reaches the log.
    /// </summary>
    internal static void Tick()
    {
        foreach (var name in WatchedAddons)
        {
            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon)
                || !GenericHelpers.IsAddonReady(addon))
            {
                // Closed addons forget their shape, so the next open dumps again.
                lastShape.Remove(name);
                continue;
            }

            var shape = Shape(addon);
            if (lastShape.TryGetValue(name, out var previous) && previous == shape)
                continue;
            lastShape[name] = shape;

            Plugin.Log.Information($"[PlantRecon] --- {name} ({addon->AtkValuesCount} values) ---");
            DumpStrings(addon, name);
            if (DumpInts.Contains(name))
                DumpIntValues(addon, name);
        }
    }

    /// <summary>Cheap identity for "this addon, as it currently stands": name, how many
    /// values it holds, and its first int. Not a checksum of the whole addon - just
    /// enough that a re-open or a changed selection reads as new.</summary>
    private static int Shape(AtkUnitBase* addon)
    {
        var firstInt = 0;
        try
        {
            for (var i = 0; i < addon->AtkValuesCount; i++)
            {
                if (addon->AtkValues[i].Type != AtkValueType.Int)
                    continue;
                firstInt = addon->AtkValues[i].Int;
                break;
            }
        }
        catch
        {
            // unreadable value = no int contribution
        }

        return HashCode.Combine(addon->AtkValuesCount, firstInt);
    }

    /// <summary>
    /// Same defensive idiom as <c>TendChain.DumpStrings</c>, duplicated rather than
    /// shared: that one hardcodes the [TendChain] prefix and lives in a Release-built
    /// class, while this copy is compiled out entirely and dies with the recon.
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
                    Plugin.Log.Information($"[PlantRecon] {tag} AtkValues[{i}] = '{text}'");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[PlantRecon] {tag} string dump failed: {ex.Message}");
        }
    }

    /// <summary>Ints for the picker addons: item ids, slot indices and button states ride
    /// here, and those are what a sow chain would have to send back in a Callback.</summary>
    private static void DumpIntValues(AtkUnitBase* addon, string tag)
    {
        try
        {
            for (var i = 0; i < addon->AtkValuesCount; i++)
            {
                var value = addon->AtkValues[i];
                if (value.Type != AtkValueType.Int)
                    continue;

                Plugin.Log.Information($"[PlantRecon] {tag} AtkValues[{i}] int = {value.Int}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[PlantRecon] {tag} int dump failed: {ex.Message}");
        }
    }
}
#endif
