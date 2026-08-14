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
        // Table fact (08-13): this parent pair is listed under TWO results -
        // Apricot (7751) and Thavnairian Onion (8183) - hence CrossResults, plural.
        var onion = T.CropBySeedId(8183);
        Assert.NotNull(onion);
        Assert.Contains("Thavnairian Onion", onion!.Name);

        Assert.Contains(onion.SeedId, T.CrossResults(kukuru.SeedId, curiel.SeedId));

        // The pair table itself still documents the recipe, both orderings tolerated.
        Assert.Contains(T.PairsForResult(onion.SeedId),
            p => (p.ParentA == kukuru.SeedId && p.ParentB == curiel.SeedId)
              || (p.ParentA == curiel.SeedId && p.ParentB == kukuru.SeedId));
    }

    [Fact]
    public void CrossResultIsOrderInsensitive()
    {
        var kukuru = T.CropBySpeciesIndex(0x24)!;
        var curiel = T.CropBySpeciesIndex(0x2C)!;
        var forward = T.CrossResults(kukuru.SeedId, curiel.SeedId);
        var reversed = T.CrossResults(curiel.SeedId, kukuru.SeedId);
        Assert.NotEmpty(forward);
        Assert.Equal(forward, reversed);   // same results, same (sorted) order
    }

    [Fact] // xivapi-verified 2026-08-13; 103 receipt-bound in-game (sunflower pot, key=129)
    public void IndoorTailSpeciesAreNamed()
    {
        Assert.Equal("Red Morning Glories", T.SpeciesName(100));
        Assert.Equal("Red Lupins", T.SpeciesName(102));
        Assert.Equal("Garden Sunflower", T.SpeciesName(103));
        Assert.Equal("Red Tea Flowers", T.SpeciesName(107));
    }

    [Fact]
    public void SpeciesIndexRoundTripsThroughSeedId()
    {
        var seedId = T.SeedIdBySpeciesIndex(0x24);
        Assert.NotNull(seedId);
        Assert.Equal((ushort)0x24, T.SpeciesIndexBySeedId(seedId!.Value));
    }
}
