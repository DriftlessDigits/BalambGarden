using System.Numerics;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Sensing;

/// <summary>The pot-gate (08-16, nine captures, every estate): a DataMap entry is a pot
/// only when the furniture vector's entry at that index is one of the three Flowerpot
/// items. Category 14 was REFUTED as the discriminator - Oasis Vase, Paramour Vase and
/// the Tier 4 Aquariums are cat-14 non-pots carrying pot-shaped DataMap data. The receipt
/// set: Riviera 65979, Glade 65980, Oasis 65981; every real pot at every estate is one of
/// these ids and no phantom shares them.</summary>
public class PotGateTests
{
    private static readonly uint[] Flowerpots = [65979, 65980, 65981];

    // The pot house, 08-16 10:34: real Oasis pots beside the phantoms that faked ripe rows.
    private static readonly List<FurniturePlacement> PotHouse =
    [
        new(117, new Vector3(1f, 0f, 0f), 66064),   // Mounted Flower Vase - "Red Tea Flowers st4"
        new(126, new Vector3(2f, 0f, 0f), 65981),   // Oasis Flowerpot, real
        new(127, new Vector3(3f, 0f, 0f), 65981),   // Oasis Flowerpot, real
        new(162, new Vector3(4f, 0f, 0f), 66494),   // Medium Angler's Canvas
        new(194, new Vector3(5f, 0f, 0f), 65757),   // Oasis Vase - cat 14, still not a pot
    ];

    [Fact]
    public void OnlyFlowerpotIdsPassTheGate()
        => Assert.Equal([126, 127], PotGate.Keys(PotHouse, Flowerpots));

    [Fact] // chambers, 08-16: an EMPTY real pot still gates in - id decides, not plant data
    public void EmptyPotIsStillAPot()
    {
        List<FurniturePlacement> chambers =
        [
            new(0, new Vector3(1f, 0f, 0f), 65980),
            new(1, new Vector3(2f, 0f, 0f), 65980),
        ];
        Assert.Equal([0, 1], PotGate.Keys(chambers, Flowerpots));
    }

    [Fact]
    public void NoFurnitureIsNoPots()
        => Assert.Empty(PotGate.Keys([], Flowerpots));
}
