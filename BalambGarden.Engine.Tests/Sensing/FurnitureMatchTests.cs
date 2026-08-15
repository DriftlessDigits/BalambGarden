using System.Numerics;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Sensing;

/// <summary>The pure half of pot identity. Receipt under test (08-15, two estates): the
/// furniture vector index IS the pot's DataMap key - Papa's Place idx 180/181 against keys
/// 180/181, apartment idx 0/1 against keys 0/1 - and the furniture entry's position is the
/// same position the game object reports.</summary>
public class FurnitureMatchTests
{
    // Papa's Place, 08-15 15:47: the Krakka twins, 0.6y apart.
    private static readonly List<FurniturePlacement> PapasPots =
    [
        new(180, new Vector3(-4.6f, 0.0f, 1.6f)),
        new(181, new Vector3(-4.5f, 0.0f, 1.0f)),
    ];

    [Fact]
    public void ExactPositionNamesTheIndex()
    {
        Assert.Equal(180, FurnitureMatch.IndexAt(PapasPots, new Vector3(-4.6f, 0.0f, 1.6f)));
        Assert.Equal(181, FurnitureMatch.IndexAt(PapasPots, new Vector3(-4.5f, 0.0f, 1.0f)));
    }

    [Fact] // the last bits of a float, not slack for a guess
    public void NearMissInsideToleranceStillNamesTheIndex()
        => Assert.Equal(180, FurnitureMatch.IndexAt(PapasPots, new Vector3(-4.6001f, 0.0f, 1.6002f)));

    [Fact]
    public void NothingNearIsNoAnswer()
        => Assert.Null(FurnitureMatch.IndexAt(PapasPots, new Vector3(0f, 0f, 0f)));

    [Fact] // twins 0.6y apart never contest each other - the tolerance is 12x tighter
    public void TwinsDoNotContestEachOther()
        => Assert.Null(FurnitureMatch.IndexAt(PapasPots, new Vector3(-4.55f, 0.0f, 1.3f)));

    [Fact]
    public void TwoEntriesInsideToleranceRefuse()
    {
        List<FurniturePlacement> stacked =
        [
            new(4, new Vector3(1.00f, 0f, 0f)),
            new(5, new Vector3(1.02f, 0f, 0f)),
        ];
        Assert.Null(FurnitureMatch.IndexAt(stacked, new Vector3(1.01f, 0f, 0f)));
    }

    [Fact] // an exact hit outranks a near one, whatever order the vector is in
    public void ExactBeatsNearby()
    {
        List<FurniturePlacement> mixed =
        [
            new(5, new Vector3(1.02f, 0f, 0f)),
            new(4, new Vector3(1.00f, 0f, 0f)),
        ];
        Assert.Equal(4, FurnitureMatch.IndexAt(mixed, new Vector3(1.00f, 0f, 0f)));
    }

    [Fact]
    public void TwoEntriesAtTheIdenticalSpotRefuse()
    {
        List<FurniturePlacement> doubled =
        [
            new(4, new Vector3(1f, 0f, 0f)),
            new(9, new Vector3(1f, 0f, 0f)),
        ];
        Assert.Null(FurnitureMatch.IndexAt(doubled, new Vector3(1f, 0f, 0f)));
    }

    [Fact]
    public void EmptyVectorIsNoAnswer()
        => Assert.Null(FurnitureMatch.IndexAt([], new Vector3(-4.6f, 0.0f, 1.6f)));
}
