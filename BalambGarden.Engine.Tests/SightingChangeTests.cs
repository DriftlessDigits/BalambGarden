using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

/// <summary>Piece 6: observations sync on CHANGE only (schema ruling 12), and the change is
/// detectable exactly where the ledger already compares prev-vs-reading - OnMapSighting.
/// Stage climbs emit Stage; reaching 4 emits Ripe (the payoff signal, its own kind). A new
/// row, a re-read, and a new tenancy emit nothing: first knowledge is not a change, and a
/// replant's story restarts with its plant receipt.</summary>
public class SightingChangeTests
{
    private static readonly EstateKey Garden = new(340, 11, 32);
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 6, 0, 0, TimeSpan.Zero);

    private static (CensusEngine Census, LedgerStore Ledger) Fresh()
    {
        var ledger = new LedgerStore();
        var census = new CensusEngine(ledger);
        census.Bind(Garden, 0, 13074);
        return (census, ledger);
    }

    private static BedReading Reading(byte stage, ushort species = 50, int slot = 3)
        => new(slot, species, stage, 0, Occupied: true);

    private static List<SightingChange> Sight(CensusEngine census, BedReading reading, DateTimeOffset at)
    {
        var changes = new List<SightingChange>();
        census.OnMapSighting(Garden, 13074, [reading], at, mayRecord: true, changes: changes);
        return changes;
    }

    [Fact]
    public void Stage_climb_emits_a_stage_change()
    {
        var (census, _) = Fresh();
        Sight(census, Reading(2), T0);
        var changes = Sight(census, Reading(3), T0.AddHours(20));

        var change = Assert.Single(changes);
        Assert.Equal(SyncKind.Stage, change.Kind);
        Assert.Equal((byte)3, change.Stage);
        Assert.Equal((ushort)50, change.Species);
        Assert.Equal(3, change.Slot);
    }

    [Fact]
    public void Reaching_four_emits_ripe()
    {
        var (census, _) = Fresh();
        Sight(census, Reading(3), T0);
        var changes = Sight(census, Reading(4), T0.AddHours(20));
        Assert.Equal(SyncKind.Ripe, Assert.Single(changes).Kind);
    }

    [Fact]
    public void First_sighting_is_knowledge_not_change()
    {
        var (census, _) = Fresh();
        Assert.Empty(Sight(census, Reading(2), T0));
    }

    [Fact]
    public void A_re_read_at_the_same_stage_emits_nothing()
    {
        var (census, _) = Fresh();
        Sight(census, Reading(2), T0);
        Assert.Empty(Sight(census, Reading(2), T0.AddMinutes(2)));
    }

    [Fact]
    public void A_new_tenancy_emits_nothing()
    {
        var (census, _) = Fresh();
        Sight(census, Reading(3, species: 50), T0);
        // Different species = replant; the rebase restarts the story, no phantom "change".
        Assert.Empty(Sight(census, Reading(1, species: 93), T0.AddDays(1)));
    }

    [Fact]
    public void No_collector_still_records_normally()
    {
        var (census, ledger) = Fresh();
        census.OnMapSighting(Garden, 13074, [Reading(2)], T0, mayRecord: true);
        census.OnMapSighting(Garden, 13074, [Reading(3)], T0.AddHours(20), mayRecord: true);
        Assert.Equal((byte)3, ledger.Beds[0].Latest!.Stage);
    }

    [Fact]
    public void Unvouched_sightings_emit_nothing()
    {
        var (census, _) = Fresh();
        Sight(census, Reading(2), T0);
        var changes = new List<SightingChange>();
        census.OnMapSighting(Garden, 13074, [Reading(3)], T0.AddHours(20),
            mayRecord: false, changes: changes);
        Assert.Empty(changes);
    }
}
