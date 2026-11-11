using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Ledger;

/// <summary>Rethink slice 23: a living read after a dead one is a new planting. A dead plant (stage
/// 5) stays dead until someone clears it, so a later read that shows a plant growing in that
/// container is a new sowing, even of the same crop, and the record resets: the dead plant's
/// sowing time and watering leave with it. Driven through the census's live map read (and the
/// household relay's absorb), the paths the plugin runs.</summary>
public class DeadResowTests
{
    private static readonly EstateKey Yard = new(340, 11, 32);
    private const ushort Onion = 50;   // Data/SpeciesIndex.json
    private const ushort Krakka = 49;

    private static readonly DateTimeOffset Sown = DateTimeOffset.Parse("2026-11-08T00:14:00Z");
    private static readonly DateTimeOffset SeenDead = DateTimeOffset.Parse("2026-11-11T00:43:41Z");
    private static readonly DateTimeOffset Later = SeenDead.AddHours(2);

    private readonly LedgerStore store = new();
    private readonly CensusEngine census;
    private readonly List<RebaseEvent> rebases = [];

    public DeadResowTests() => census = new CensusEngine(store);

    private void Read(DateTimeOffset at, byte stage, ushort species = Onion, bool occupied = true)
        => census.OnMapSighting(Yard, 126, [new BedReading(0, species, stage, 0, occupied)], at,
            isPot: true, mayRecord: true, rebases: rebases);

    private ClaimedBed Pot => store.Beds.Single(b => b.IsPot && b.MapKey == 126);

    /// <summary>The 2026-11-10 onion pot: sown and watered Sat, read growing, read dead Tue.</summary>
    private void DeadOnionPot()
    {
        Read(Sown.AddMinutes(5), stage: 1);
        Seed.Plant(Pot, Sown, Onion, by: Seed.Household);
        Seed.Tend(Pot, Sown.AddMinutes(1), Onion, by: Seed.Household);
        Read(SeenDead, stage: 5);
        rebases.Clear();
    }

    [Fact]
    public void ALivingReadAfterADeadReadIsANewPlanting()
    {
        DeadOnionPot();

        Read(Later, stage: 1);   // cleared and resown with onion; no sowing receipt heard

        var reset = Assert.Single(rebases);
        Assert.Equal(RebaseReason.NewPlantingSighted, reset.Reason);
        Assert.True(reset.RemovedAnything);
        Assert.Null(Pot.PlantedAt);              // the dead plant's sowing time is gone
        Assert.Null(Pot.LastTended);             // and its watering
        Assert.Null(ClockWiltSource.RecordedStart(Pot));
        Assert.Equal((byte)1, Pot.Latest!.Stage);
        Assert.Equal(Later, Pot.Latest.At);
    }

    [Fact] // a stage-0 read replaces the dead read as Latest whatever the rule says, so it must reset:
           // left alone, the next growing read would find no dead read and the dead plant's clock would stay
    public void AStageZeroReadAfterADeadReadIsANewPlanting()
    {
        DeadOnionPot();

        Read(Later, stage: 0);

        Assert.Equal(RebaseReason.NewPlantingSighted, Assert.Single(rebases).Reason);
        Assert.Null(Pot.PlantedAt);
        Assert.Null(Pot.LastTended);

        Read(Later.AddHours(1), stage: 1);   // the growing read after it is the same new plant

        Assert.Single(rebases);
    }

    [Fact] // a different crop was already a new planting; still is
    public void ADifferentCropAfterADeadReadIsANewPlanting()
    {
        DeadOnionPot();

        Read(Later, stage: 2, species: Krakka);

        Assert.Equal(RebaseReason.NewPlantingSighted, Assert.Single(rebases).Reason);
        Assert.Null(Pot.PlantedAt);
    }

