using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Ledger;

/// <summary>The planting record on its own (ruled 2026-10-14): the fence, the reasons, and THE
/// RULE as the bed itself enforces it. Wire-page scenes live in ForeignApplyTests; these are the
/// bed-level and live-sighting scenes from pseudocode s8 and the pass-3 fixes (s10).</summary>
public class PlantingRecordTests
{
    private static readonly EstateKey Garden = new(340, 11, 32);
    private static readonly DateTimeOffset Mon = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Tue8 = Mon.AddHours(14), Tue9 = Mon.AddHours(15), Tue10 = Mon.AddHours(16);
    private static readonly DateTimeOffset Wed = Mon.AddHours(48);
    private const ushort Apple = 50, Fig = 51, Onion = 52;
    private static readonly Hand Her = Seed.Household;

    private static ClaimedBed Bed() => new()
        { Estate = Garden, MapKey = 110, PatchOrdinal = 0, BedSlot = 3, FirstRecorded = Mon };

    private static (CensusEngine Engine, ClaimedBed Bed) LiveBed(ushort species, byte stage, DateTimeOffset at)
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Garden, 0, 110);
        engine.OnMapSighting(Garden, 110, [new BedReading(3, species, stage, 0, true)], at, mayRecord: true);
        return (engine, engine.LedgerBeds.Single());
    }

    private static int See(CensusEngine engine, ushort species, byte stage, DateTimeOffset at, ICollection<RebaseEvent>? rebases = null)
        => engine.OnMapSighting(Garden, 110, [new BedReading(3, species, stage, 0, true)], at, mayRecord: true, rebases: rebases);

    // ------------------------------------------------------------------ the fence

    [Fact]
    public void A_bare_row_has_no_planting_and_no_fence()
    {
        var bed = Bed();
        Assert.False(bed.HasPlanting);
        Assert.Null(bed.NewestEvidenceAt);
        Assert.Equal(0, bed.MaxStageSeen);
    }

    [Fact] // every reason is reachable and the fence only moves forward
    public void Fence_only_moves_forward_and_refuses_older_wire_cuts()
    {
        var bed = Seed.Sight(Bed(), Mon, Fig, 2);
        Assert.NotNull(bed.Rebase(RebaseReason.ReadsEmpty, Mon.AddHours(1)));
        Assert.Equal(Mon.AddHours(1), bed.PlantingResetAt);

        Assert.Null(bed.Rebase(RebaseReason.HarvestWitnessed, Mon, by: Her));           // older wire cut: refused
        Assert.Equal(RebaseReason.ReadsEmpty, bed.PlantingResetReason);

        var same = bed.Rebase(RebaseReason.PlantReceipt, Mon.AddHours(1), by: Her);   // equal: allowed, reason refreshed
        Assert.NotNull(same);
        Assert.False(same!.RemovedAnything);
        Assert.Equal(RebaseReason.PlantReceipt, bed.PlantingResetReason);
    }

    [Fact] // an own act is never refused by the fence - the game wins, and a future fence is skew
    public void Own_cuts_are_never_refused()
    {
        var bed = Seed.Sight(Bed(), Mon, Fig, 2);
        bed.Rebase(RebaseReason.HarvestWitnessed, Wed, by: Her);                         // a fence far ahead
        Assert.NotNull(bed.Rebase(RebaseReason.HarvestWitnessed, Mon.AddHours(1), by: Hand.Own));
        Assert.NotNull(bed.Rebase(RebaseReason.ReadsEmpty, Mon.AddHours(2)));           // own read, no hand
        Assert.Equal(Wed, bed.PlantingResetAt);                                           // and the fence did not move back
    }

    [Fact] // a bare-row plant resets nothing - the event says so, so the pump logs it quietly
    public void Plant_on_a_bare_row_removes_nothing()
    {
        var bed = Bed();
        var ev = bed.Plant(Mon, Fig, 0, Hand.Own, out var changed);
        Assert.True(changed);
        Assert.False(ev!.RemovedAnything);
        Assert.Equal(RebaseReason.PlantReceipt, ev.Reason);
        Assert.Equal(Mon, bed.PlantedAt);
        Assert.Equal(Fig, bed.Latest!.SpeciesIndex);
        Assert.Equal(ObservationSource.PlantReceipt, bed.Latest.Source);
    }

    [Fact] // (vi): a plant-only row replanted by a live sighting rebases at cut == fence, anchor gone
    public void Plant_only_row_replanted_by_sighting_rebases_at_the_fence()
    {
        var (engine, bed) = LiveBed(Fig, 1, Mon);
        bed.Plant(Mon.AddHours(5), Apple, 0, Her, out _);               // their sowing, newer than our look
        Assert.Equal(Mon.AddHours(5), bed.PlantingResetAt);

        var rebases = new List<RebaseEvent>();
        var changes = new List<SightingChange>();
        engine.OnMapSighting(Garden, 110, [new BedReading(3, Onion, 1, 0, true)], Wed,
            mayRecord: true, changes: changes, rebases: rebases);

        var ev = Assert.Single(rebases);
        Assert.Equal(RebaseReason.NewPlantingSighted, ev.Reason);
        Assert.Equal(Mon.AddHours(5), ev.Cut);                             // cut == fence, allowed
        Assert.Null(bed.PlantedAt);
        Assert.Equal(Onion, bed.Latest!.SpeciesIndex);
        Assert.Empty(changes);                                              // a replant emits no stage change
        Assert.Equal(RebaseReason.NewPlantingSighted, bed.PlantingResetReason);
    }

    // ------------------------------------------------------------------ tends under THE RULE

    [Fact] // pass-2 #2: an own sowing after a household tend of the SAME species clears the clock
    public void Own_plant_after_a_household_tend_clears_the_clock()
    {
        var bed = Seed.Sight(Bed(), Mon, Onion, 1);
        bed.Tend(Mon.AddHours(4), Her, Onion);
        bed.Plant(Mon.AddHours(6), Onion, 0, Hand.Own, out _);
        Assert.Null(bed.LastTended);
        Assert.Equal(Mon.AddHours(6), bed.PlantedAt);
    }

    /// <summary>The pass-3 scene. Drift last saw apples Monday. A housemate waters the apples Tuesday
    /// 8am, pulls them by hand, sows figs at 9am and does not water them. The Wednesday look
    /// sees figs: the figs must read UNWATERED, whichever order the apple tend and the fig
    /// sighting arrive in.</summary>
    [Fact]
    public void Apple_to_fig_replant_drops_the_apple_watering_when_the_tend_arrives_first()
    {
        var (engine, bed) = LiveBed(Apple, 2, Mon);
        Assert.True(bed.Tend(Tue8, Her, Apple));                            // newer than our look: lands (Q1)

        var rebases = new List<RebaseEvent>();
        See(engine, Fig, 1, Wed, rebases);

        var ev = Assert.Single(rebases);
        Assert.Equal(Mon, ev.Cut);                                          // our last look at the apples
        Assert.True(ev.RemovedAnything);
        Assert.Null(bed.LastTended);                                        // the apple tend went with the apples
        Assert.Equal(Fig, bed.Latest!.SpeciesIndex);
    }

    [Fact]
    public void Apple_to_fig_replant_refuses_the_apple_watering_when_the_sighting_arrives_first()
    {
        var (engine, bed) = LiveBed(Apple, 2, Mon);
        See(engine, Fig, 1, Wed);

        Assert.False(bed.Tend(Tue8, Her, Apple));                           // older, names the plant that left
        Assert.Null(bed.LastTended);
    }

    [Fact] // Q1: a post-cut tend naming the NEW species is the new plant's watering - it stays
    public void Apple_to_fig_replant_keeps_a_fig_watering_from_the_ambiguous_window()
    {
        var (engine, bed) = LiveBed(Apple, 2, Mon);
        bed.Tend(Tue10, Her, Fig);

        See(engine, Fig, 1, Wed);

        Assert.Equal(Tue10, bed.LastTended);
        Assert.Equal(Her, bed.LastTendedBy);
        Assert.True(bed.LastTendedIsExternal);
    }

    [Fact] // Q1: a post-cut tend naming NO species is kept too - trust ordering, the hover shows its age
    public void Replant_keeps_a_post_cut_tend_that_named_no_species()
    {
        var (engine, bed) = LiveBed(Apple, 2, Mon);
        bed.Tend(Tue10, Her, 0);
        See(engine, Fig, 1, Wed);
        Assert.Equal(Tue10, bed.LastTended);
    }

    [Fact] // a same-species regrow (stage regression) keeps a post-cut tend - it names the same plant
    public void Same_species_regrow_keeps_a_post_cut_tend()
    {
        var (engine, bed) = LiveBed(Fig, 3, Mon);
        bed.Tend(Tue10, Her, Fig);
        See(engine, Fig, 1, Wed);
        Assert.Equal(RebaseReason.NewPlantingSighted, bed.PlantingResetReason);
        Assert.Equal(Tue10, bed.LastTended);
    }

    [Fact] // pass-2 #5: a row holding only a tend still has a planting; reading empty clears it
    public void Row_holding_only_a_tend_clears_on_an_empty_read()
    {
        var engine = new CensusEngine(new LedgerStore());
        engine.Bind(Garden, 0, 110);
        var bed = engine.OnReceipt(new ReceiptEvent(Garden, 0, 3, ReceiptVerb.Tend, Fig, 1, Mon))!;
        Assert.True(bed.HasPlanting);
        Assert.Null(bed.Latest);

        var landed = engine.OnMapSighting(Garden, 110, [new BedReading(3, 0, 0, 0, false)], Tue8, mayRecord: true);

        Assert.Equal(1, landed);
        Assert.False(bed.HasPlanting);
        Assert.Equal(RebaseReason.ReadsEmpty, bed.PlantingResetReason);
    }

    // ------------------------------------------------------------------ sightings and brackets

    [Fact] // the per-read sighting is the census' normal life: one bracket per stage, widened in place
    public void Repeated_sightings_widen_brackets_instead_of_evicting_anchors()
    {
        var (engine, bed) = LiveBed(Fig, 1, Mon);
        bed.Plant(Mon.AddHours(-1), Fig, 0, Her, out _);                   // the sowing, older than our look: anchors
        for (var i = 0; i < 40; i++)
            See(engine, Fig, 1, Mon.AddMinutes(i));                         // forty reads in forty minutes
        See(engine, Fig, 2, Tue8);

        Assert.Equal(Mon.AddHours(-1), bed.PlantedAt);                      // the ring would have evicted this
        Assert.Equal(Mon, bed.StageSeen[1].FirstSeen);
        Assert.Equal(Mon.AddMinutes(39), bed.StageSeen[1].LastSeen);
        Assert.Equal(Tue8, bed.StageSeen[2].FirstSeen);
        Assert.Null(bed.PlantingResetAt is { } r && r > Mon.AddHours(-1) ? bed.PlantingResetAt : null);
    }

    [Fact] // a straddling bracket is trimmed to its post-cut point and flagged as removal
    public void Rebase_trims_a_straddling_bracket_to_the_post_cut_point()
    {
        var bed = Seed.Sight(Seed.Sight(Bed(), Mon, Fig, 2), Wed, Fig, 2);
        var ev = bed.Rebase(RebaseReason.HarvestWitnessed, Tue8, by: Her)!;
        Assert.True(ev.RemovedAnything);
        Assert.Equal(Wed, bed.StageSeen[2].FirstSeen);
        Assert.Equal(Wed, bed.StageSeen[2].LastSeen);
        Assert.Equal(2, bed.Latest!.Stage);                                 // Wed's reading is after the cut
    }

    [Fact] // the live sighting emits a stage change against the belief walking in, before the sight lands
    public void Sighting_emits_a_change_against_the_prior_belief()
    {
        var (engine, _) = LiveBed(Fig, 1, Mon);
        var changes = new List<SightingChange>();
        engine.OnMapSighting(Garden, 110, [new BedReading(3, Fig, 2, 0, true)], Tue8, mayRecord: true, changes: changes);
        var change = Assert.Single(changes);
        Assert.Equal(2, change.Stage);
    }

    [Fact] // an own manual plant that re-read the map first keeps that reading (pass-3 nit)
    public void Plant_carries_the_receipt_stage_into_latest()
    {
        var bed = Bed();
        bed.Plant(Mon, Fig, 1, Hand.Own, out _);
        Assert.Equal(1, bed.Latest!.Stage);
        Assert.Equal(Mon, bed.StageSeen[1].FirstSeen);
    }

    [Fact] // a replayed own plant at the same instant is a no-op
    public void Same_instant_plant_is_a_replay()
    {
        var bed = Seed.Plant(Bed(), Mon, Fig);
        Seed.Sight(bed, Tue8, Fig, 1);
        Assert.Null(bed.Plant(Mon, Fig, 0, Hand.Own, out var changed));
        Assert.False(changed);
        Assert.Equal(1, bed.Latest!.Stage);
    }

    [Fact] // the contradiction rule is one copy: a live stage regression on the same species is a new planting
    public void Contradicts_reads_the_planting_not_only_the_last_look()
    {
        var bed = Seed.Sight(Seed.Sight(Bed(), Mon, Fig, 3), Tue8, Fig, 3);
        Assert.True(bed.Contradicts(Fig, 2));
        Assert.True(bed.Contradicts(Apple, 3));
        Assert.False(bed.Contradicts(Fig, 3));
        Assert.False(bed.Contradicts(Fig, 4));
        Assert.False(bed.Contradicts(0, 1));
        Assert.False(bed.Contradicts(Fig, 0));
    }
}
