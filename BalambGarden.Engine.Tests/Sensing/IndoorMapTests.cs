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
