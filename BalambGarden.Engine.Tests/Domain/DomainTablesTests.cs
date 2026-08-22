using BalambGarden.Engine.Domain;
using Xunit;

namespace BalambGarden.Engine.Tests.Domain;

public class DomainTablesTests
{
    private static readonly DomainTables T = DomainTables.Load();

    [Fact] // receipts: species indices verified in-game 08-12
    public void KnownSpeciesNamesDecode()
    {
        Assert.Equal("Mirror Apple", T.SpeciesName(0x11));
        Assert.Equal("Old World Fig", T.SpeciesName(0x41));
        Assert.Equal("Krakka Root", T.SpeciesName(0x31));
        Assert.Equal("Royal Kukuru", T.SpeciesName(0x24));
        Assert.Equal("Curiel Root", T.SpeciesName(0x2C));
    }

    [Fact] // the seed pickers enumerate this; order must not wander between sessions
    public void CropsEnumerateNameOrdered()
    {
        var crops = T.Crops;
        Assert.NotEmpty(crops);
        Assert.Equal(crops.OrderBy(c => c.Name, StringComparer.Ordinal), crops);
        Assert.Contains(crops, c => c.Name == "Krakka Root");
    }

    [Fact] // 08-13: id 108 exists in-game but is newer than the index snapshot
    public void UnknownSpeciesFallsBackHonestly()
        => Assert.Equal("Unknown (0x6C)", T.SpeciesName(0x6C));

    [Fact] // 108 is LISTED but joins to nothing - listing an id is not a claim about it
    public void ListedButUnknownSpeciesHasNoSeedJoin()
    {
        Assert.Null(T.SeedIdBySpeciesIndex(108));
        Assert.Null(T.CropBySpeciesIndex(108));
        Assert.Null(T.SpeciesIndexBySeedId(0));   // no invented seed 0 filling the hole
    }

    [Fact] // the shipped table must be unambiguous; this is the guard, not the recovery
    public void ShippedSpeciesNamesAreUnique()
        => Assert.Empty(T.SpeciesNameCollisions);

    [Fact] // a duplicate display name must not stop the plugin from starting
    public void DuplicateSpeciesNamesFailSoftAndAreReported()
    {
        var collisions = new List<string>();
        var index = DomainTables.BuildNameIndex(
            new Dictionary<ushort, string>
            {
                [40] = "Garden Sunflower",
                [12] = "Krakka Root",
                [103] = "Garden Sunflower",
            },
            collisions);

        Assert.Equal((ushort)40, index["Garden Sunflower"]);   // lowest index wins
        Assert.Equal((ushort)12, index["Krakka Root"]);
        var collision = Assert.Single(collisions);
        Assert.Contains("Garden Sunflower", collision);
        Assert.Contains("103", collision);
    }

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

    [Fact] // 08-16 receipt (Drift's blue lupins wearing "Red"): color is pot PIGMENT, not
    // species - species names are the colorless base plant. 82/93 receipted in Drift's pots
    // (Yellow Daisies item 17999, Purple Cosmos item 30371, xivapi 08-16).
    public void FlowerSpeciesNamesAreColorless()
    {
        Assert.Equal("Daisies", T.SpeciesName(82));
        Assert.Equal("Cosmos", T.SpeciesName(93));
        Assert.Equal("Morning Glories", T.SpeciesName(100));
        Assert.Equal("Lupins", T.SpeciesName(102));
        Assert.Equal("Sunflowers", T.SpeciesName(103));
        Assert.Equal("Tea Flowers", T.SpeciesName(107));
    }

    [Fact] // the name-variant gap closes: Talk speaks colored ITEM names ("Red Sunflowers"
    // for a red-pigment harvest); a leading color word strips to the base species. The
    // bench evidence the 08-15 ledger note waited for arrived 08-16 (pigment nibble).
    public void ColoredItemNamesResolveToBaseSpecies()
    {
        Assert.Equal((ushort)103, T.SpeciesIndexByName("Red Sunflowers"));
        Assert.Equal((ushort)102, T.SpeciesIndexByName("Blue Lupins"));
        Assert.Equal((ushort)82, T.SpeciesIndexByName("Yellow Daisies"));
        Assert.Equal((ushort)102, T.SpeciesIndexByName("Lupins"));   // exact still first
        Assert.Null(T.SpeciesIndexByName("Blue Nonsense"));          // strip never invents
    }

    [Fact] // Talk speaks the harvest ITEM name where it differs from the species name:
    // "Royal Kukuru Bean" receipted on Drift's yard beds (4x unknown-species warnings,
    // 08-16 13:46 dalamud.log) and in Gardener's /xllog. Receipts-only alias table -
    // one entry per proven string, never a pattern.
    public void ReceiptedItemNameAliasesResolve()
    {
        Assert.Equal((ushort)0x24, T.SpeciesIndexByName("Royal Kukuru Bean"));
        Assert.Equal((ushort)0x24, T.SpeciesIndexByName("  royal kukuru bean "));
        Assert.Null(T.SpeciesIndexByName("Royal Nonsense Bean")); // aliases never invent
    }

    [Fact]
    public void SpeciesIndexRoundTripsThroughSeedId()
    {
        var seedId = T.SeedIdBySpeciesIndex(0x24);
        Assert.NotNull(seedId);
        Assert.Equal((ushort)0x24, T.SpeciesIndexBySeedId(seedId!.Value));
    }

    [Fact] // grow hours for ANY species the tables can clock: crop rows keep their own
    // hours; a NAMED species with no crop row is a flowerpot flower - the whole line
    // grows in 1 day (community table, 08-16; our own anchored pots will receipt it).
    // Unnamed species get null: no clock is claimed for a plant we cannot even name.
    public void GrowHoursCoversCropsAndFlowers()
    {
        var krakka = T.CropBySpeciesIndex(0x31)!;
        Assert.Equal(krakka.GrowHours, T.GrowHours(0x31));
        Assert.Equal(24, T.GrowHours(82));    // Daisies - flower, no crop row
        Assert.Equal(24, T.GrowHours(93));    // Cosmos
        Assert.Null(T.GrowHours(0xEE));       // unnamed - no claim
    }
}
