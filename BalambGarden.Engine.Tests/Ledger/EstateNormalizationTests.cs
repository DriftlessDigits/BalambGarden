using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Ledger;

/// <summary>The 08-15 split-estate repair. Shapes here are Drift's live ledger: Shirogane
/// W4 P52 filed as 641 outdoors and 649 indoors, Lavender Beds W12 P33 as 340 / 344.</summary>
public class EstateNormalizationTests
{
    private static readonly EstateKey Outside = new(641, 3, 51);
    private static readonly EstateKey Inside = new(649, 3, 51, Room: 0);
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-15T04:00:00Z");

    private static EstateRecord Record(EstateKey key, DateTimeOffset seen, string nickname = "")
        => new() { Key = key, FirstSeen = seen, LastVisited = seen, Nickname = nickname };

    private static ClaimedBed Bed(
        EstateKey key, int ordinal, int slot, bool isPot = false, int mapKey = 1038)
        => new()
        {
            Estate = key, MapKey = mapKey, PatchOrdinal = ordinal, BedSlot = slot,
            IsPot = isPot, FirstRecorded = T0,
        };

    [Fact] // the bug itself: one physical plot, two rows
    public void SplitRecordsBecomeOnePhysicalPlot()
    {
        var store = new LedgerStore();
        store.Estates.Add(Record(Outside, T0));
        store.Estates.Add(Record(Inside, T0.AddHours(2)));

        var report = LedgerMigration.NormalizeEstates(store);

        Assert.Equal(1, report.MergedRecords);
        Assert.Empty(report.Warnings);
        var only = Assert.Single(store.Estates);
        Assert.Equal(Outside, only.Key);
        Assert.Equal(T0, only.FirstSeen);                    // discovery receipt survives
        Assert.Equal(T0.AddHours(2), only.LastVisited);      // latest visit wins
    }

    [Fact] // running it twice must not move anything a second time
    public void MigrationIsIdempotent()
    {
        var store = new LedgerStore();
        store.Estates.Add(Record(Outside, T0));
        store.Estates.Add(Record(Inside, T0.AddHours(2)));
        store.Beds.Add(Bed(Inside, ordinal: 1200, slot: 0, isPot: true, mapKey: 1200));
        store.Bindings[Inside.BindingKey(1200)] = 1200;

        LedgerMigration.NormalizeEstates(store);
        var second = LedgerMigration.NormalizeEstates(store);

        Assert.Equal(0, second.MergedRecords);
        Assert.False(second.Changed);
        Assert.Empty(second.Warnings);
        Assert.Single(store.Estates);
        Assert.Single(store.Bindings);
    }

    [Fact] // a ledger that was never split reports nothing at all
    public void CleanLedgerIsUntouched()
    {
        var store = new LedgerStore();
        store.Estates.Add(Record(Outside, T0));
        store.Estates.Add(Record(new EstateKey(340, 11, 32), T0));

        var report = LedgerMigration.NormalizeEstates(store);

        Assert.False(report.Changed);
        Assert.Empty(report.Warnings);
        Assert.Empty(report.Notes);
        Assert.Equal(2, store.Estates.Count);
    }

    [Fact] // pot claims and pot bindings ride the merge into the pot namespace
    public void PotBedsAndBindingsRideToTheCanonicalKey()
    {
        var store = new LedgerStore();
        store.Estates.Add(Record(Outside, T0));
        store.Estates.Add(Record(Inside, T0));
        store.Beds.Add(Bed(Outside, ordinal: 0, slot: 2));
        store.Beds.Add(Bed(Inside, ordinal: 2, slot: 0, isPot: true, mapKey: 2));
        store.Bindings[Outside.BindingKey(0)] = 1038;
        store.Bindings[Inside.BindingKey(2)] = 2;

        LedgerMigration.NormalizeEstates(store);

        Assert.All(store.Beds, b => Assert.Equal(Outside, b.Estate));
        Assert.Equal(1038, store.Bindings[Outside.BindingKey(0)]);
        Assert.Equal(2, store.Bindings[Outside.BindingKey(2, isPot: true)]);
        // The pot's map key 2 must NOT have landed on patch ordinal 2's namespace.
        Assert.False(store.Bindings.ContainsKey(Outside.BindingKey(2)));
    }

