using System;
using System.Collections.Generic;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BalambGarden.Chains;

/// <summary>
/// The picker-driving half of the sow flow: fills <c>HousingGardening</c>'s two slots and
/// presses its Confirm, so a cycle stops asking the player to type the order twice (once
/// in our dropdown, once in the game's picker).
///
/// <para>MECHANISM (Sam's lateral, 08-16): with the picker open, right-click an item in
/// the inventory and pick Use - the game routes it into the matching slot itself. That
/// context-menu Use is <c>AgentInventoryContext.UseItem</c>, a real game entry point, so
/// the slots never need a synthetic click at all. (The slot components register no
/// MouseClick - the 08-16 tree dump receipt - and their DragDrop event dialect stays
/// unmapped on purpose: there is nothing left that needs it.)</para>
///
/// <para>FAIL-CLOSED, structurally: Confirm only enables once the game itself accepted
/// both items into their slots. A Use that landed nothing leaves Confirm disabled, the
/// budget runs out, and the picker is handed to the player - who was getting it anyway.
/// Nothing here presses Yes on a sow: the prompt verification in
/// <see cref="CycleChain"/> / <see cref="PotChain"/> still stands between a filled
/// picker and a spent seed.</para>
/// </summary>
internal static unsafe partial class PlantFlow
{
    /// <summary>The picker's OK button. Matched as TEXT against the button's own label -
    /// a miss is a refusal, never a guess at a node id.</summary>
    internal const string ConfirmLabel = "Confirm";

    /// <summary>Between two driven actions, jittered. The same human-tempo argument the
    /// chains make: a fill that lands its items in one frame is not a person using a menu.</summary>
    internal const int FillPaceMS = 600;
    internal const int FillJitterMS = 250;

    /// <summary>Before the FIRST action after the picker opens. A person spends a beat
    /// reaching for the inventory, and the game agrees: an immediate UseItem got "unable
    /// to execute at this time" live (08-16, Sam) - the picker reads ready before the
    /// interaction transition has finished.</summary>
    internal const int FillSettleMS = 1_200;

    /// <summary>How long one fill step gets before the driver hands the picker back.
    /// Short on purpose: the fallback is the player, who is already standing there.</summary>
    internal const int FillStepBudgetMS = 4_000;

    /// <summary>The icon id the picker's soil (0) / seed (1) slot currently shows, read
    /// off the DragDrop component's own icon. Null = the slots could not be read. An
    /// EMPTY slot is not necessarily id 0 - the picker draws placeholder glyphs - so the
    /// caller compares against a baseline taken before any Use, never against zero.</summary>
    internal static uint? SlotIconId(AtkUnitBase* picker, int index)
    {
        var slots = new List<nint>();
        CollectComponents(picker, ComponentType.DragDrop, slots, visibleOnly: true);
        if (slots.Count != 2 || index < 0 || index > 1)
            return null;

        if (ScreenX(slots[0]) > ScreenX(slots[1]))   // soil left, seed right (capture F2)
            (slots[0], slots[1]) = (slots[1], slots[0]);

        var drag = (AtkComponentDragDrop*)((AtkComponentNode*)slots[index])->Component;
        if (drag == null || drag->AtkComponentIcon == null)
            return null;
        return drag->AtkComponentIcon->IconId;
    }

    private static float ScreenX(nint componentNode)
        => ((AtkComponentNode*)componentNode)->AtkResNode.ScreenX;

    internal static bool GardeningReady(out AtkUnitBase* addon)
        => GenericHelpers.TryGetAddonByName(GardeningAddon, out addon)
            && GenericHelpers.IsAddonReady(addon);

    /// <summary>The item name the game itself prints for an item id. Read off the Item
    /// sheet rather than our own tables on purpose: the tables carry OUR names for things
    /// (and pot soils are not in them at all). Sheet read is Scrooge's precedent.</summary>
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

