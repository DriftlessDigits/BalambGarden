using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

/// <summary>The twin-pot join. Every fixture here is shaped like a live 08-15 receipt:
/// planting makes an entry appear (apartment keys 0/1), harvesting clears one (the
/// sunflower morning capture), watering changes nothing at all.</summary>
public class PotDiffTests
{
    private static PotReading Melon(byte stage = 1) => new(0x67, stage, 0, true, true);

    [Fact] // planting: an entry APPEARS at exactly one key -> that key is this pot
    public void AppearedEntryIsThePot()
    {
        var before = new Dictionary<int, PotReading> { [0] = Melon() };
        var after = new Dictionary<int, PotReading> { [0] = Melon(), [1] = Melon() };
        Assert.Equal(1, PotDiff.Join(before, after));
    }

    [Fact] // harvesting: an entry CLEARS at exactly one key
    public void VanishedEntryIsThePot()
    {
        var before = new Dictionary<int, PotReading> { [129] = Melon(), [180] = Melon() };
        var after = new Dictionary<int, PotReading> { [129] = Melon() };
        Assert.Equal(180, PotDiff.Join(before, after));
    }

    [Fact] // the twins: identical entries, and the one that moved is still the one that moved
    public void TwinsAreSeparatedByWhichOneChanged()
    {
        var before = new Dictionary<int, PotReading> { [0] = Melon(), [1] = Melon() };
        var after = new Dictionary<int, PotReading> { [0] = Melon(), [1] = Melon(2) };
        Assert.Equal(1, PotDiff.Join(before, after));
    }

    [Fact] // watering writes nothing (full 48-byte receipt) - no diff, no bind, ever
    public void UnchangedMapBindsNothing()
    {
        var map = new Dictionary<int, PotReading> { [0] = Melon(), [1] = Melon() };
        Assert.Null(PotDiff.Join(map, new Dictionary<int, PotReading>(map)));
        Assert.Empty(PotDiff.ChangedKeys(map, new Dictionary<int, PotReading>(map)));
    }

    [Fact] // two entries moved: ambiguity is not evidence, so nothing binds
    public void TwoChangesBindNothing()
    {
        var before = new Dictionary<int, PotReading> { [0] = Melon(), [1] = Melon() };
        var after = new Dictionary<int, PotReading> { [0] = Melon(2), [1] = Melon(2) };
        Assert.Null(PotDiff.Join(before, after));
        Assert.Equal([0, 1], PotDiff.ChangedKeys(before, after));
    }

    [Fact] // the caller needs the count and the order to say what happened
    public void ChangedKeysAreReportedInKeyOrder()
    {
        var before = new Dictionary<int, PotReading> { [181] = Melon(), [129] = Melon() };
        var after = new Dictionary<int, PotReading>();
        Assert.Equal([129, 181], PotDiff.ChangedKeys(before, after));
    }

    [Fact]
    public void EmptyBothWaysBindsNothing()
        => Assert.Null(PotDiff.Join(
            new Dictionary<int, PotReading>(), new Dictionary<int, PotReading>()));
}