    [Fact] // two records for one bed union their planting evidence - nothing is picked over
    public void DuplicateBedsMergeTheirRecords()
    {
        var store = new LedgerStore();
        store.Estates.Add(Record(Outside, T0));
        store.Estates.Add(Record(Inside, T0));

        var kept = Bed(Outside, ordinal: 0, slot: 1);
        Seed.Sight(kept, T0.AddHours(3), 44, 2);
        kept.LastTended = T0.AddHours(3);
        var twin = new ClaimedBed
        {
            Estate = Inside, MapKey = 1038, PatchOrdinal = 0, BedSlot = 1,
            FirstRecorded = T0.AddHours(-1), LastTended = T0.AddHours(5),
        };
        Seed.Sight(twin, T0.AddHours(1), 44, 1);
        store.Beds.Add(kept);
        store.Beds.Add(twin);

        LedgerMigration.NormalizeEstates(store);

        var bed = Assert.Single(store.Beds);
        Assert.Equal(Outside, bed.Estate);
        Assert.Equal(T0.AddHours(-1), bed.FirstRecorded);        // earliest claim is the receipt
        Assert.Equal(T0.AddHours(5), bed.LastTended);        // latest tend wins
        Assert.Equal(2, bed.StageSeen.Count);                // union of both brackets
        Assert.Equal(T0.AddHours(1), bed.StageSeen[1].FirstSeen);
        Assert.Equal(T0.AddHours(3), bed.Latest!.At);        // newest reading wins
    }

    [Fact] // rethink slice 19: the repair runs at every load, so a pot's wilt sighting must ride it
    public void APotWiltSightingRidesTheMove()
    {
        var store = new LedgerStore();
        store.Estates.Add(Record(Outside, T0));
        store.Estates.Add(Record(Inside, T0));
        var pot = Bed(Inside, ordinal: 2, slot: 0, isPot: true, mapKey: 2);
        Seed.Sight(pot, T0, 44, 2);
        pot.SightWilt(T0, wilting: true, growing: true);
        store.Beds.Add(pot);

        LedgerMigration.NormalizeEstates(store);

        Assert.Equal(T0, Assert.Single(store.Beds).WiltSeenAt);
    }

    [Fact] // two records of one pot: the newest sighting stands unless a watering heard after it answered it
    public void TwinPotsMergeTheirWiltSightings()
    {
        var store = new LedgerStore();
        store.Estates.Add(Record(Outside, T0));
        store.Estates.Add(Record(Inside, T0));
        var kept = Bed(Outside, ordinal: 2, slot: 0, isPot: true, mapKey: 2);
        Seed.Sight(kept, T0.AddHours(1), 44, 2);
        kept.SightWilt(T0.AddHours(1), wilting: true, growing: true);
        var twin = Bed(Inside, ordinal: 2, slot: 0, isPot: true, mapKey: 2);
        Seed.Sight(twin, T0, 44, 2);
        store.Beds.Add(kept);
        store.Beds.Add(twin);

        LedgerMigration.NormalizeEstates(store);
        Assert.Equal(T0.AddHours(1), Assert.Single(store.Beds).WiltSeenAt);

        var watered = new LedgerStore();
        watered.Estates.Add(Record(Outside, T0));
        watered.Estates.Add(Record(Inside, T0));
        var seen = Bed(Outside, ordinal: 2, slot: 0, isPot: true, mapKey: 2);
        Seed.Sight(seen, T0.AddHours(1), 44, 2);
        seen.SightWilt(T0.AddHours(1), wilting: true, growing: true);
        var tended = Bed(Inside, ordinal: 2, slot: 0, isPot: true, mapKey: 2);
        Seed.Tend(tended, T0.AddHours(2), 44);
        watered.Beds.Add(seen);
        watered.Beds.Add(tended);

        LedgerMigration.NormalizeEstates(watered);
        Assert.Null(Assert.Single(watered.Beds).WiltSeenAt);
    }

    [Fact] // rethink slice 21: a pot's seen-fine floor rides the move, and twins keep the newer one
    public void APotsSeenFineFloorRidesTheMoveAndTheMerge()
    {
        var store = new LedgerStore();
        store.Estates.Add(Record(Outside, T0));
        store.Estates.Add(Record(Inside, T0));
        var pot = Bed(Inside, ordinal: 2, slot: 0, isPot: true, mapKey: 2);
        Seed.Sight(pot, T0, 44, 2);
        pot.SightWilt(T0, wilting: false, growing: true);
        store.Beds.Add(pot);

        LedgerMigration.NormalizeEstates(store);
        Assert.Equal(T0, Assert.Single(store.Beds).SeenFineAt);

        var twins = new LedgerStore();
        twins.Estates.Add(Record(Outside, T0));
        twins.Estates.Add(Record(Inside, T0));
        var kept = Bed(Outside, ordinal: 2, slot: 0, isPot: true, mapKey: 2);
        Seed.Sight(kept, T0, 44, 2);
        kept.SightWilt(T0, wilting: false, growing: true);
        var twin = Bed(Inside, ordinal: 2, slot: 0, isPot: true, mapKey: 2);
        Seed.Sight(twin, T0.AddHours(1), 44, 2);
        twin.SightWilt(T0.AddHours(1), wilting: false, growing: true);
        twins.Beds.Add(kept);
        twins.Beds.Add(twin);

        LedgerMigration.NormalizeEstates(twins);
        Assert.Equal(T0.AddHours(1), Assert.Single(twins.Beds).SeenFineAt);
    }

