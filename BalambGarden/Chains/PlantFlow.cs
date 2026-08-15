using System;
using Dalamud.Memory;
using ECommons;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
#if DEBUG
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.DalamudServices;
#endif

namespace BalambGarden.Chains;

/// <summary>
/// The sow/harvest flow's driver half: the addon names, the menu wording, and the
/// small read/click primitives that <see cref="CycleChain"/> and <see cref="PotChain"/>
/// compose into steps. Every constant below is quoted from
/// <c>captures/2026-08-15-plant-flow.log</c> - the bench capture, not this code's guesses,
/// is the binding authority for what the game says.
///
/// <para>Pacing lives in the chains, not here: these methods read and click, the caller
/// decides when. Nothing here writes to the ledger.</para>
/// </summary>
internal static unsafe partial class PlantFlow
{
    // capture: "SelectString AtkValues[7] = 'Plant Seeds'" (empty pot menu)
    internal const string PlantOption = "Plant Seeds";

    // capture: "SelectString AtkValues[7] = 'Harvest Crop'" (ripe pot menu)
    internal const string HarvestOption = "Harvest Crop";

    // capture: "SelectString AtkValues[8] = 'Quit'" (both menus)
    internal const string QuitOption = "Quit";

    // Not in the capture (the recon covered a ripe pot and an empty pot, never a growing
    // one). Same wording family TendChain has matched at the bench since 08-11; a pot that
    // does not offer it simply quits the menu and says so.
    internal const string TendOption = "Tend";

    // capture: "Talk AtkValues[0] = 'There is nothing in this flowerpot.'"
    internal const string EmptyPotTalk = "There is nothing in this flowerpot.";

    // capture: "Talk AtkValues[0] = 'Red Sunflowers\nThese flowers are in bloom.'"
    internal const string BloomTalkLine = "These flowers are in bloom.";

    // capture: "--- HousingGardening (0 values) ---" - the soil/seed picker, and it holds
    // no text at all. Two slots the player fills from inventory (capture F2). The driver in
    // PlantFill.cs works the slots as NODES for that reason: there is nothing here to read,
    // only things to click.
    internal const string GardeningAddon = "HousingGardening";

    /// <summary>How long the chain will stand at an open picker waiting for the human to
    /// fill both slots and press Confirm. Deliberately far above the step timeout: waiting
    /// on a person is not a stalled dialogue, and a run must not die because someone went
    /// looking for the right seed.</summary>
    internal const int HumanFillTimeoutMS = 120_000;

    /// <summary>The bed identity in a bed/pot menu ("2nd Bed, 1st Patch"), index mapped
    /// live 2026-08-11.</summary>
    private const int HeaderValueIndex = 2;

    internal static bool MenuReady(out AtkUnitBase* addon)
        => GenericHelpers.TryGetAddonByName("SelectString", out addon)
            && GenericHelpers.IsAddonReady(addon);

    internal static bool TalkReady(out AtkUnitBase* addon)
        => GenericHelpers.TryGetAddonByName("Talk", out addon)
            && GenericHelpers.IsAddonReady(addon);

    /// <summary>Is the soil/seed picker up? It carries no AtkValues, so its presence is the
    /// whole signal that the sow is at the fill stage - whoever is doing the filling.</summary>
    internal static bool GardeningOpen() => GardeningReady(out _);

    /// <summary>The sow confirmation, if it is up. Its text is the only place the game
    /// names what is about to be planted.</summary>
    internal static bool SowPromptReady(out string prompt)
    {
        prompt = "";
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
            return false;

        // capture: "SelectYesno AtkValues[0] = 'Prepare the bed with a bag of ...?'"
        prompt = ReadStringValue(addon, 0);
        return prompt.Length > 0;
    }

    /// <summary>Yes sows; No walks away with nothing planted and nothing spent.</summary>
    internal static bool AnswerSow(bool yes)
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
            return false;

