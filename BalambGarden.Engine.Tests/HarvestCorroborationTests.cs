using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

public class HarvestCorroborationTests
{
    // MANDATORY two-witness agreement, deliberately stricter than FertilizeCorroboration's
    // absence-proceeds. The fertilize sentences are garden-specific words nothing else prints;
    // the obtain shape is the game's generic loot channel (quest rewards, lockboxes, coffers),
    // and a from-inventory open needs NO target - so a stale bed or pot on previous-target plus
    // an absence-proceeds rule would mint phantom harvests. Two receipts or nothing: the
    // ItemPayload names what arrived, the last map read names what grew there, and only their
    // agreement is a harvest.

    [Fact]
    public void Both_witnesses_agreeing_is_a_harvest()
    {
        var (proceed, _) = HarvestCorroboration.Check(itemSpecies: 88, containerSpecies: 88);
        Assert.True(proceed);
    }

    [Fact]
    public void An_unresolvable_item_is_not_a_harvest()
    {
        // A lockbox's contents, a quest reward - or a flower bouquet, which is the named cost:
        // manual flower harvests never receipt, because their item name resolves to no species.
        var (proceed, _) = HarvestCorroboration.Check(itemSpecies: 0, containerSpecies: 88);
        Assert.False(proceed);
    }

    [Fact]
    public void An_unknown_container_is_not_a_harvest()
    {
        // The map never read this bed or pot (unbound patch, unread side) - one witness is not
        // enough on a generic line.
        var (proceed, _) = HarvestCorroboration.Check(itemSpecies: 88, containerSpecies: 0);
        Assert.False(proceed);
    }

    [Fact]
    public void Disagreeing_witnesses_are_not_a_harvest()
    {
        var (proceed, reason) = HarvestCorroboration.Check(itemSpecies: 88, containerSpecies: 12);
        Assert.False(proceed);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Every_verdict_carries_a_reason_for_the_log()
    {
        Assert.NotEmpty(HarvestCorroboration.Check(88, 88).Reason);
        Assert.NotEmpty(HarvestCorroboration.Check(0, 88).Reason);
        Assert.NotEmpty(HarvestCorroboration.Check(88, 0).Reason);
    }
}