    [Fact] // a merged fence that reaches the floor takes it with the plant it was about
    public void AMergedFenceTrimsTheFloor()
    {
        var store = new LedgerStore();
        store.Estates.Add(Record(Outside, T0));
        store.Estates.Add(Record(Inside, T0));
        var kept = Bed(Outside, ordinal: 2, slot: 0, isPot: true, mapKey: 2);
        Seed.Sight(kept, T0, 44, 2);
        kept.SightWilt(T0, wilting: false, growing: true);
        var twin = Bed(Inside, ordinal: 2, slot: 0, isPot: true, mapKey: 2);
        twin.Rebase(RebaseReason.ReadsEmpty, T0.AddHours(1));
        store.Beds.Add(kept);
        store.Beds.Add(twin);

        LedgerMigration.NormalizeEstates(store);

        Assert.Null(Assert.Single(store.Beds).SeenFineAt);
    }

    [Fact] // only the interior record carries a nickname: it comes along
    public void NicknameSurvivesFromEitherSide()
    {
        var store = new LedgerStore();
        store.Estates.Add(Record(Outside, T0));
        store.Estates.Add(Record(Inside, T0, nickname: "Home"));

        LedgerMigration.NormalizeEstates(store);

        Assert.Equal("Home", Assert.Single(store.Estates).Nickname);
    }

    [Fact] // fail closed: two nicknames is a conflict we cannot settle, so keep both rows
    public void ConflictingNicknamesKeepBothRecords()
    {
        var store = new LedgerStore();
        store.Estates.Add(Record(Outside, T0, nickname: "Home"));
        store.Estates.Add(Record(Inside, T0, nickname: "The other one"));

        var report = LedgerMigration.NormalizeEstates(store);

        Assert.Equal(0, report.MergedRecords);
        Assert.Single(report.Warnings);
        Assert.Equal(2, store.Estates.Count);
    }

    [Fact] // fail closed: two districts share this ward/plot, so the interior is unattributable
    public void TwoExteriorsAtOneWardPlotBlockTheMerge()
    {
        var store = new LedgerStore();
        store.Estates.Add(Record(Outside, T0));
        store.Estates.Add(Record(new EstateKey(340, 3, 51), T0));
        store.Estates.Add(Record(Inside, T0));

        var report = LedgerMigration.NormalizeEstates(store);

        Assert.Equal(0, report.MergedRecords);
        Assert.Single(report.Warnings);
        Assert.Equal(3, store.Estates.Count);
    }

    [Fact] // interior ids are shared templates - with no exterior visit, the district is unknown
    public void InteriorWithoutExteriorIsKeptAndReported()
    {
        var store = new LedgerStore();
        store.Estates.Add(Record(Inside, T0));

        var report = LedgerMigration.NormalizeEstates(store);

        Assert.Equal(0, report.MergedRecords);
        Assert.Single(report.Warnings);
        Assert.Single(store.Estates);
    }

    [Fact] // fail closed: two bindings claiming the same slot with different keys, keep both
    public void ConflictingBindingsBlockTheMerge()
    {
        var store = new LedgerStore();
        store.Estates.Add(Record(Outside, T0));
        store.Estates.Add(Record(Inside, T0));
        store.Bindings[Outside.BindingKey(7, isPot: true)] = 700;
        store.Bindings[Inside.BindingKey(7)] = 701;

        var report = LedgerMigration.NormalizeEstates(store);

        Assert.Equal(0, report.MergedRecords);
        Assert.Single(report.Warnings);
        Assert.Equal(2, store.Estates.Count);
        Assert.Equal(700, store.Bindings[Outside.BindingKey(7, isPot: true)]);
        Assert.Equal(701, store.Bindings[Inside.BindingKey(7)]);
    }

    [Fact] // a private room is its own estate now - the split-repair must not eat one
    public void PrivateRoomRecordSurvivesUntouched()
    {
        var room = new EstateKey(641, 3, 51, Room: 7);
        var store = new LedgerStore();
        store.Estates.Add(Record(Outside, T0));
        store.Estates.Add(Record(room, T0));

        var report = LedgerMigration.NormalizeEstates(store);

        Assert.Equal(0, report.MergedRecords);
        Assert.Empty(report.Warnings);
        Assert.Equal(2, store.Estates.Count);
        Assert.Contains(store.Estates, e => e.Key == room);
    }

    [Fact] // apartments have no plot and were never split: no merge, and no warning noise
    public void ApartmentRecordIsIgnoredEntirely()
    {
        var apartment = EstateKey.Apartment(979, ward: 7, division: 0, room: 29);
        var store = new LedgerStore();
        store.Estates.Add(Record(apartment, T0));

        var report = LedgerMigration.NormalizeEstates(store);

        Assert.False(report.Changed);
        Assert.Empty(report.Warnings);
        Assert.Empty(report.Notes);
        Assert.Equal(apartment, Assert.Single(store.Estates).Key);
    }

