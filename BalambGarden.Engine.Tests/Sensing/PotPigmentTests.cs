using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Sensing;

/// <summary>Pigment nibble names, receipts only: 1=Blue (Sam's lupins AND morning
/// glories, 08-16), 2=Yellow (Sam's daisies + item 17999 "Yellow Daisies"). Everything
/// else answers null and renders as the bare species - never a guessed color.</summary>
public class PotPigmentTests
{
    [Fact]
    public void ReceiptedPigmentsAreNamed()
    {
        Assert.Equal("Blue", PotPigment.Name(1));
        Assert.Equal("Yellow", PotPigment.Name(2));
    }

    [Fact]
    public void UnreceiptedPigmentsAreNull()
    {
        Assert.Null(PotPigment.Name(0));   // unpigmented crops
        Assert.Null(PotPigment.Name(5));   // FC cosmos - pending Sam's eyes
        Assert.Null(PotPigment.Name(9));
    }
}