    /// <summary>The context menu's Use, without the context menu (Sam's receipt: Use on a
    /// soil/seed while the picker is open fills its slot). The item's REAL bag location is
    /// found first and passed explicitly - a defaulted location can silently Use nothing
    /// (08-16: 'got to the inventory and then nothing happened'). True only means the call
    /// was made; whether the item landed is read off the slot's own icon.</summary>
    internal static bool UseFromInventory(uint itemId)
    {
        try
        {
            var agent = AgentInventoryContext.Instance();
            if (agent == null)
                return false;

            var (bag, slot, found) = FindInBags(itemId);
            if (!found)
            {
                Plugin.Log.Information($"[Fill] item {itemId} is not in the bags");
                return false;
            }

            Plugin.Log.Information($"[Fill] UseItem({itemId}) from {bag} slot {slot}");
            agent->UseItem(itemId, bag, (uint)slot);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Fill] UseItem({itemId}) failed: {ex.Message}");
            return false;
        }
    }

    private static (FFXIVClientStructs.FFXIV.Client.Game.InventoryType Bag, int Slot, bool Found)
        FindInBags(uint itemId)
    {
        var manager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (manager == null)
            return (default, 0, false);

        foreach (var bag in new[]
                 {
                     FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1,
                     FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory2,
                     FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory3,
                     FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4,
                 })
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null)
                continue;
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item != null && item->ItemId == itemId)
                    return (bag, i, true);
            }
        }
        return (default, 0, false);
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

    /// <summary>Every component node of one kind in an addon's node list, as raw
    /// addresses (a pointer cannot ride in a generic list). Read-only walk; a null or
    /// wrong-typed entry is simply skipped.</summary>
    private static void CollectComponents(
        AtkUnitBase* addon, ComponentType kind, List<nint> into, bool visibleOnly)
    {
        try
        {
            var uld = addon->UldManager;
            for (var i = 0; i < uld.NodeListCount; i++)
            {
                var node = uld.NodeList[i];
                // A component node's Type is 1000 + a variant (08-16 tree receipt: the
                // picker's buttons are 1001, its slots 1007) - equality against
                // NodeType.Component matches NOTHING. >= 1000 is the ECommons test.
                if (node == null || (ushort)node->Type < 1000)
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

    /// <summary>
    /// Clicks a component node by replaying the node's OWN registered MouseClick event
    /// through the addon - event type and param both come from the game's event manager,
    /// so there is no magic number here to be wrong about. This is ECommons'
    /// <c>ClickAddonButton</c> pattern with the event-type walk kept, so a node whose
    /// first event is a hover does not get fired as if it were a click. False = this node
    /// has no click to replay; the caller fails closed.
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

            // Buttons can register their click on a collision child rather than on the
            // component node itself. Still the node's own event, still no constants.
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
/// </summary>
internal sealed unsafe class GardeningFill
{
    private enum Step
    {
        UseSoil,
        UseSeed,
        PressConfirm,
        Done,
    }

    private readonly uint soilItemId;
    private readonly uint seedItemId;
    private readonly string soilName;
    private readonly string seedName;
    private readonly Random random = new();
    private Step step = Step.UseSoil;
    private DateTime nextActionAt = DateTime.MinValue;
    private DateTime stepDeadline = DateTime.MaxValue;
    private bool settled;
    private int usesThisStep;
    private readonly uint?[] baselineIcon = new uint?[2];

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
        this.soilItemId = soilItemId;
        this.seedItemId = seedItemId;
        soilName = PlantFlow.ItemName(soilItemId);
        seedName = PlantFlow.ItemName(seedItemId);

        if (!Plugin.Configuration.AutoFillPicker)
            GaveUp = "auto-fill is switched off";
        else if (soilName.Length == 0 || seedName.Length == 0)
            GaveUp = "nothing named to fill the picker with";
    }

    /// <summary>One pass. Safe to call every frame from the moment the plant option is
    /// selected: it does nothing at all until the picker is actually up. The picker-open
    /// guard is also what scopes UseItem - a soil never gets Used into a world with no
    /// slot waiting for it.</summary>
    internal void Tick()
    {
        if (GaveUp is not null || step == Step.Done)
            return;

        if (DateTime.UtcNow < nextActionAt)
            return;

        if (!PlantFlow.GardeningReady(out var picker))
            return;

        // The picker reads ready before the world does (08-16 live: an immediate Use got
        // "unable to execute at this time"). A person spends this beat reaching for the
        // inventory anyway.
        if (!settled)
        {
            settled = true;
            // The empty picker's slots ARE the baseline - placeholder glyphs mean an
            // empty slot's icon id is not zero, so "landed" is "changed", never "nonzero".
            baselineIcon[0] = PlantFlow.SlotIconId(picker, 0);
            baselineIcon[1] = PlantFlow.SlotIconId(picker, 1);
            Plugin.Log.Information(
                $"[Fill] settling; empty-slot icons = {baselineIcon[0]?.ToString() ?? "?"} / "
                + $"{baselineIcon[1]?.ToString() ?? "?"}");
            nextActionAt = DateTime.UtcNow.AddMilliseconds(Jitter(PlantFlow.FillSettleMS));
            return;
        }

        if (stepDeadline == DateTime.MaxValue)
            stepDeadline = DateTime.UtcNow.AddMilliseconds(PlantFlow.FillStepBudgetMS);

        switch (step)
        {
            case Step.UseSoil:
                Use(picker, slot: 0, soilItemId, soilName, Step.UseSeed);
                break;

            case Step.UseSeed:
                Use(picker, slot: 1, seedItemId, seedName, Step.PressConfirm);
                break;

            case Step.PressConfirm:
                // Confirm enabling IS the receipt that both Uses landed; until the budget
                // runs out, "not enabled yet" just means keep waiting.
                if (PlantFlow.PressGardeningConfirm(picker, out var confirmWhy))
                {
                    step = Step.Done;
                    Plugin.Log.Information($"[Fill] filled {What} and pressed {PlantFlow.ConfirmLabel}");
                }
                else
                {
                    Expire(confirmWhy);
                }

                break;
        }
    }

    /// <summary>One slot's fill, verified where the picker lets us: the slot's own icon
    /// says whether the Use landed (a refused Use - "unable to execute" - is silent to the
    /// caller). Landed -> next step; not yet -> re-Use at pace, up to three tries; slots
    /// unreadable -> one Use on trust and Confirm arbitrates, the original contract.</summary>
    private void Use(AtkUnitBase* picker, int slot, uint itemId, string itemName, Step next)
    {
        var current = PlantFlow.SlotIconId(picker, slot);
        var filled = current is null || baselineIcon[slot] is null
            ? (bool?)null
            : current != baselineIcon[slot];
        if (filled == true)
        {
            Plugin.Log.Information($"[Fill] {itemName} landed (slot icon {baselineIcon[slot]} -> {current})");
            Advance(next);
            return;
        }

        if (usesThisStep >= 1 && filled is null)
        {
            Advance(next);   // unreadable slots: trust the one call, Confirm decides
            return;
        }

        if (usesThisStep >= 3)
        {
            Expire($"{itemName} would not go in after {usesThisStep} tries");
            return;
        }

        if (!PlantFlow.UseFromInventory(itemId))
        {
            Expire($"could not Use {itemName} from the bags");
            return;
        }

        usesThisStep++;
        nextActionAt = DateTime.UtcNow.AddMilliseconds(Jitter(PlantFlow.FillPaceMS));
    }

    private void Advance(Step next)
    {
        step = next;
        usesThisStep = 0;
        nextActionAt = DateTime.UtcNow.AddMilliseconds(Jitter(PlantFlow.FillPaceMS));
        stepDeadline = DateTime.UtcNow.AddMilliseconds(PlantFlow.FillStepBudgetMS);
    }

    private int Jitter(int baseMS)
        => Math.Max(250, baseMS + (int)(((random.NextDouble() * 2.0) - 1.0) * PlantFlow.FillJitterMS));

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
