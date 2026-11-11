namespace BalambGarden.Engine.Derivations;

/// <summary>What the plugin can see in bags RIGHT NOW. The Engine asks, never caches -
/// a supply claim is only ever as fresh as the read behind it (ruling 2026-08-16: bags
/// only, live, honest about scope).</summary>
public interface IInventorySource
{
    int CountOf(uint itemId);
}
