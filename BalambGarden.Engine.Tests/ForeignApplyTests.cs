using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

/// <summary>Piece 4 (ruled 2026-08-29): foreign receipts apply against the mint-at-census
/// entity cache; unmatched receipts PARK beside the cursor in the ledger file (ruling 9
/// coherence - a restore rewinds both together) and the stream never dams. Rewritten for the
/// tenancy record (2026-10-14): THE RULE - an older receipt never overwrites newer evidence -
/// and the history-before-cursor contract.</summary>
public class ForeignApplyTests
{
    private static readonly EstateKey Garden = new(340, 11, 32);
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 6, 0, 0, TimeSpan.Zero);
    private const string OwnUser = "k7f3qx";
    private const string Other = "hhbbaa";          // a neutral household slug - never a name
    private const ushort Apple = 50, Fig = 51, Onion = 52;

    private static LedgerStore WithBed(string? wireEntity = "entity-a", bool isPot = false)
    {
        var ledger = new LedgerStore();
        ledger.Beds.Add(new ClaimedBed
        {
            Estate = Garden, MapKey = 2, PatchOrdinal = 0, BedSlot = 3, IsPot = isPot,
            FirstRecorded = T0, WireEntity = wireEntity,
        });
        return ledger;
    }

    private static SyncReceipt Foreign(
        SyncKind kind, string entity = "entity-a", string id = $"{Other}:eraaaa:1",
        DateTimeOffset? at = null, SyncData? data = null)
        => new(id, entity, SyncEntityType.Bed, kind, at ?? T0, SyncSource.Chain,
            data ?? SyncData.Empty);

    private static SyncReceipt Stage(ushort species, byte stage, DateTimeOffset at, int n)
        => Foreign(stage == GardenStage.Ripe ? SyncKind.Ripe : SyncKind.Stage,
            id: $"{Other}:eraaaa:{n}", at: at, data: new SyncData(Species: species, Stage: stage));

    private static SyncReceipt Plant(ushort? species, DateTimeOffset at, int n)
        => Foreign(SyncKind.Plant, id: $"{Other}:eraaaa:{n}", at: at, data: new SyncData(Species: species));

    private static SyncReceipt Tend(ushort species, DateTimeOffset at, int n)
        => Foreign(SyncKind.Tend, id: $"{Other}:eraaaa:{n}", at: at, data: new SyncData(Species: species));

    private static SyncReceipt Harvest(DateTimeOffset at, int n, IReadOnlyList<SyncYield>? yields = null)
        => Foreign(SyncKind.Harvest, id: $"{Other}:eraaaa:{n}", at: at, data: new SyncData(Yields: yields));

    private static ForeignOutcome Apply(LedgerStore ledger, params SyncReceipt[] page)
        => ForeignApply.ApplyPage(ledger, page, OwnUser, T0.AddDays(1));

    // ------------------------------------------------------------------ routing

    [Fact]
    public void Own_receipts_skip_without_touching_anything()
    {
        var ledger = WithBed();
        var outcome = Apply(ledger, Foreign(SyncKind.Harvest, id: $"{OwnUser}:eraaaa:1"));
        Assert.Equal(1, outcome.OwnSkipped);
        Assert.Equal(0, outcome.Applied);
        Assert.Empty(ledger.ForeignHold);
        Assert.Empty(outcome.HistoryToRecord);
    }

    [Fact] // "own" is this install's local word for its own hand; a sender using it as a slug is refused
    public void A_sender_using_the_own_slug_is_rejected()
    {
        var ledger = WithBed();
        var outcome = Apply(ledger, Foreign(SyncKind.Harvest, id: "own:eraaaa:1"));
        Assert.Equal(1, outcome.OwnSkipped);
        Assert.True(ledger.Beds[0].HasTenancy == false);
    }

    [Fact]
    public void Matched_tend_sets_the_clock_and_whose_hand()
    {
        var ledger = WithBed();
        var outcome = Apply(ledger, Foreign(SyncKind.Tend, data: new SyncData(Species: Apple, Stage: 2)));

        Assert.Equal(1, outcome.Applied);
        var bed = ledger.Beds[0];
        Assert.Equal(T0, bed.LastTended);
        Assert.Equal(new Hand(Other), bed.LastTendedBy);
        Assert.Equal(Apple, bed.LastTendedSpecies);
        Assert.True(bed.LastTendedIsExternal);
        Assert.Null(bed.Latest);                       // a tend is a clock, not a reading
    }

    [Fact]
    public void Late_arriving_older_tend_never_regresses_the_clock()
    {
        var ledger = WithBed();
        Seed.Tend(ledger.Beds[0], T0);
        Apply(ledger, Foreign(SyncKind.Tend, at: T0.AddHours(-5)));
        Assert.Equal(T0, ledger.Beds[0].LastTended);
        Assert.Equal(Hand.Own, ledger.Beds[0].LastTendedBy);
    }

    [Fact]
    public void Matched_harvest_is_the_emptying_witness_and_a_history_row()
    {
        var ledger = WithBed();
        Seed.Tend(Seed.Sight(ledger.Beds[0], T0, Apple, 3), T0);

        var outcome = Apply(ledger, Harvest(T0.AddHours(1), 1, [new SyncYield(4789, 3)]));

        var bed = ledger.Beds[0];
        Assert.False(bed.HasTenancy);
        Assert.Equal(RebaseReason.HarvestWitnessed, bed.TenancyResetReason);
        Assert.Equal(T0.AddHours(1), bed.TenancyResetAt);
        var row = Assert.Single(outcome.HistoryToRecord);
        Assert.Equal(HistoryKind.Harvest, row.Kind);
        Assert.Equal(Other, row.By);
        Assert.Equal(3, row.YieldQty);
        Assert.Equal(T0.AddHours(1), row.At);           // the fact's time, not arrival
    }

    [Fact]
    public void Unmatched_receipts_park_and_the_page_still_counts_them()
    {
        var ledger = WithBed(wireEntity: null);   // estate not yet censused under this phrase
        var outcome = Apply(ledger, Foreign(SyncKind.Tend));

        Assert.Equal(1, outcome.Parked);
        Assert.Equal(0, outcome.Applied);
        var parked = Assert.Single(ledger.ForeignHold);
        Assert.Equal($"{Other}:eraaaa:1", parked.Receipt.Id);
        Assert.Empty(outcome.HistoryToRecord);     // no access, no history (finding 8, ruled)
    }

    [Fact]
    public void Parking_dedupes_by_receipt_id()
    {
        var ledger = WithBed(wireEntity: null);
        Apply(ledger, Foreign(SyncKind.Tend));
        Apply(ledger, Foreign(SyncKind.Tend));
        Assert.Single(ledger.ForeignHold);
    }

    [Fact]
    public void Retry_applies_parked_receipts_once_the_entity_mints()
    {
        var ledger = WithBed(wireEntity: null);
        Apply(ledger, Foreign(SyncKind.Tend));

        ledger.Beds[0].WireEntity = "entity-a";   // the census visit minted it
        var landed = ForeignApply.RetryParked(ledger, T0.AddMinutes(5), new GardenHistory(), () => true);

        Assert.Equal(1, landed);
        Assert.Empty(ledger.ForeignHold);
        Assert.Equal(T0, ledger.Beds[0].LastTended);
    }

    [Fact] // a landed row leaves the hold only after its history is on disk; otherwise it re-lands next time
    public void Retry_keeps_landed_rows_parked_while_the_history_save_fails()
    {
        var ledger = WithBed(wireEntity: null);
        var history = new GardenHistory();
        Apply(ledger, Plant(Apple, T0, 1));
        ledger.Beds[0].WireEntity = "entity-a";

        Assert.Equal(1, ForeignApply.RetryParked(ledger, T0.AddMinutes(5), history, () => false));
        Assert.Single(ledger.ForeignHold);            // still parked
        Assert.Single(history.Entries);               // recorded in memory
        Assert.True(history.Dirty);
        Assert.Equal(T0, ledger.Beds[0].PlantedAt);   // the ledger change stands (idempotent on re-land)

        Assert.Equal(1, ForeignApply.RetryParked(ledger, T0.AddMinutes(6), history, () => { history.MarkSaved(); return true; }));
        Assert.Empty(ledger.ForeignHold);
        Assert.Single(history.Entries);               // dedup by id
        Assert.False(history.Dirty);
    }

    [Fact]
    public void Parked_receipts_prune_after_the_lifetime()
    {
        var ledger = WithBed(wireEntity: null);
        Apply(ledger, Foreign(SyncKind.Tend));
        ForeignApply.RetryParked(ledger, T0.AddDays(1) + ForeignApply.ParkedLifetime + TimeSpan.FromDays(1),   // parked at T0+1d (Apply's now)
            new GardenHistory(), () => true);
        Assert.Empty(ledger.ForeignHold);
    }

    [Fact]
    public void Unparseable_ids_drop_rather_than_park_forever()
    {
        var ledger = WithBed();
        var outcome = Apply(ledger, Foreign(SyncKind.Tend, id: "not-a-wire-id"));
        Assert.Equal(1, outcome.Dropped);
        Assert.Empty(ledger.ForeignHold);
    }

    [Fact]
    public void Cursor_and_parking_ride_the_ledger_file()
    {
        var ledger = WithBed(wireEntity: null);
        ledger.RelayCursor = "1042";
        Apply(ledger, Foreign(SyncKind.Tend));

        var back = LedgerStore.FromJson(ledger.ToJson());

        Assert.Equal("1042", back.RelayCursor);
        var parked = Assert.Single(back.ForeignHold);
        Assert.Equal(SyncKind.Tend, parked.Receipt.Kind);
    }

    /// <summary>Ruling 10 display law: external vs internal is WHOSE HAND set the clock - stored
    /// once, read everywhere. A later own tend takes the clock back.</summary>
    [Fact]
    public void External_watering_follows_the_hand()
    {
        var ledger = WithBed();
        Apply(ledger, Foreign(SyncKind.Tend));
        Assert.True(ledger.Beds[0].LastTendedIsExternal);

        Seed.Tend(ledger.Beds[0], T0.AddHours(2));
        Assert.False(ledger.Beds[0].LastTendedIsExternal);
    }

    [Fact]
    public void Cursor_defaults_to_the_full_replay_position()
    {
        Assert.Equal("0", new LedgerStore().RelayCursor);
    }

    // ------------------------------------------------------------------ cursor contract

    [Fact] // history BEFORE cursor: a failed history save holds the inbox position; the retry re-records by id
    public void Absorbing_a_page_advances_the_cursor_only_after_the_history_saves()
    {
        var ledger = WithBed();
        var history = new GardenHistory();
        var page = new RelayPage([Harvest(T0, 1)], "77");

        var held = ForeignApply.AbsorbPage(ledger, page, OwnUser, T0, history, () => false);
        Assert.Equal(1, held.Applied);
        Assert.False(held.CursorAdvanced);
        Assert.Equal("0", ledger.RelayCursor);
        Assert.Single(history.Entries);

        var saved = ForeignApply.AbsorbPage(ledger, page, OwnUser, T0, history, () => { history.MarkSaved(); return true; });
        Assert.True(saved.CursorAdvanced);
        Assert.Equal("77", ledger.RelayCursor);
        Assert.Single(history.Entries);               // the replayed page landed once
    }

    [Fact] // a page that owes the history nothing never asks for a save
    public void Absorbing_a_page_without_history_rows_advances_without_a_save()
    {
        var ledger = WithBed();
        var asked = false;
        var outcome = ForeignApply.AbsorbPage(
            ledger, new RelayPage([Foreign(SyncKind.Tend)], "78"), OwnUser, T0,
            new GardenHistory(), () => { asked = true; return false; });
        Assert.True(outcome.CursorAdvanced);
        Assert.Equal("78", ledger.RelayCursor);
        Assert.False(asked);
    }

    /// <summary>The phrase-change invariant: a new naming key invalidates the entity cache, the
    /// parked receipts, the cursor AND whose hands did what (ruled 2026-10-14) - together. The
    /// timestamps stay: they are facts about the garden, not the household.</summary>
    [Fact]
    public void Reset_household_clears_every_household_scoped_surface_together()
    {
        var ledger = WithBed();
        Apply(ledger, Foreign(SyncKind.Tend, entity: "no-match"));
        Apply(ledger, Plant(Apple, T0, 2), Tend(Apple, T0.AddHours(1), 3));
        ledger.RelayCursor = "1042";

        ledger.ResetHousehold();

        var bed = ledger.Beds[0];
        Assert.Null(bed.WireEntity);
        Assert.Empty(ledger.ForeignHold);
        Assert.Equal("0", ledger.RelayCursor);
        Assert.Null(bed.PlantedBy);
        Assert.Null(bed.LastTendedBy);
        Assert.Equal(T0, bed.PlantedAt);
        Assert.Equal(T0.AddHours(1), bed.LastTended);
    }

    // ------------------------------------------------------------------ THE RULE on the wire
    // An older receipt never overwrites newer evidence: agrees -> adds its evidence; contradicts
    // -> history only; neither ever resets the bed (ruled 2026-10-14).

    [Fact] // "update the stage-2 timestamps, don't trigger a replant"
    public void Older_agreeing_stage_tightens_the_bracket_and_resets_nothing()
    {
        var ledger = WithBed();
        var bed = Seed.Sight(Seed.Plant(ledger.Beds[0], T0, Fig), T0.AddHours(20), Fig, 3);

        var outcome = Apply(ledger, Stage(Fig, 2, T0.AddHours(19), 1));

        Assert.Equal(0, outcome.Contradicted);
        Assert.Empty(outcome.Rebases);
        Assert.Equal(T0.AddHours(19), bed.StageSeen[2].FirstSeen);
        Assert.Equal(3, bed.Latest!.Stage);
        Assert.Equal(T0, bed.PlantedAt);
        Assert.Equal(RebaseReason.PlantReceipt, bed.TenancyResetReason);
    }

    [Fact]
    public void Older_contradicting_stage_changes_nothing_on_the_ledger()
    {
        var ledger = WithBed();
        var bed = Seed.Sight(ledger.Beds[0], T0.AddHours(20), Fig, 3);

        var outcome = Apply(ledger, Stage(Apple, 2, T0.AddHours(19), 1));

        Assert.Equal(1, outcome.Contradicted);
        Assert.Single(bed.StageSeen);
        Assert.Equal(Fig, bed.Latest!.SpeciesIndex);
        Assert.Null(bed.TenancyResetAt);
    }

    [Fact] // newer and contradicting = a replant we have not seen: rebase at our last look, then fold
    public void Newer_contradicting_stage_rebases_then_folds()
    {
        var ledger = WithBed();
        var bed = Seed.Sight(ledger.Beds[0], T0.AddHours(20), Fig, 3);

        var outcome = Apply(ledger, Stage(Fig, 2, T0.AddHours(22), 1));

        var ev = Assert.Single(outcome.Rebases);
        Assert.Equal(RebaseReason.NewTenancySighted, ev.Reason);
        Assert.Equal(T0.AddHours(20), ev.Cut);
        Assert.Equal(new Hand(Other), ev.By);
        Assert.Equal(2, bed.Latest!.Stage);
        Assert.Equal(ObservationSource.External, bed.Latest.Source);
        Assert.Equal([2], bed.StageSeen.Keys.ToArray());
    }

    [Fact] // the pass-2 blocker scene, the full window replayed from cursor 0 over a live ledger
    public void Full_window_replay_over_a_live_ledger_is_byte_identical()
    {
        var ledger = WithBed();
        var history = new GardenHistory();
        SyncReceipt[] window =
        [
            Plant(Fig, T0, 1), Tend(Fig, T0.AddHours(2), 2), Stage(Fig, 2, T0.AddHours(30), 3),
            Stage(Fig, 4, T0.AddHours(70), 4), Harvest(T0.AddHours(72), 5),
            Plant(Onion, T0.AddHours(73), 6), Stage(Onion, 1, T0.AddHours(80), 7),
        ];
        var first = ForeignApply.AbsorbPage(ledger, new RelayPage(window, "7"), OwnUser, T0.AddHours(81), history, () => true);
        var json = ledger.ToJson();

        ledger.RelayCursor = "0";
        var replay = ForeignApply.AbsorbPage(ledger, new RelayPage(window, "7"), OwnUser, T0.AddHours(90), history, () => true);

        Assert.Equal(json, ledger.ToJson());
        Assert.Equal(3, history.Entries.Count);       // two plants, one harvest - once each
        Assert.Equal(7, first.Applied);
        Assert.Equal(7, replay.Applied);
        Assert.DoesNotContain(replay.Rebases, r => r.RemovedAnything);
    }

    [Fact]
    public void Older_plant_with_a_conflicting_species_is_history_only()
    {
        var ledger = WithBed();
        var bed = Seed.Sight(ledger.Beds[0], T0.AddHours(10), Fig, 1);

        var outcome = Apply(ledger, Plant(Apple, T0, 1));

        Assert.Null(bed.PlantedAt);
        Assert.Equal(Fig, bed.Latest!.SpeciesIndex);
        Assert.Null(bed.TenancyResetAt);
        Assert.Equal(HistoryKind.Plant, Assert.Single(outcome.HistoryToRecord).Kind);
    }

    [Fact] // the sowing of the plant we are already watching: anchors, newer evidence stays
    public void Older_plant_of_the_same_species_anchors_without_disturbing_newer_evidence()
    {
        var ledger = WithBed();
        var bed = Seed.Sight(ledger.Beds[0], T0.AddHours(2), Fig, 1);

        Apply(ledger, Plant(Fig, T0, 1));

        Assert.Equal(T0, bed.PlantedAt);
        Assert.Equal(new Hand(Other), bed.PlantedBy);
        Assert.Equal(T0.AddHours(2), bed.Latest!.At);
        Assert.Equal(1, bed.Latest.Stage);
        Assert.Equal(T0.AddHours(2), bed.StageSeen[1].FirstSeen);
    }

    [Fact] // unknown is not agreement (pass-3 should #4)
    public void Older_plant_with_unknown_species_never_anchors()
    {
        var ledger = WithBed();
        var bed = Seed.Sight(ledger.Beds[0], T0.AddHours(2), Fig, 1);

        var outcome = Apply(ledger, Plant(null, T0, 1));

        Assert.Null(bed.PlantedAt);
        Assert.Single(outcome.HistoryToRecord);
    }

    [Fact] // a sowing older than a sowing already known is a previous tenancy
    public void Older_plant_never_displaces_a_newer_known_sowing()
    {
        var ledger = WithBed();
        var bed = Seed.Plant(ledger.Beds[0], T0.AddHours(5), Fig);

        Apply(ledger, Plant(Fig, T0, 1));

        Assert.Equal(T0.AddHours(5), bed.PlantedAt);
        Assert.Equal(Hand.Own, bed.PlantedBy);
    }

    [Fact] // (i): the window arrives out of order - the harvest first fences its own plant and tend
    public void Harvest_arriving_before_its_plant_and_tend_fences_them_on_the_ledger()
    {
        var ledger = WithBed();
        var history = new GardenHistory();

        var outcome = ForeignApply.AbsorbPage(ledger,
            new RelayPage([Harvest(T0.AddHours(3), 3), Plant(Fig, T0, 1), Tend(Fig, T0.AddHours(2), 2)], "3"),
            OwnUser, T0.AddHours(4), history, () => true);

        var bed = ledger.Beds[0];
        Assert.False(bed.HasTenancy);
        Assert.Equal(T0.AddHours(3), bed.TenancyResetAt);
        Assert.Equal(RebaseReason.HarvestWitnessed, bed.TenancyResetReason);
        Assert.Equal(2, history.Entries.Count);       // plant + harvest, both kept
        Assert.Equal(3, outcome.Applied);
    }

    [Fact] // (v): two sowings in one page land the same whichever is read first
    public void Two_plants_in_one_page_reach_the_same_end_state_in_either_order()
    {
        var a = WithBed();
        var b = WithBed();
        Apply(a, Plant(Apple, T0, 1), Plant(Fig, T0.AddHours(5), 2));
        Apply(b, Plant(Fig, T0.AddHours(5), 2), Plant(Apple, T0, 1));

        Assert.Equal(a.ToJson(), b.ToJson());
        Assert.Equal(T0.AddHours(5), a.Beds[0].PlantedAt);
        Assert.Equal(Fig, a.Beds[0].PlantedSpecies);
    }

    [Fact] // pass-3 must-fix #2: no clock clamping - a household clock ahead of ours cannot fence our own act
    public void A_fence_ahead_of_wall_clock_never_fences_an_own_act_but_still_fences_wire_facts()
    {
        var ledger = WithBed();
        var now = T0;
        Apply(ledger, Harvest(now.AddSeconds(30), 1));            // their clock runs 30s ahead
        var bed = ledger.Beds[0];
        Assert.Equal(now.AddSeconds(30), bed.TenancyResetAt);     // stored as sent, never altered

        Assert.True(bed.Tend(now, Hand.Own, 0));                  // own live act lands
        Assert.Equal(now, bed.LastTended);

        Apply(ledger, Tend(Fig, now.AddSeconds(10), 2));          // a wire tend inside the fence does not
        Assert.Equal(Hand.Own, bed.LastTendedBy);
    }

    [Fact] // pass-3 must-fix #1, door two: an older tend naming another plant watered the plant that left
    public void Older_tend_naming_a_different_species_is_refused_but_a_newer_one_lands()
    {
        var ledger = WithBed();
        var bed = Seed.Sight(ledger.Beds[0], T0.AddHours(10), Fig, 1);

        Apply(ledger, Tend(Apple, T0.AddHours(5), 1));
        Assert.Null(bed.LastTended);

        Apply(ledger, Tend(Apple, T0.AddHours(12), 2));           // newer: trust ordering (Q1)
        Assert.Equal(T0.AddHours(12), bed.LastTended);
    }

    [Fact] // pass-3 should #3: "ripe at 5pm" against "stage 3 at 8pm" is not agreement
    public void Older_ripe_against_a_newer_lower_stage_is_a_contradiction()
    {
        var ledger = WithBed();
        var bed = Seed.Sight(ledger.Beds[0], T0.AddHours(8), Fig, 3);

        var outcome = Apply(ledger, Stage(Fig, GardenStage.Ripe, T0.AddHours(5), 1));

        Assert.Equal(1, outcome.Contradicted);
        Assert.Equal([3], bed.StageSeen.Keys.ToArray());
    }

    [Fact] // a late old-tenancy stage must not fold into a same-species replant that has since climbed
    public void Older_stage_from_before_a_lower_sighting_is_a_contradiction()
    {
        var ledger = WithBed();
        var bed = Seed.Sight(Seed.Sight(ledger.Beds[0], T0.AddHours(8), Fig, 1), T0.AddHours(20), Fig, 3);

        var outcome = Apply(ledger, Stage(Fig, 3, T0.AddHours(6), 1));

        Assert.Equal(1, outcome.Contradicted);
        Assert.Equal(T0.AddHours(20), bed.StageSeen[3].FirstSeen);
    }

    [Fact] // a stage that predates a known sowing belongs to the previous plant: the sowing's own fence stops it
    public void Older_stage_predating_the_known_sowing_is_fenced()
    {
        var ledger = WithBed();
        var bed = Seed.Sight(Seed.Plant(ledger.Beds[0], T0.AddHours(5), Fig), T0.AddHours(8), Fig, 1);

        var outcome = Apply(ledger, Stage(Fig, 1, T0.AddHours(3), 1));

        Assert.Equal(0, outcome.Contradicted);                   // fenced, not contradicted - same silence
        Assert.Equal(T0.AddHours(8), bed.StageSeen[1].FirstSeen);
        Assert.Equal(T0.AddHours(5), bed.PlantedAt);
    }

    [Fact] // RetryParked after a fresh sighting of the same plant folds only, never wipes
    public void Parked_stage_landing_after_a_fresh_sighting_folds_only()
    {
        var ledger = WithBed(wireEntity: null);
        Apply(ledger, Stage(Fig, 2, T0.AddHours(10), 1));
        var bed = Seed.Sight(ledger.Beds[0], T0.AddHours(12), Fig, 3);
        bed.WireEntity = "entity-a";

        ForeignApply.RetryParked(ledger, T0.AddHours(13), new GardenHistory(), () => true);

        Assert.Equal(3, bed.Latest!.Stage);
        Assert.Equal(T0.AddHours(10), bed.StageSeen[2].FirstSeen);
        Assert.Null(bed.TenancyResetAt);
    }

    [Fact] // the same harvest echoed twice (a replayed page) is one history row
    public void Foreign_harvest_delivered_twice_is_one_history_row()
    {
        var ledger = WithBed();
        var history = new GardenHistory();
        var page = new RelayPage([Harvest(T0, 1)], "1");
        ForeignApply.AbsorbPage(ledger, page, OwnUser, T0, history, () => { history.MarkSaved(); return true; });
        ForeignApply.AbsorbPage(ledger, page, OwnUser, T0, history, () => { history.MarkSaved(); return true; });
        Assert.Single(history.Entries);
    }
}
