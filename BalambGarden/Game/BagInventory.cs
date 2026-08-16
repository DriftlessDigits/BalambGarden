using BalambGarden.Engine.Derivations;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BalambGarden.Game;

/// <summary>The live bag read behind state-aware tips (ruling 2026-08-16: current
/// character's bags, live, honest about scope). Runs on the draw thread, asks the game
/// directly, caches nothing - a supply claim is exactly as fresh as the frame it renders.</summary>
internal sealed unsafe class BagInventory : IInventorySource
{
    public int CountOf(uint itemId)
    {
        var manager = InventoryManager.Instance();
        return manager == null ? 0 : manager->GetInventoryItemCount(itemId);
    }
}
