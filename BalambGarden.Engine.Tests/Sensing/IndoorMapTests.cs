using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Sensing;

public class IndoorMapTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static bool Known(ushort id) => T.SeedIdBySpeciesIndex(id) is not null;

    private static byte[] Bytes(string hex)
        => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
              .Select(h => Convert.ToByte(h, 16)).ToArray();

    // key=129: Garden Sunflower planted ~19:10, dumped 19:12 (receipt bind 08-13)
    private const string Sunflower =
        "67 00 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 7F 00 00 00 00 00 01 00 00 00 00 00 44 00 00 00 00 00 00";

    // key=117: Red Tea Flowers, stage 4, extra byte 01 (pigment suspect)
    private const string TeaFlowers =
        "6B 00 04 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 7F 00 00 00 00 00 01 00 00 00 00 00 44 00 00 00 00 00 00";

    // key=165: five-sub-entry furniture (NOT a pot) - must not decode as one
    private const string MultiSlot =
        "02 00 03 00 00 00 03 00 03 00 00 00 5C 01 01 00 00 00 5C 01 01 00 00 00 " +
        "5C 01 01 00 00 7F 00 00 00 00 00 01 00 00 00 00 00 44 00 00 00 00 00 00";

    [Fact]
    public void SunflowerPotDecodes()
    {
        var pot = MapFormat.DecodeIndoorEntry(Bytes(Sunflower), Known);
        Assert.NotNull(pot);
        Assert.Equal(0x67, pot!.SpeciesIndex);
        Assert.Equal(1, pot.Stage);
        Assert.True(pot.Occupied);
        Assert.True(pot.Recognized);
    }

    [Fact] // extra byte preserved raw - pigment is a HYPOTHESIS, never interpreted here
    public void ExtraBytePreservedNotInterpreted()
    {
        var pot = MapFormat.DecodeIndoorEntry(Bytes(TeaFlowers), Known)!;
        Assert.Equal(0x6B, pot.SpeciesIndex);
        Assert.Equal(4, pot.Stage);
        Assert.Equal(0x01, pot.Extra);
    }

    // key=126 at Mama's Place (08-16 10:34): Sam's eyes say BLUE Lupins, ripe. b2=0x14:
    // pigment in the high nibble (1=blue), stage in the low (4=ripe). Same split receipted
    // at three estates: yellow daisies 0x24, FC cosmos 0x54, unpigmented crops 0x01/0x02.
    private const string BlueLupins =
        "66 00 14 00 02 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 7F 00 00 00 00 00 39 00 00 00 00 00 BF 00 00 00 00 00 00";

    [Fact] // b2 = (pigment << 4) | stage - the 08-16 receipt that killed "Stage 20"
    public void PigmentedFlowerSplitsColorFromStage()
    {
        var pot = MapFormat.DecodeIndoorEntry(Bytes(BlueLupins), Known)!;
        Assert.Equal(0x66, pot.SpeciesIndex);
        Assert.Equal(4, pot.Stage);
        Assert.Equal(1, pot.Color);
    }

    [Fact] // yellow daisies, Mama's pot 129: pigment 2, ripe
    public void YellowDaisiesDecode()
    {
        var pot = MapFormat.DecodeIndoorEntry(
            Bytes(BlueLupins.Replace("66 00 14", "52 00 24")), Known)!;
        Assert.Equal(0x52, pot.SpeciesIndex);
        Assert.Equal(4, pot.Stage);
        Assert.Equal(2, pot.Color);
    }

    [Fact] // unpigmented crops keep color 0 - the split changes nothing for them
    public void UnpigmentedCropHasColorZero()
    {
        var pot = MapFormat.DecodeIndoorEntry(Bytes(Sunflower), Known)!;
        Assert.Equal(1, pot.Stage);
        Assert.Equal(0, pot.Color);
    }

    // Papa's Krakka twins, 08-16 12:01 - the wilt lab's verdict. Byte-identical except
    // offset +4, and exactly one wilts by Sam's eyes: b4 is the wilt flag. First pot-wilt
    // receipt ever taken; pot-immortality dies for normal crops (flower tail unaffected).
    private const string WiltingTwin =
        "31 00 02 00 01 FF 00 00 00 00 00 FF 00 00 00 00 00 FF 00 00 00 00 00 FF " +
        "00 00 00 00 00 7F 00 00 00 00 00 FF 00 00 00 00 00 01 00 00 00 00 00 00";

    [Fact]
    public void WiltByteDecodes()
    {
        var dry = MapFormat.DecodeIndoorEntry(Bytes(WiltingTwin), Known)!;
        Assert.Equal(1, dry.Wilt);
        var watered = MapFormat.DecodeIndoorEntry(
            Bytes(WiltingTwin.Replace("31 00 02 00 01", "31 00 02 00 00")), Known)!;
        Assert.Equal(0, watered.Wilt);
    }

    [Fact] // ripe flowers carry b4=2 everywhere (Mama's, FC) - a third state, kept raw
    public void RipeFlowerThirdStateKeptRaw()
    {
        var pot = MapFormat.DecodeIndoorEntry(Bytes(BlueLupins), Known)!;
        Assert.Equal(2, pot.Wilt);
    }

    [Fact] // multi-slot furniture must be rejected, not misread as a pot
    public void MultiSlotFurnitureIsNotAPot()
        => Assert.Null(MapFormat.DecodeIndoorEntry(Bytes(MultiSlot), Known));

    [Fact] // id 0x6C exists in-game but not in the index: tracked, flagged unrecognized
    public void NewerThanIndexSpeciesIsTrackedButUnrecognized()
    {
        var hex = TeaFlowers.Replace("6B 00 04 01", "6C 00 04 08");
        var pot = MapFormat.DecodeIndoorEntry(Bytes(hex), Known)!;
        Assert.Equal(0x6C, pot.SpeciesIndex);
        Assert.True(pot.Occupied);
        Assert.False(pot.Recognized);
    }
}