        var master = new AddonMaster.SelectYesno((nint)addon);
        if (yes)
            master.Yes();
        else
            master.No();
        return true;
    }

    internal static void ClickTalk(AtkUnitBase* talk)
        => new AddonMaster.Talk((nint)talk).Click();

    /// <summary>The Talk's whole first value - "Red Sunflowers\nThese flowers are in bloom."</summary>
    internal static string TalkText(AtkUnitBase* talk) => ReadStringValue(talk, 0);

    /// <summary>The first line of a status Talk is the plant's name.</summary>
    internal static string TalkHeadline(AtkUnitBase* talk)
    {
        var text = TalkText(talk);
        var newline = text.IndexOf('\n');
        return (newline > 0 ? text[..newline] : text).Trim();
    }

    internal static string MenuHeader(AtkUnitBase* menu)
    {
        var text = ReadStringValue(menu, HeaderValueIndex);
        return text.Length > 0 ? text : "(unknown bed)";
    }

    internal static bool MenuOffers(AtkUnitBase* menu, string needle)
    {
        foreach (var entry in new AddonMaster.SelectString(menu).Entries)
        {
            if (entry.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Picks the first option whose text contains <paramref name="needle"/>.
    /// False = this menu does not offer it, which is a fact for the caller to report,
    /// never something to retry into a timeout.</summary>
    internal static bool SelectOption(AtkUnitBase* menu, string needle)
    {
        foreach (var entry in new AddonMaster.SelectString(menu).Entries)
        {
            if (!entry.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;
            entry.Select();
            return true;
        }

        return false;
    }

    /// <summary>Reads one string-typed AtkValue defensively; empty string when absent.</summary>
    internal static string ReadStringValue(AtkUnitBase* addon, int index)
    {
        try
        {
            if (addon->AtkValuesCount > index
                && addon->AtkValues[index].Type is AtkValueType.String or AtkValueType.ConstString
                    or AtkValueType.WideString or AtkValueType.ManagedString
                && addon->AtkValues[index].String.Value != null)
                return MemoryHelper.ReadSeStringNullTerminated(
                    (nint)addon->AtkValues[index].String.Value).GetText();
        }
        catch
        {
            // unreadable value = absent value
        }

        return "";
    }
}

#if DEBUG
/// <summary>
/// The recon half (debug builds only - a Release plugin carries none of it). It did its
/// job on 2026-08-15: the constants above are what it wrote down. Kept because the flow
/// will need re-reading the next time the game changes a dialog, and a watcher that no
/// longer exists cannot be turned on.
///
/// <para>Read-only and defensive throughout: a recon tool that throws would break the very
/// interaction it is trying to observe.</para>
/// </summary>
internal static unsafe partial class PlantFlow
{
    /// <summary>Addons the sow flow walks through. Ints are dumped for the two that might
    /// carry structured selection state; the rest are dialogue surfaces where only the text
    /// matters. (The capture settled it: HousingGardening carries nothing at all.)</summary>
    private static readonly string[] WatchedAddons =
        ["HousingGardening", "ContextIconMenu", "SelectYesno", "SelectString", "Talk"];

    private static readonly System.Collections.Generic.HashSet<string> DumpInts =
        ["HousingGardening", "ContextIconMenu"];

    /// <summary>Last dumped shape per addon. An addon that is merely still open has not
    /// said anything new, and at pump tempo it would say it hundreds of times - so a
    /// dump only repeats when the addon's shape changes (a new open, a new page, a
    /// different selection).</summary>
    private static readonly System.Collections.Generic.Dictionary<string, int> lastShape = [];

    /// <summary>The two addons the fill drives. Their RECEIVE-EVENT traffic is what a click
    /// actually sends, which the AtkValue dump above cannot show at all - the picker has no
    /// AtkValues to dump. Recording it while a human plants by hand is how the driver's own
    /// clicks get something to be checked against.</summary>
    private static readonly string[] EventWatchedAddons = ["HousingGardening", "ContextIconMenu"];

    /// <summary>Held as one delegate instance so the unregister removes the same listener
    /// the register added - a method group converted twice is two objects.</summary>
    private static readonly Dalamud.Plugin.Services.IAddonLifecycle.AddonEventDelegate
        ReceiveListener = OnReceiveEvent;

    internal static bool Watching { get; private set; }

    internal static void StartWatching()
    {
        if (Watching)
            return;

        Watching = true;
        // A fresh watch starts with no memory: whatever is already on screen deserves
        // one dump, not silence because a previous session saw the same shape.
        lastShape.Clear();
        Svc.AddonLifecycle.RegisterListener(
            AddonEvent.PreReceiveEvent, EventWatchedAddons, ReceiveListener);
        Plugin.Log.Information("[PlantRecon] watching plant-flow addons (+ receive events)");
    }

    internal static void StopWatching()
    {
        if (!Watching)
            return;

        Watching = false;
        Svc.AddonLifecycle.UnregisterListener(
            AddonEvent.PreReceiveEvent, EventWatchedAddons, ReceiveListener);
        Plugin.Log.Information("[PlantRecon] stopped");
    }

    /// <summary>
    /// One line per event the picker or its item list receives: which addon, which
    /// AtkEventType, and the event param. That pair IS what a click carries - the driver
    /// replays a node's own registered event, so a manual fill logged here and a driven fill
    /// logged here should read the same, and any difference is a finding rather than a
    /// mystery.
    ///
    /// <para>Strictly an observer: it never calls PreventOriginal and never touches the
    /// event. A watcher that changed the interaction would be measuring itself.</para>
    /// </summary>
    private static void OnReceiveEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (args is not AddonReceiveEventArgs receive)
                return;

            Plugin.Log.Information(
                $"[PlantRecon] {args.AddonName} receive {receive.AtkEventType} param={receive.EventParam}");
        }
        catch (Exception ex)
        {
            // The game is mid-event here; a throw would land inside its own UI dispatch.
            Plugin.Log.Warning($"[PlantRecon] receive-event read failed: {ex.Message}");
        }
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

    private static void DumpStrings(AtkUnitBase* addon, string tag)
    {
        try
        {
            for (var i = 0; i < addon->AtkValuesCount; i++)
            {
                var text = ReadStringValue(addon, i);
                if (text.Length > 0)
                    Plugin.Log.Information($"[PlantRecon] {tag} AtkValues[{i}] = '{text}'");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[PlantRecon] {tag} string dump failed: {ex.Message}");
        }
    }

    /// <summary>Ints for the picker addons: item ids, slot indices and button states would
    /// ride here if the picker carried any.</summary>
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
