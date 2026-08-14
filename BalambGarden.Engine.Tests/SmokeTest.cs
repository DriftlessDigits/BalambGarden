using Xunit;

namespace BalambGarden.Engine.Tests;

public class SmokeTest
{
    [Fact]
    public void TestFrameworkRuns() => Assert.Equal(2, 1 + 1);
}
