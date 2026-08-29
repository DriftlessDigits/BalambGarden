using BalambGarden.Engine.Derivations;
using Xunit;

namespace BalambGarden.Engine.Tests;

public class PotWiltRuleTests
{
    // Field receipt 2026-08-26 (the apartment Krakka pair): ripe pots read b4=1 while the game
    // itself offers no watering verb at stage 4 - "wilting - water now" is an impossible
    // instruction there. Whether that 1 is a frozen wilt or a ripe marker is still open (the
    // ambient discriminator: a pot that ripens freshly watered); the DISPLAY claim is settled
    // either way - live stage outranks the byte, mirroring the bed rule (fully grown cannot
    // wilt, ClockWiltSource forces NotApplicable at stage 4).

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_growing_pot_with_the_wilt_byte_claims_wilting(byte stage)
        => Assert.True(PotWiltRule.Claims(wilt: 1, stage: stage));

    [Fact]
    public void A_ripe_pot_never_claims_wilting()
        => Assert.False(PotWiltRule.Claims(wilt: 1, stage: 4));

    [Theory]
    [InlineData(0, 2)]
    [InlineData(0, 4)]
    [InlineData(2, 2)] // flowers broadcast 2 at bloom (08-16 twins) - never a water claim
    [InlineData(2, 4)]
    public void Only_byte_one_ever_claims(byte wilt, byte stage)
        => Assert.False(PotWiltRule.Claims(wilt, stage));
}
