using BalambGarden.Engine.Domain;
using Xunit;

namespace BalambGarden.Engine.Tests.Domain;

public class DomainTablesTests
{
    private static readonly DomainTables T = DomainTables.Load();

    [Fact] // receipts: SpeciesTable.g.cs verified in-game 08-12
    public void KnownSpeciesNamesDecode()
    {
        Assert.Equal("Mirror Apple", T.SpeciesName(0x11));
        Assert.Equal("Old World Fig", T.SpeciesName(0x41));
        Assert.Equal("Krakka Root", T.SpeciesName(0x31));
        Assert.Equal("Royal Kukuru", T.SpeciesName(0x24));
        Assert.Equal("Curiel Root", T.SpeciesName(0x2C));
    }

    [Fact] // 08-13: id 108 exists in-game but is newer than the index snapshot
    public void UnknownSpeciesFallsBackHonestly()
        => Assert.Equal("Unknown (0x6C)", T.SpeciesName(0x6C));

    [Fact]
    public void KrakkaCropTimersLoad()
    {
        var krakka = T.CropBySpeciesIndex(0x31);
        Assert.NotNull(krakka);
        Assert.Equal(72, krakka!.GrowHours);   // 3-day crop
        Assert.Equal(24, krakka.WiltHours);    // fastest wilt tier
        Assert.True(krakka.WitherHours > krakka.WiltHours);
    }

    [Fact] // the Onion pipeline's finisher recipe, verified 54 pairs on 08-12
    public void KukuruCrossCurielMakesThavnairianOnion()
    {
        var kukuru = T.CropBySpeciesIndex(0x24)!;
        var curiel = T.CropBySpeciesIndex(0x2C)!;
        Assert.NotNull(T.CrossResult(kukuru.SeedId, curiel.SeedId));

        // Table fact (08-13): this parent pair is listed under TWO results -
        // Apricot (7751) and Thavnairian Onion (8183) - so single-valued
        // CrossResult cannot pin the onion. Assert the pair table directly.
        var onion = T.CropBySeedId(8183);
        Assert.NotNull(onion);
        Assert.Contains("Thavnairian Onion", onion!.Name);
        Assert.Contains(T.PairsForResult(onion.SeedId),
            p => (p.ParentA == kukuru.SeedId && p.ParentB == curiel.SeedId)
              || (p.ParentA == curiel.SeedId && p.ParentB == kukuru.SeedId));
    }

    [Fact]
    public void CrossResultIsOrderInsensitive()
    {
        var kukuru = T.CropBySpeciesIndex(0x24)!;
        var curiel = T.CropBySpeciesIndex(0x2C)!;
        Assert.Equal(T.CrossResult(kukuru.SeedId, curiel.SeedId),
                     T.CrossResult(curiel.SeedId, kukuru.SeedId));
    }

    [Fact]
    public void SpeciesIndexRoundTripsThroughSeedId()
    {
        var seedId = T.SeedIdBySpeciesIndex(0x24);
        Assert.NotNull(seedId);
        Assert.Equal((ushort)0x24, T.SpeciesIndexBySeedId(seedId!.Value));
    }
}
