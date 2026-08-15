using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

public class ReceiptParserTests
{
    // Header shape mapped live 2026-08-11: SelectString AtkValues[2] = "2nd Bed, 1st Patch".
    [Theory]
    [InlineData("1st Bed, 1st Patch", 0, 0)]
    [InlineData("2nd Bed, 1st Patch", 1, 0)]
    [InlineData("3rd Bed, 2nd Patch", 2, 1)]
    [InlineData("8th Bed, 3rd Patch", 7, 2)]
    public void ParsesBedHeaders(string header, int slot, int ordinal)
    {
        var parsed = ReceiptParser.ParseBedHeader(header);
        Assert.NotNull(parsed);
        Assert.Equal(slot, parsed!.Value.BedSlot);
        Assert.Equal(ordinal, parsed.Value.PatchOrdinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(unknown bed)")]
    [InlineData("Oasis Flowerpot")]
    public void RejectsNonBedHeaders(string header)
        => Assert.Null(ReceiptParser.ParseBedHeader(header));

    [Fact] // reverse lookup feeds receipt -> species joins
    public void SpeciesIndexByNameRoundTrips()
    {
        var tables = DomainTables.Load();
        var index = tables.SpeciesIndexByName("Royal Kukuru");
        Assert.NotNull(index);
        Assert.Equal("Royal Kukuru", tables.SpeciesName(index!.Value));
        Assert.Equal(index, tables.SpeciesIndexByName("  royal kukuru "));
        Assert.Null(tables.SpeciesIndexByName("Definitely Not A Plant"));
    }
}
