using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Sensing;

public class OutdoorMapTests
{
    private static byte[] Bytes(string hex)
        => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
              .Select(h => Convert.ToByte(h, 16)).ToArray();

    // key=110 (Chelsea 1st Patch, 08-13 19:56 dump): fresh Fig x Mirror replant, stage 1
    private const string Key110 =
        "41 00 01 00 00 10 11 00 01 00 00 51 41 00 01 00 00 00 11 00 01 00 00 00 " +
        "41 00 01 00 00 A7 11 00 01 00 00 69 41 00 01 00 00 00 11 00 01 00 00 00";

    // key=1150 (FC-ward neighbor): 4/8 occupied, alternating empty slots
    private const string Key1150 =
        "1D 00 04 00 00 00 00 00 00 00 00 00 1D 00 04 00 00 00 00 00 00 00 00 00 " +
        "1D 00 04 00 00 02 00 00 00 00 00 00 1D 00 04 00 00 CD 00 00 00 00 00 00";

    // empty entry (key=402): junk columns 02/CD present even with no plants
    private const string KeyEmpty =
        "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 02 00 00 00 00 00 00 00 00 00 00 00 CD 00 00 00 00 00 00";

    [Fact]
    public void ChelseaFirstPatchDecodesFigMirrorAlternation()
    {
        var beds = MapFormat.DecodeOutdoorEntry(Bytes(Key110));
        Assert.Equal(8, beds.Count);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(i, beds[i].Slot);
            Assert.True(beds[i].Occupied);
            Assert.Equal(i % 2 == 0 ? 0x41 : 0x11, beds[i].SpeciesIndex); // Fig / Mirror
            Assert.Equal(1, beds[i].Stage);
        }
    }

    [Fact]
    public void PartialOccupancyDecodes()
    {
        var beds = MapFormat.DecodeOutdoorEntry(Bytes(Key1150));
        Assert.Equal(4, beds.Count(b => b.Occupied));
        Assert.All(beds.Where(b => b.Occupied), b =>
        {
            Assert.Equal(0x1D, b.SpeciesIndex);
            Assert.Equal(4, b.Stage); // ripe
        });
        Assert.All(beds.Where(b => !b.Occupied), b => Assert.Equal(0, b.SpeciesIndex));
    }

    [Fact] // junk columns (02/CD) must not fake occupancy
    public void EmptyEntryReadsEmptyDespiteJunkColumns()
    {
        Assert.True(MapFormat.LooksEmpty(Bytes(KeyEmpty)));
        Assert.All(MapFormat.DecodeOutdoorEntry(Bytes(KeyEmpty)), b => Assert.False(b.Occupied));
    }

    [Fact]
    public void WrongLengthThrows()
        => Assert.Throws<ArgumentException>(() => MapFormat.DecodeOutdoorEntry(new byte[47]));
}
