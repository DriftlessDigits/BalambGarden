using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Sensing;

/// <summary>Pigment nibble names, receipts only: 1=Blue (Drift's lupins AND morning
/// glories, 08-16), 2=Yellow (Drift's daisies + item 17999 "Yellow Daisies"). Everything
/// else answers null and renders as the bare species - never a guessed color.</summary>
public class PotPigmentTests
{
    [Fact]
    public void ReceiptedPigmentsAreNamed()
    {
        Assert.Equal("Blue", PotPigment.Name(1));
        Assert.Equal("Yellow", PotPigment.Name(2));
        // 5 = Purple: FC cosmos, double receipt 08-16 - Drift's screenshot AND the game's
        // own Talk line "Purple Cosmos - These flowers are in bloom."
        Assert.Equal("Purple", PotPigment.Name(5));
    }

    [Fact]
    public void UnreceiptedPigmentsAreNull()
    {
        Assert.Null(PotPigment.Name(0));   // unpigmented crops
        Assert.Null(PotPigment.Name(9));
    }
}
