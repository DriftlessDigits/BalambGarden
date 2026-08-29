using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

/// <summary>Piece 4 (ruled 2026-08-29): foreign receipts apply against the mint-at-census
/// entity cache; unmatched receipts PARK beside the cursor in the ledger file (ruling 9
/// coherence - a restore rewinds both together) and the stream never dams. Apply is
/// idempotent by construction because a ledger restore deliberately replays.</summary>
public class ForeignApplyTests
{
    private static readonly EstateKey Garden = new(340, 11, 32);
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 6, 0, 0, TimeSpan.Zero);
    private const string OwnUser = "k7f3qx";

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
        SyncKind kind, string entity = "entity-a", string id = "chelse:eraaaa:1",
        DateTimeOffset? at = null, SyncData? data = null)
        => new(id, entity, SyncEntityType.Bed, kind, at ?? T0, SyncSource.Chain,
            data ?? SyncData.Empty);

    [Fact]
    public void Own_receipts_skip_without_touching_anything()
    {
        var ledger = WithBed();
        var outcome = ForeignApply.ApplyPage(
            ledger, [Foreign(SyncKind.Harvest, id: $"{OwnUser}:eraaaa:1")], OwnUser, T0);
        Assert.Equal(1, outcome.OwnSkipped);
        Assert.Equal(0, outcome.Applied);
        Assert.Empty(ledger.ForeignHold);
    }

    [Fact]
    public void Matched_tend_sets_the_clock_and_an_external_observation()
    {
        var ledger = WithBed();
        var outcome = ForeignApply.ApplyPage(
            ledger, [Foreign(SyncKind.Tend, data: new SyncData(Species: 50, Stage: 2))],
            OwnUser, T0);

        Assert.Equal(1, outcome.Applied);
        var bed = ledger.Beds[0];
        Assert.Equal(T0, bed.LastTended);
        var obs = Assert.Single(bed.Ring);
        Assert.Equal(ObservationSource.External, obs.Source);
        Assert.Equal((ushort)50, obs.SpeciesIndex);
    }

    [Fact]
    public void Late_arriving_older_tend_never_regresses_the_clock()
    {
        var ledger = WithBed();
        ledger.Beds[0].LastTended = T0;
        ForeignApply.ApplyPage(
            ledger, [Foreign(SyncKind.Tend, at: T0.AddHours(-5))], OwnUser, T0);
        Assert.Equal(T0, ledger.Beds[0].LastTended);
    }

    [Fact]
    public void Matched_harvest_is_the_emptying_witness()
    {
        var ledger = WithBed();
        ledger.Beds[0].LastTended = T0;
        ledger.Beds[0].Observe(new Observation(T0, 50, 3, ObservationSource.MapSighting));

        ForeignApply.ApplyPage(ledger, [Foreign(SyncKind.Harvest)], OwnUser, T0);

        Assert.Empty(ledger.Beds[0].Ring);
        Assert.Null(ledger.Beds[0].LastTended);
    }

    [Fact]
    public void Unmatched_receipts_park_and_the_page_still_counts_them()
    {
        var ledger = WithBed(wireEntity: null);   // estate not yet censused under this phrase
        var outcome = ForeignApply.ApplyPage(
            ledger, [Foreign(SyncKind.Tend)], OwnUser, T0);

        Assert.Equal(1, outcome.Parked);
        Assert.Equal(0, outcome.Applied);
        var parked = Assert.Single(ledger.ForeignHold);
        Assert.Equal("chelse:eraaaa:1", parked.Receipt.Id);
    }

    [Fact]
    public void Parking_dedupes_by_receipt_id()
    {
        var ledger = WithBed(wireEntity: null);
        ForeignApply.ApplyPage(ledger, [Foreign(SyncKind.Tend)], OwnUser, T0);
        ForeignApply.ApplyPage(ledger, [Foreign(SyncKind.Tend)], OwnUser, T0.AddMinutes(1));
        Assert.Single(ledger.ForeignHold);
    }

    [Fact]
    public void Retry_applies_parked_receipts_once_the_entity_mints()
    {
        var ledger = WithBed(wireEntity: null);
        ForeignApply.ApplyPage(ledger, [Foreign(SyncKind.Tend)], OwnUser, T0);

        ledger.Beds[0].WireEntity = "entity-a";   // the census visit minted it
        var applied = ForeignApply.RetryParked(ledger, T0.AddMinutes(5));

        Assert.Equal(1, applied);
        Assert.Empty(ledger.ForeignHold);
        Assert.Equal(T0, ledger.Beds[0].LastTended);
    }

    [Fact]
    public void Parked_receipts_prune_after_the_lifetime()
    {
        var ledger = WithBed(wireEntity: null);
        ForeignApply.ApplyPage(ledger, [Foreign(SyncKind.Tend)], OwnUser, T0);
        ForeignApply.RetryParked(ledger, T0 + ForeignApply.ParkedLifetime + TimeSpan.FromDays(1));
        Assert.Empty(ledger.ForeignHold);
    }

    /// <summary>A ledger restore deliberately replays the window (ruling 9); the second pass
    /// must not double anything.</summary>
    [Fact]
    public void Reapplying_the_same_page_is_idempotent()
    {
        var ledger = WithBed();
        var page = new[] { Foreign(SyncKind.Tend, data: new SyncData(Species: 50)) };
        ForeignApply.ApplyPage(ledger, page, OwnUser, T0);
        ForeignApply.ApplyPage(ledger, page, OwnUser, T0.AddMinutes(1));

        Assert.Single(ledger.Beds[0].Ring);
        Assert.Equal(T0, ledger.Beds[0].LastTended);
    }

    [Fact]
    public void Unparseable_ids_drop_rather_than_park_forever()
    {
        var ledger = WithBed();
        var outcome = ForeignApply.ApplyPage(
            ledger, [Foreign(SyncKind.Tend, id: "not-a-wire-id")], OwnUser, T0);
        Assert.Equal(1, outcome.Dropped);
        Assert.Empty(ledger.ForeignHold);
    }

    [Fact]
    public void Cursor_and_parking_ride_the_ledger_file()
    {
        var ledger = WithBed(wireEntity: null);
        ledger.RelayCursor = "1042";
        ForeignApply.ApplyPage(ledger, [Foreign(SyncKind.Tend)], OwnUser, T0);

        var back = LedgerStore.FromJson(ledger.ToJson());

        Assert.Equal("1042", back.RelayCursor);
        var parked = Assert.Single(back.ForeignHold);
        Assert.Equal(SyncKind.Tend, parked.Receipt.Kind);
    }

    /// <summary>Ruling 10 display law: external vs internal, derived - never stored twice.
    /// The clock is external exactly when the ring's External observation stamped it.</summary>
    [Fact]
    public void External_watering_is_derivable_from_the_ring()
    {
        var ledger = WithBed();
        ForeignApply.ApplyPage(ledger, [Foreign(SyncKind.Tend)], OwnUser, T0);
        Assert.True(ledger.Beds[0].LastTendedIsExternal);

        // A later LOCAL tend takes the clock back: nothing external stamped THIS time.
        ledger.Beds[0].LastTended = T0.AddHours(2);
        Assert.False(ledger.Beds[0].LastTendedIsExternal);
    }

    [Fact]
    public void Cursor_defaults_to_the_full_replay_position()
    {
        Assert.Equal("0", new LedgerStore().RelayCursor);
    }
}