    [Fact] // a ledger written before any of this loads with every value where it was
    public void OldShapeLedgerLoadsUnchanged()
    {
        // A v2 file as it was written on 08-15, before estates had three shapes: one house
        // plot, one pot bed and one pot binding under it.
        const string json = """
        {
          "Version": 2,
          "Beds": [
            {
              "Estate": { "TerritoryId": 641, "Ward": 3, "Plot": 51, "Room": -1 },
              "MapKey": 129,
              "PatchOrdinal": 129,
              "BedSlot": 0,
              "IsPot": true,
              "ClaimedAt": "2026-08-15T04:00:00+00:00",
              "LastTended": "2026-08-15T05:00:00+00:00",
              "RingStorage": [
                { "At": "2026-08-15T05:00:00+00:00", "SpeciesIndex": 94, "Stage": 2,
                  "Source": "TendReceipt" }
              ]
            }
          ],
          "Bindings": { "641:3:51:-1#pot129": 129 },
          "Estates": [
            {
              "Key": { "TerritoryId": 641, "Ward": 3, "Plot": 51, "Room": -1 },
              "Nickname": "Home",
              "FirstSeen": "2026-08-13T19:10:00+00:00",
              "LastVisited": "2026-08-15T05:00:00+00:00"
            }
          ]
        }
        """;

        var store = LedgerStore.FromJson(json);

        var estate = Assert.Single(store.Estates);
        Assert.Equal(Outside, estate.Key);
        Assert.Equal("Home", estate.Nickname);
        Assert.False(estate.Key.IsApartment);
        Assert.False(estate.Key.IsIndoorOnly);
        Assert.Equal("Home", estate.DisplayName);

        var bed = Assert.Single(store.Beds);
        Assert.Equal(Outside, bed.Estate);
        Assert.True(bed.IsPot);
        Assert.Equal(129, bed.MapKey);
        Assert.Equal(94, Assert.Single(bed.LegacyRing!).SpeciesIndex);   // the v2 ring, read but not yet folded
        Assert.Equal(2, store.Version);
        Assert.Equal(129, store.Bindings[Outside.BindingKey(129, isPot: true)]);

        // The v2 -> v3 fold runs BEFORE the estate repair (pseudocode s7 placement).
        Assert.Equal(1, LedgerMigration.FoldLegacyRings(store));
        Assert.Equal(LedgerStore.CurrentVersion, store.Version);
        Assert.Null(bed.LegacyRing);
        Assert.Equal(94, bed.Latest!.SpeciesIndex);
        Assert.Equal(2, bed.MaxStageSeen);
        Assert.Equal(Hand.Own, bed.LastTendedBy);               // a TendReceipt entry at the clock's instant
        Assert.DoesNotContain("RingStorage", store.ToJson());   // the ring never writes again

        // ...and the repair pass has nothing to say about it, twice.
        Assert.False(LedgerMigration.NormalizeEstates(store).Changed);
        Assert.False(LedgerMigration.NormalizeEstates(store).Changed);
        Assert.Single(store.Estates);
        Assert.Single(store.Beds);
        Assert.Single(store.Bindings);
    }

    [Fact] // one estate key now spans two DataMaps, so the pot space has to be named apart
    public void PotAndPatchNamespacesNeverCollide()
    {
        Assert.NotEqual(Outside.BindingKey(2), Outside.BindingKey(2, isPot: true));

        var store = new LedgerStore();
        var engine = new CensusEngine(store);
        engine.Bind(Outside, patchOrdinal: 2, mapKey: 1313);
        engine.Bind(Outside, patchOrdinal: 2, mapKey: 2, isPot: true);

        Assert.Equal(1313, engine.BoundKey(Outside, 2));
        Assert.Equal(2, engine.BoundKey(Outside, 2, isPot: true));

        var pot = engine.OnReceipt(new ReceiptEvent(
            Outside, PatchOrdinal: 2, BedSlot: 0, ReceiptVerb.PotWater,
            SpeciesIndex: 103, Stage: 2, At: T0, IsPot: true));
        var patch = engine.OnReceipt(new ReceiptEvent(
            Outside, PatchOrdinal: 2, BedSlot: 0, ReceiptVerb.Tend,
            SpeciesIndex: 44, Stage: 2, At: T0));

        Assert.NotNull(pot);
        Assert.NotNull(patch);
        Assert.NotSame(pot, patch);
        Assert.Equal(2, pot!.MapKey);
        Assert.Equal(1313, patch!.MapKey);
        Assert.Equal(2, store.Beds.Count);
    }
}
