using System;
using System.Collections.Generic;
using System.Linq;
using BalambGarden.Chains;
using BalambGarden.Engine.Derivations;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BalambGarden.Game;

/// <summary>The live bag read (ruling 2026-08-16: current character's bags, live, honest
/// about scope). Runs on the draw thread, asks the game directly, caches nothing - a supply
/// claim is exactly as fresh as the frame it renders. The instance is the engine's
/// <see cref="IInventorySource"/> (tips, the pot cycle planner); the static reads are what the
/// verbs and their pickers ask.</summary>
internal sealed unsafe class BagInventory : IInventorySource
{
    public int CountOf(uint itemId) => Count(itemId);

    /// <summary>How many of one item the bags hold; 0 before the game has an inventory.</summary>
    internal static int Count(uint itemId)
    {
        var manager = InventoryManager.Instance();
        return manager == null ? 0 : manager->GetInventoryItemCount(itemId);
    }

    /// <summary>Empty bag slots, the room a harvest needs; 0 before the game has an inventory.</summary>
    internal static int FreeSlots()
    {
        var manager = InventoryManager.Instance();
        return manager == null ? 0 : (int)manager->GetEmptySlotsInBag();
    }

    /// <summary>Every soil the bags hold, by the game's own item names. Read live rather
    /// than tabled on purpose: there is NO soil table to draw a flowerpot's soil from -
    /// the shipped Soils.json is the nine outdoor topsoils, and "Potting Soil", which is
    /// what a flowerpot actually takes, is not a topsoil and is nowhere in our data. So
    /// this is not a table at all: it is what is in the bags right now whose name the GAME
    /// says ends in "soil". Nothing invented, nothing hardcoded, and a soil we have never
    /// heard of shows up the moment the player buys one.</summary>
    internal static List<(uint ItemId, string Name, int Count)> SoilsInBag()
        => ItemsWhere(static name => name.EndsWith("soil", StringComparison.OrdinalIgnoreCase));

    /// <summary>Seed items in the bags that the crop table does NOT know - flowerpot
    /// flower seeds, mostly (the crop table is outdoor gardening; flowerpot flowers are a
    /// separate game system with their own seeds). Read live by the game's own item names,
    /// same reasoning as <see cref="SoilsInBag"/>: nothing invented, and the sow
    /// verification receipts the name at plant time either way.</summary>
    internal static List<(uint ItemId, string Name, int Count)> ExtraSeedsInBag()
    {
        var known = Plugin.Tables.Crops.Select(c => c.SeedId).ToHashSet();
        return ItemsWhere(name => name.EndsWith(" seeds", StringComparison.OrdinalIgnoreCase))
            .Where(s => !known.Contains(s.ItemId))
            .ToList();
    }

    private static List<(uint ItemId, string Name, int Count)> ItemsWhere(Func<string, bool> nameMatches)
    {
        var found = new List<(uint, string, int)>();
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return found;

        var seen = new HashSet<uint>();
        foreach (var bag in new[]
                 {
                     InventoryType.Inventory1,
                     InventoryType.Inventory2,
                     InventoryType.Inventory3,
                     InventoryType.Inventory4,
                 })
        {
            var container = inventory->GetInventoryContainer(bag);
            if (container == null)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0 || !seen.Add(slot->ItemId))
                    continue;

                var name = PlantFlow.ItemName(slot->ItemId);
                if (!nameMatches(name))
                    continue;

                found.Add((slot->ItemId, name, inventory->GetInventoryItemCount(slot->ItemId)));
            }
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        return found;
    }
}
