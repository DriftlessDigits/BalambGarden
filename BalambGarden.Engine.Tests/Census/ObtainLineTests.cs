using BalambGarden.Engine.Census;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

public class ObtainLineTests
{
    [Fact] // the live receipt, Sam's pot harvest 2026-08-15
    public void ReadsTheCapturedHarvestLine()
    {
        Assert.Equal("bouquet of red sunflowers",
            ObtainLine.Item("You obtain a bouquet of red sunflowers."));
    }

    [Theory]
    [InlineData("You obtain 3 kukuru beans.", "kukuru beans")]
    [InlineData("You obtain an almond.", "almond")]
    [InlineData("You obtain a mandrake.", "mandrake")]
    public void ReadsArticlesAndQuantities(string line, string item)
        => Assert.Equal(item, ObtainLine.Item(line));

    [Theory]
    [InlineData("You obtain a bouquet of red sunflowers")]   // no terminator
    [InlineData("The gardening bed is ready.")]
    [InlineData("You lose a bag of potting soil.")]
    [InlineData("")]
    public void IgnoresEverythingElse(string line)
        => Assert.False(ObtainLine.IsObtain(line));
}
