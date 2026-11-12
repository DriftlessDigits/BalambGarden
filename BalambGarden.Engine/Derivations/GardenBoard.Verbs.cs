namespace BalambGarden.Engine.Derivations;

/// <summary>A press a container's menu on the here card can offer (rethink slice 5).
/// <see cref="Tend"/> waters (a pot's is its Water); <see cref="Harvest"/> takes the crop;
/// <see cref="Replant"/> harvests and sows the same crop again.</summary>
public enum CellVerb { Tend, Harvest, Replant }

public static partial class GardenBoard
{
    /// <summary>The presses a container's menu offers, from what the card believes about it: a
    /// growing plant (thirsty or not) can drink; a ripe one, seen or by the clock, can be harvested
    /// or replanted (the press reads the map first, and the game has the last word); an empty,
    /// dead, never-read or unrecorded container offers nothing (sowing is the plant pop-up's,
    /// rethink slice 6).</summary>
    public static IReadOnlyList<CellVerb> VerbsFor(ContainerCell cell) => cell.Look switch
    {
        ContainerLook.Growing or ContainerLook.WaterSoon or ContainerLook.Wilting or ContainerLook.Dying
            => [CellVerb.Tend],
        ContainerLook.Ripe => [CellVerb.Harvest, CellVerb.Replant],
        _ => [],
    };
}
