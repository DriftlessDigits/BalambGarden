using System;
using System.Collections.Generic;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BalambGarden.Chains;

/// <summary>
/// The picker-driving half of the sow flow: the primitives that fill
/// <c>HousingGardening</c>'s two slots and press its Confirm, so a cycle stops asking the
/// player to type the order twice (once in our dropdown, once in the game's picker).
///
/// <para>EVIDENCE DISCIPLINE. Nothing in here fires an invented callback number. The
/// captures establish the flow's SHAPE only - "HousingGardening (0 values)" and
/// "ContextIconMenu fires per slot with the chosen item NAME at AtkValues[13]" - and
/// neither they nor Scrooge nor the FFXIVClientStructs headers say which callback case
/// opens a gardening slot. So this drives the addons the way a mouse does: it finds the
/// node the player would click and REPLAYS THAT NODE'S OWN REGISTERED AtkEvent, taking the
/// event type and param from the game's own event manager. There is no constant to be
/// wrong about; the worst case is a node that carries no click event, which is a refusal,
/// not a misfire. The replay itself is ECommons' proven pattern
/// (ECommons/Automation/UIInput/ClickHelper.cs:129-146, the same call AddonMaster's
/// ClickButtonIfEnabled uses everywhere Scrooge clicks a dialogue).</para>
///
/// <para>Everything here is READ-AND-CLICK. It never presses Yes on a sow: the prompt
/// verification in <see cref="CycleChain.ConfirmSow"/> / <see cref="PotChain"/> still
/// stands between the filled picker and a spent seed, and it now guards our own fill.</para>
/// </summary>
internal static unsafe partial class PlantFlow
{
    /// <summary>The picker's OK button. Not from a capture - the recon watcher only ever
    /// dumped AtkValues, and this addon has none - so it is matched as TEXT against the
    /// button's own label and a miss is a refusal, never a guess at a node id.</summary>
    internal const string ConfirmLabel = "Confirm";

    /// <summary>Between two synthetic clicks. The same human-tempo argument the chains make:
    /// a fill that lands three clicks in one frame is not a person using a menu.</summary>
    internal const int FillPaceMS = 400;

    /// <summary>How long one fill step gets before the driver hands the picker back. Short
    /// on purpose: the fallback is the player, who is already standing there.</summary>
    internal const int FillStepBudgetMS = 3_000;

    /// <summary>The item-selection list a slot click raises. Capture (apartment recon,
    /// 2026-08-15): "ContextIconMenu fires per slot with the chosen item NAME at
    /// AtkValues[13] ('Potting Soil' / 'Allagan Melon Seeds'; AtkValues[5] int = -1)."</summary>
    internal const string IconMenuAddon = "ContextIconMenu";

    internal static bool GardeningReady(out AtkUnitBase* addon)
        => GenericHelpers.TryGetAddonByName(GardeningAddon, out addon)
            && GenericHelpers.IsAddonReady(addon);

    internal static bool IconMenuReady(out AtkUnitBase* addon)
        => GenericHelpers.TryGetAddonByName(IconMenuAddon, out addon)
            && GenericHelpers.IsAddonReady(addon);

    internal static bool IconMenuOpen() => IconMenuReady(out _);

