using BalambGarden.Engine.Domain;
using Xunit;

namespace BalambGarden.Engine.Tests.Domain;

public class SoilsTests
{
    [Fact]
    public void SoilsLoadAndLookUp()
    {
        var tables = DomainTables.Load();
        Assert.True(tables.Soils.Count >= 9);   // 3 regions x 3 grades minimum
        Assert.All(tables.Soils, s => Assert.Contains("Topsoil", s.Name));
        var first = tables.Soils[0];
        Assert.Equal(first, tables.SoilByItemId(first.ItemId));
        Assert.Null(tables.SoilByItemId(1));
    }
}
