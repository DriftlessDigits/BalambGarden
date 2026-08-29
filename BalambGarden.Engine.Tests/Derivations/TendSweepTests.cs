using BalambGarden.Engine.Derivations;
using Xunit;

namespace BalambGarden.Engine.Tests;

public class TendSweepTests
{
    // The bed-side twin of the pot Water All rule (08-22: the button must not claim waters it
    // cannot pour; ruled onto beds 2026-08-28 "if we have confirmation that a crop is empty or
    // ripe, we can skip it"). A sweep skips only what the live map CONFIRMED skippable - the
    // game refuses the tend at ripe and empty anyway, the filter just stops paying a full
    // dialogue walk to hear the refusal.

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void A_growing_bed_is_swept(byte stage)
        => Assert.True(TendSweep.Include(hasReading: true, occupied: true, stage: stage));

    [Fact]
    public void A_confirmed_ripe_bed_is_skipped()
        => Assert.False(TendSweep.Include(hasReading: true, occupied: true, stage: 4));

    [Fact]
    public void A_confirmed_empty_bed_is_skipped()
        => Assert.False(TendSweep.Include(hasReading: true, occupied: false, stage: 0));

    [Fact]
    public void Map_silence_is_not_a_confirmation()
    {
        // FAIL OPEN, the opposite pole from the pot rule's fail-closed - and both are honest:
        // a pot without a map key has no identity to claim water against, but a bed's identity
        // is its gimmick, map or no map. Skipping on ignorance would leave a thirsty crop dry
        // to save ten seconds; including it just means the game decides in dialogue, which is
        // exactly today's behavior.
        Assert.True(TendSweep.Include(hasReading: false, occupied: false, stage: 0));
        Assert.True(TendSweep.Include(hasReading: false, occupied: true, stage: 4));
    }
}