    /// <summary>The item name the game itself prints for an item id - the exact string the
    /// picker's list will show. Read off the Item sheet rather than our own tables on
    /// purpose: the tables carry OUR names for things (and pot soils are not in them at
    /// all), and what has to match here is the game's. Sheet read is Scrooge's precedent
    /// (Scrooge/AutoPinch.cs:333, 840).</summary>
    internal static string ItemName(uint itemId)
    {
        if (itemId == 0)
            return "";

        try
        {
            var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            var row = sheet.GetRowOrDefault(itemId);
            return row?.Name.ExtractText().Trim() ?? "";
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Fill] item {itemId} name lookup failed: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Opens one of the picker's two item slots. Slot order is the capture's:
    /// "TWO EMPTY SLOTS (soil left, seed right) the player fills FROM INVENTORY"
    /// (captures/2026-08-15-plant-flow.log F2) - so the slots are sorted by screen X and
    /// index 0 is soil.
    ///
    /// <para>An item slot in an FFXIV addon is a DragDrop component; that is what is being
    /// matched, not a node id. Exactly two visible ones must be present or the shape is not
    /// the shape this was written against and the driver stands down.</para>
    /// </summary>
    internal static bool ClickGardeningSlot(AtkUnitBase* picker, int index, out string why)
    {
        var slots = new List<nint>();
        CollectComponents(picker, ComponentType.DragDrop, slots, visibleOnly: true);

        if (slots.Count != 2)
        {
            why = $"the picker showed {slots.Count} item slots, not 2";
            return false;
        }

        // Left slot first. Two entries, so an explicit swap beats a comparer.
        if (ScreenX(slots[0]) > ScreenX(slots[1]))
            (slots[0], slots[1]) = (slots[1], slots[0]);

        if (index < 0 || index >= slots.Count)
        {
            why = $"no slot {index} in the picker";
            return false;
        }

        if (!ClickComponentNode(picker, (AtkComponentNode*)slots[index]))
        {
            why = $"slot {index + 1} carries no click event";
            return false;
        }

        why = "";
        return true;
    }

    /// <summary>
    /// Picks the named item out of the slot's inventory list. Matched by the item's own
    /// name, read off the list's rendered rows - the one thing about this addon the capture
    /// does establish is that the item NAME is what identifies an entry.
    ///
    /// <para>Fails closed when the name is not among the rendered rows. A list long enough
    /// to scroll can hide its tail from this read, and "I could not see it" is the honest
    /// answer there; the player fills that slot instead.</para>
    /// </summary>
    internal static bool ClickIconMenuItem(AtkUnitBase* menu, string itemName, out string why)
    {
        var lists = new List<nint>();
        CollectComponents(menu, ComponentType.List, lists, visibleOnly: false);
        if (lists.Count == 0)
        {
            why = "the item list had no rows to read";
            return false;
        }

        // Read the whole list first, then decide. A row's label can carry SeString payloads
        // (quality marks and the like) around the name, so an exact hit is preferred and a
        // single unambiguous partial is accepted; two partials are not a match at all.
        var rows = new List<(nint Renderer, string Text)>();
        foreach (var listNode in lists)
        {
            var list = (AtkComponentList*)((AtkComponentNode*)listNode)->Component;
            if (list == null)
                continue;

            var count = list->GetItemCount();
            for (var i = 0; i < count; i++)
            {
                var renderer = list->GetItemRenderer(i);
                if (renderer == null)
                    continue;

                var text = TextOf(renderer->AtkComponentButton.ButtonTextNode);
                if (text.Length > 0)
                    rows.Add(((nint)renderer, text));
            }
        }

        if (rows.Count == 0)
        {
            why = $"could not read the picker's list to find '{itemName}'";
            return false;
        }

        var hits = rows.FindAll(r => r.Text.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (hits.Count == 0)
            hits = rows.FindAll(r => r.Text.Contains(itemName, StringComparison.OrdinalIgnoreCase));

        if (hits.Count != 1)
        {
            why = hits.Count == 0
                ? $"'{itemName}' is not in the picker's list ({string.Join(", ", rows.ConvertAll(r => r.Text))})"
                : $"'{itemName}' matched {hits.Count} rows in the picker's list";
            return false;
        }

        if (!ClickComponentNode(menu, ((AtkComponentListItemRenderer*)hits[0].Renderer)->OwnerNode))
        {
            why = $"'{itemName}' is listed but carries no click event";
            return false;
        }

        why = "";
        return true;
    }

    /// <summary>Presses the picker's Confirm once the game has enabled it. A disabled
    /// Confirm is not a failure yet - the caller keeps trying until its budget runs out,
    /// because the slots settle a beat after they are filled.</summary>
    internal static bool PressGardeningConfirm(AtkUnitBase* picker, out string why)
    {
        var buttons = new List<nint>();
        CollectComponents(picker, ComponentType.Button, buttons, visibleOnly: true);

        foreach (var nodePtr in buttons)
        {
            var node = (AtkComponentNode*)nodePtr;
            var button = (AtkComponentButton*)node->Component;
            if (button == null)
                continue;
            if (!TextOf(button->ButtonTextNode).Equals(ConfirmLabel, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!button->IsEnabled)
            {
                why = "Confirm is not enabled yet";
                return false;
            }

            if (!ClickComponentNode(picker, node))
            {
                why = "Confirm carries no click event";
                return false;
            }

            why = "";
            return true;
        }

        why = $"no '{ConfirmLabel}' button in the picker";
        return false;
    }

    // ------------------------------------------------------------------ node plumbing

    /// <summary>Every component node of one kind in an addon's node list, as raw addresses
    /// (a pointer cannot ride in a generic list). Read-only walk; a null or wrong-typed
    /// entry is simply skipped.</summary>
    private static void CollectComponents(
        AtkUnitBase* addon, ComponentType kind, List<nint> into, bool visibleOnly)
    {
        try
        {
            var uld = addon->UldManager;
            for (var i = 0; i < uld.NodeListCount; i++)
            {
                var node = uld.NodeList[i];
                if (node == null || node->Type != NodeType.Component)
                    continue;
                if (visibleOnly && !node->IsVisible())
                    continue;

                var component = ((AtkComponentNode*)node)->Component;
                if (component == null || component->GetComponentType() != kind)
                    continue;

                into.Add((nint)node);
            }
        }
        catch (Exception ex)
        {
            // A node walk that throws must never take the run with it - the caller reads
            // an empty/short list and hands the picker back to the player.
            Plugin.Log.Warning($"[Fill] node walk failed: {ex.Message}");
        }
    }

    private static float ScreenX(nint componentNode)
        => ((AtkComponentNode*)componentNode)->AtkResNode.ScreenX;

    /// <summary>
    /// Clicks a component node by replaying the node's OWN registered MouseClick event
    /// through the addon - event type and param both come from the game's event manager, so
    /// there is no magic number here to be wrong about. This is ECommons'
    /// <c>ClickAddonButton</c> pattern (ClickHelper.cs:129-146) with the event-type walk
    /// kept, so a node whose first event is a hover does not get fired as if it were a
    /// click. False = this node has no click to replay; the caller fails closed.
    /// </summary>
    private static bool ClickComponentNode(AtkUnitBase* addon, AtkComponentNode* node)
    {
        if (node == null)
            return false;

        try
        {
            var resNode = &node->AtkResNode;
            if (Replay(addon, resNode))
                return true;

            // Item slots and list rows register their click on a collision child rather
            // than on the component node itself, so the component's own node list is the
            // second place to look. Still the node's own event, still no constants.
            var component = node->Component;
            if (component == null)
                return false;

            var uld = component->UldManager;
            for (var i = 0; i < uld.NodeListCount; i++)
            {
                var child = uld.NodeList[i];
                if (child != null && Replay(addon, child))
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Fill] click failed: {ex.Message}");
            return false;
        }
    }

    private static bool Replay(AtkUnitBase* addon, AtkResNode* node)
    {
        if (!node->IsEventRegistered(AtkEventType.MouseClick))
            return false;

        var evt = node->AtkEventManager.Event;
        while (evt != null && evt->State.EventType != AtkEventType.MouseClick)
            evt = evt->NextEvent;
        if (evt == null)
            return false;

        addon->ReceiveEvent(AtkEventType.MouseClick, (int)evt->Param, evt, null);
        return true;
    }

    private static string TextOf(AtkTextNode* text)
    {
        if (text == null)
            return "";
        try
        {
            return text->NodeText.ToString().Trim();
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>
/// One planting's auto-fill, driven a step at a time from inside the chain's existing
/// wait-for-the-human step. It is an ACCELERATOR, not a gate: the chain's sow step polls
/// this while it waits, and the instant this gives up the step is exactly the hybrid step
/// it has always been - picker open, note posted, player fills it and presses Confirm.
///
/// <para>That is the whole fail-closed story, and it is structural rather than procedural:
/// giving up means "stop clicking", and the thing that was going to happen anyway happens.
/// An auto-fill failure cannot abort a run a person standing at the bed could finish.</para>
/// </summary>
internal sealed unsafe class GardeningFill
{
    private enum Step
    {
        OpenSoilSlot,
        AwaitSoilList,
        PickSoil,
        AwaitSoilListGone,
        OpenSeedSlot,
        AwaitSeedList,
        PickSeed,
        AwaitSeedListGone,
        PressConfirm,
        Done,
    }

    private readonly string soilName;
    private readonly string seedName;
    private Step step = Step.OpenSoilSlot;
    private DateTime nextActionAt = DateTime.MinValue;
    private DateTime stepDeadline = DateTime.MaxValue;

    /// <summary>Why the driver stopped driving, if it did. Non-null means the step is the
    /// human's now - and that is a normal ending, not an error.</summary>
    internal string? GaveUp { get; private set; }

    /// <summary>True once Confirm has been pressed. The sow prompt is what actually
    /// confirms the planting, and that is verified downstream exactly as before.</summary>
    internal bool Filled => step == Step.Done;

    /// <summary>What it put in, for the run log: "Potting Soil + Krakka Root Seeds".</summary>
    internal string What => $"{soilName} + {seedName}";

    internal GardeningFill(uint soilItemId, uint seedItemId)
    {
        soilName = PlantFlow.ItemName(soilItemId);
        seedName = PlantFlow.ItemName(seedItemId);

        if (!Plugin.Configuration.AutoFillPicker)
            GaveUp = "auto-fill is switched off";
        else if (soilName.Length == 0 || seedName.Length == 0)
            GaveUp = "nothing named to fill the picker with";
    }

    /// <summary>One pass. Safe to call every frame from the moment the plant option is
    /// selected: it does nothing at all until the picker is actually up.</summary>
    internal void Tick()
    {
        if (GaveUp is not null || step == Step.Done)
            return;

        if (DateTime.UtcNow < nextActionAt)
            return;

        if (!PlantFlow.GardeningReady(out var picker))
            return;

        // Arm the first step's budget the moment the picker exists. Live receipt (19:04:10,
        // FC room): the addon reports ready in the same millisecond it opens, BEFORE its
        // component tree is populated - the first slot scan saw 0 slots and a hard stop
        // ended the attempt. Failures inside a step are retried until this deadline; only
        // a budget's worth of empty reads hands the picker over.
        if (stepDeadline == DateTime.MaxValue)
            stepDeadline = DateTime.UtcNow.AddMilliseconds(PlantFlow.FillStepBudgetMS);

        switch (step)
        {
            case Step.OpenSoilSlot:
                OpenSlot(picker, 0, Step.AwaitSoilList);
                break;

            case Step.AwaitSoilList:
                if (PlantFlow.IconMenuOpen())
                    Advance(Step.PickSoil);
                else
                    Expire("the soil slot did not open its list");
                break;

            case Step.PickSoil:
                Pick(soilName, Step.AwaitSoilListGone);
                break;

            case Step.AwaitSoilListGone:
                if (!PlantFlow.IconMenuOpen())
                    Advance(Step.OpenSeedSlot);
                else
                    Expire($"the list stayed open after picking {soilName}");
                break;

            case Step.OpenSeedSlot:
                OpenSlot(picker, 1, Step.AwaitSeedList);
                break;

            case Step.AwaitSeedList:
                if (PlantFlow.IconMenuOpen())
                    Advance(Step.PickSeed);
                else
                    Expire("the seed slot did not open its list");
                break;

            case Step.PickSeed:
                Pick(seedName, Step.AwaitSeedListGone);
                break;

            case Step.AwaitSeedListGone:
                if (!PlantFlow.IconMenuOpen())
                    Advance(Step.PressConfirm);
                else
                    Expire($"the list stayed open after picking {seedName}");
                break;

            case Step.PressConfirm:
                if (PlantFlow.PressGardeningConfirm(picker, out var confirmWhy))
                {
                    step = Step.Done;
                    Plugin.Log.Information($"[Fill] filled {What} and pressed {PlantFlow.ConfirmLabel}");
                }
                else
                {
                    // A not-yet-enabled Confirm is the expected reading for a frame or two.
                    Expire(confirmWhy);
                }

                break;
        }
    }

    private void OpenSlot(AtkUnitBase* picker, int index, Step next)
    {
        // Retry inside the budget, never hard-stop: an addon that just opened can read as
        // ready with an empty component tree for a few frames (live receipt 19:04:10 - the
        // first scan of a same-millisecond-old picker counted 0 slots).
        if (PlantFlow.ClickGardeningSlot(picker, index, out var why))
            Advance(next);
        else
            Expire(why);
    }

    private void Pick(string itemName, Step next)
    {
        if (!PlantFlow.IconMenuReady(out var menu))
        {
            Expire("the item list went away");
            return;
        }

        // Same budgeted retry: a just-opened list can render its rows a frame late, and an
        // item genuinely absent still hands over within the budget.
        if (PlantFlow.ClickIconMenuItem(menu, itemName, out var why))
            Advance(next);
        else
            Expire(why);
    }

    private void Advance(Step next)
    {
        step = next;
        nextActionAt = DateTime.UtcNow.AddMilliseconds(PlantFlow.FillPaceMS);
        stepDeadline = DateTime.UtcNow.AddMilliseconds(PlantFlow.FillStepBudgetMS);
    }

    /// <summary>A wait that has not paid off yet: keep waiting until the budget is gone,
    /// then hand the picker to the player with the reason it stalled.</summary>
    private void Expire(string why)
    {
        if (DateTime.UtcNow <= stepDeadline)
            return;
        Stop(why);
    }

    private void Stop(string why)
    {
        GaveUp = why;
        Plugin.Log.Information($"[Fill] standing down - {why}; the picker is yours");
    }
}
