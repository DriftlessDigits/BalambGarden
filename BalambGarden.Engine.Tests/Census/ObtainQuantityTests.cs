using BalambGarden.Engine.Census;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

/// <summary>Q3 (ruled 2026-10-14): the obtain line's count is kept - it is the quantity half
/// of the wire's harvest.yields, reserved since 08-23 and shipped empty until now.</summary>
public class ObtainQuantityTests
{
    [Theory]
    [InlineData("You obtain 9 krakka roots.", "krakka roots", 9)]
    [InlineData("You obtain 3 kukuru beans.", "kukuru beans", 3)]
    [InlineData("You obtain a bouquet of red sunflowers.", "bouquet of red sunflowers", 1)]
    [InlineData("You obtain an apple.", "apple", 1)]
    [InlineData("You obtain the thing.", "thing", 1)]
    [InlineData("You obtain Thavnairian onion seeds.", "Thavnairian onion seeds", 1)]
    [InlineData("  You obtain 12 fire shards.  ", "fire shards", 12)]
    public void Parses_item_and_count(string line, string item, int qty)
    {
        var parsed = ObtainLine.Parse(line);
        Assert.NotNull(parsed);
        Assert.Equal(item, parsed!.Value.Item);
        Assert.Equal(qty, parsed.Value.Qty);
        Assert.Equal(item, ObtainLine.Item(line));
        Assert.Equal(qty, ObtainLine.Quantity(line));
        Assert.True(ObtainLine.IsObtain(line));
    }

    [Theory]
    [InlineData("You harvest the crop.")]
    [InlineData("Krakka Root obtained.")]
    [InlineData("")]
    public void Non_obtain_lines_parse_to_nothing(string line)
    {
        Assert.Null(ObtainLine.Parse(line));
        Assert.Null(ObtainLine.Item(line));
        Assert.Null(ObtainLine.Quantity(line));
        Assert.False(ObtainLine.IsObtain(line));
    }
}
