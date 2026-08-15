using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Ledger;

/// <summary>The 08-15 split-estate repair. Shapes here are Sam's live ledger: Shirogane
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
            IsPot = isPot, ClaimedAt = T0,
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

    [Fact] // two records for one bed union their observations - nothing is picked over
    public void DuplicateBedsMergeTheirRings()
    {
        var store = new LedgerStore();
        store.Estates.Add(Record(Outside, T0));
        store.Estates.Add(Record(Inside, T0));

        var kept = Bed(Outside, ordinal: 0, slot: 1);
        kept.Observe(new Observation(T0.AddHours(3), 44, 2, ObservationSource.MapSighting));
        kept.LastTended = T0.AddHours(3);
        var twin = new ClaimedBed
        {
            Estate = Inside, MapKey = 1038, PatchOrdinal = 0, BedSlot = 1,
            ClaimedAt = T0.AddHours(-1), LastTended = T0.AddHours(5),
        };
        twin.Observe(new Observation(T0.AddHours(1), 44, 1, ObservationSource.TendReceipt));
        store.Beds.Add(kept);
        store.Beds.Add(twin);

        LedgerMigration.NormalizeEstates(store);

        var bed = Assert.Single(store.Beds);
        Assert.Equal(Outside, bed.Estate);
        Assert.Equal(T0.AddHours(-1), bed.ClaimedAt);        // earliest claim is the receipt
        Assert.Equal(T0.AddHours(5), bed.LastTended);        // latest tend wins
        Assert.Equal(2, bed.Ring.Count);
        Assert.Equal(T0.AddHours(1), bed.Ring[0].At);        // union, oldest first
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
        Assert.Equal(94, Assert.Single(bed.Ring).SpeciesIndex);
        Assert.Equal(129, store.Bindings[Outside.BindingKey(129, isPot: true)]);

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