    [Fact] // the 18:52 re-entry after the clearing: an empty read empties it, as before
    public void AnEmptyReadAfterADeadReadStillEmptiesIt()
    {
        DeadOnionPot();

        Read(Later, stage: 0, species: 0, occupied: false);

        Assert.Equal(RebaseReason.ReadsEmpty, Assert.Single(rebases).Reason);
        Assert.Null(Pot.Latest);
        Assert.Null(Pot.PlantedAt);
    }

    [Fact]
    public void ADeadReadAfterADeadReadChangesNothing()
    {
        DeadOnionPot();

        Read(Later, stage: 5);

        Assert.Empty(rebases);
        Assert.Equal(Sown, Pot.PlantedAt);
        Assert.Equal(Sown.AddMinutes(1), Pot.LastTended);
        Assert.Equal(Later, Pot.Latest!.At);
    }

    [Fact] // the other install's stage observation, newer than the dead read, says the same
    public void AHouseholdStageNewerThanTheDeadReadIsANewPlanting()
    {
        DeadOnionPot();

        var outcome = Pot.Absorb(Later, Onion, stage: 2, Seed.Household, out var rebase);

        Assert.Equal(AbsorbOutcome.Applied, outcome);
        Assert.Equal(RebaseReason.NewPlantingSighted, rebase!.Reason);
        Assert.Null(Pot.PlantedAt);
        Assert.Equal((byte)2, Pot.Latest!.Stage);
    }

    [Fact] // a household stage observation OLDER than the dead read is the dead plant's past: no reset
    public void AHouseholdStageOlderThanTheDeadReadDoesNotReset()
    {
        DeadOnionPot();

        Pot.Absorb(SeenDead.AddHours(-30), Onion, stage: 2, Seed.Household, out var rebase);

        Assert.Null(rebase);
        Assert.Equal(Sown, Pot.PlantedAt);
        Assert.Equal((byte)5, Pot.Latest!.Stage);
    }

    [Fact] // the four onion pots as saved on 2026-11-10: dead, no stage bracket at all - no migration needed
    public void ADeadPotSavedInTheOldShapeLoadsAndResets()
    {
        const string saved = """
            {
              "Version": 4,
              "Beds": [
                {
                  "Estate": { "TerritoryId": 340, "Ward": 11, "Plot": 32, "Room": -1, "IsApartment": false, "ApartmentDivision": -1, "IsIndoorOnly": false },
                  "MapKey": 126, "PatchOrdinal": 126, "BedSlot": 0, "IsPot": true,
                  "ClaimedAt": "2026-08-16T02:57:06.4282158+00:00",
                  "Latest": { "At": "2026-11-11T00:43:41.811661+00:00", "SpeciesIndex": 50, "Stage": 5, "Source": "MapSighting" },
                  "PlantedAt": "2026-11-08T00:14:27.8507425+00:00", "PlantedBy": "hhbbaa", "PlantedSpecies": 50,
                  "StageSeen": {}, "LastTendedSpecies": 0,
                  "PlantingResetAt": "2026-11-08T00:14:27.8507425+00:00", "PlantingResetReason": "PlantReceipt"
                }
              ],
              "Bindings": { "340:11:32:-1#pot126": 126 },
              "Estates": []
            }
            """;
        var loaded = LedgerStore.FromJson(saved);
        var onLoad = Assert.Single(loaded.Beds);
        Assert.Equal((byte)5, onLoad.Latest!.Stage);
        Assert.Empty(onLoad.StageSeen);

        var resets = new List<RebaseEvent>();
        new CensusEngine(loaded).OnMapSighting(Yard, 126, [new BedReading(0, Onion, 1, 0, true)], Later,
            isPot: true, mayRecord: true, rebases: resets);

        var pot = Assert.Single(loaded.Beds);
        Assert.Equal(RebaseReason.NewPlantingSighted, Assert.Single(resets).Reason);
        Assert.Null(pot.PlantedAt);
        Assert.Equal((byte)1, pot.Latest!.Stage);
    }
}
