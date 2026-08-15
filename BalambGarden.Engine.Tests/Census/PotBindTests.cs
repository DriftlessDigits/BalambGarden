using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

public class PotBindTests
{
    // The 08-13 sunflower bind: species 0x67 at exactly one key (129) minutes after planting.
    [Fact]
    public void UniqueSpeciesBinds()
    {
        var map = new Dictionary<int, PotReading>
        {
            [126] = new(0x64, 1, 0, true, true),
            [129] = new(0x67, 1, 0, true, true),
        };
        Assert.Equal(129, PotBind.UniqueSpeciesKey(0x67, map));
    }

    [Fact] // two pots, same species -> ambiguous -> null
    public void DuplicateSpeciesBindsNothing()
    {
        var map = new Dictionary<int, PotReading>
        {
            [129] = new(0x67, 1, 0, true, true),
            [130] = new(0x67, 2, 0, true, true),
        };
        Assert.Null(PotBind.UniqueSpeciesKey(0x67, map));
    }

    [Fact]
    public void AbsentSpeciesBindsNothing()
        => Assert.Null(PotBind.UniqueSpeciesKey(0x67, new Dictionary<int, PotReading>()));
}
