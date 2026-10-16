using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Ledger;

public class LedgerTests
{
    private static readonly EstateKey Gardener = new(TerritoryId: 340, Ward: 11, Plot: 32);
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-13T19:10:00Z");

    private static ClaimedBed NewBed() => new()
    {
        Estate = Gardener, MapKey = 110, PatchOrdinal = 0, BedSlot = 3,
        IsPot = false, FirstRecorded = T0,
    };

    [Fact] // 0-based raw storage, +1 display only (Frame 2 / probe-proven gotcha)
    public void DisplayConvertsZeroBasedToHuman()
        => Assert.Equal("Ward 12 Plot 33", Gardener.DisplayLabel());

    [Fact]
    public void FreshLedgerIsCurrentVersion()
        => Assert.Equal(LedgerStore.CurrentVersion, new LedgerStore().Version);

    [Fact] // RUN, not reasoned (pass-3 F): hands, brackets, fence and reason all survive the file
    public void PlantingRecordRoundTripsThroughJson()
    {
        var store = new LedgerStore();
        var bed = NewBed();
        Seed.Plant(bed, T0, 0x41, by: Seed.Household);
        Seed.Sight(bed, T0.AddHours(1), 0x41, 1);
        Seed.Sight(bed, T0.AddHours(30), 0x41, 2);
        Seed.Tend(bed, T0.AddHours(2), 0x41);
        bed.WireEntity = "entity-a";
        store.Beds.Add(bed);
        store.Bindings[$"{Gardener.TerritoryId}:{Gardener.Ward}:{Gardener.Plot}:-1#0"] = 110;

        var json = store.ToJson();
        Assert.Contains("\"PlantedBy\": \"hhbbaa\"", json);      // a bare slug, never an object, never a name
        Assert.DoesNotContain("RingStorage", json);
        Assert.DoesNotContain("HasPlanting", json);                 // derived values never persist
        Assert.DoesNotContain("NewestEvidenceAt", json);

        var restored = LedgerStore.FromJson(json);
        var rb = Assert.Single(restored.Beds);
        Assert.Equal(Gardener, rb.Estate);
        Assert.Equal(110, rb.MapKey);
        Assert.Equal(3, rb.BedSlot);
        Assert.Equal("entity-a", rb.WireEntity);
        Assert.Equal(T0, rb.PlantedAt);
        Assert.Equal(Seed.Household, rb.PlantedBy);
        Assert.Equal(0x41, rb.PlantedSpecies);
        Assert.Equal(T0.AddHours(2), rb.LastTended);
        Assert.Equal(Hand.Own, rb.LastTendedBy);
        Assert.Equal(0x41, rb.LastTendedSpecies);
        Assert.Equal(2, rb.StageSeen.Count);
        Assert.Equal(T0.AddHours(1), rb.StageSeen[1].FirstSeen);
        Assert.Equal(T0.AddHours(30), rb.StageSeen[2].LastSeen);
        Assert.Equal(T0, rb.PlantingResetAt);
        Assert.Equal(RebaseReason.PlantReceipt, rb.PlantingResetReason);
        Assert.Equal(2, rb.Latest!.Stage);
        Assert.Equal(ObservationSource.MapSighting, rb.Latest.Source);
        Assert.Equal(110, restored.Bindings[$"{Gardener.TerritoryId}:{Gardener.Ward}:{Gardener.Plot}:-1#0"]);
    }

    [Fact]
    public void NullHandsRoundTripAsAbsent()
    {
        var store = new LedgerStore();
        var bed = NewBed();
        Seed.Sight(bed, T0, 0x41, 1);
        store.Beds.Add(bed);

        var json = store.ToJson();
        Assert.DoesNotContain("PlantedBy", json);
        var rb = Assert.Single(LedgerStore.FromJson(json).Beds);
        Assert.Null(rb.PlantedBy);
        Assert.Null(rb.LastTendedBy);
        Assert.False(rb.LastTendedIsExternal);
    }

    [Fact] // a v2 file's ring reads into the seam and nothing else breaks
    public void VersionTwoRingReadsIntoTheLegacySeam()
    {
        const string json = """
        {
          "Version": 2,
          "Beds": [
            {
              "Estate": { "TerritoryId": 340, "Ward": 11, "Plot": 32, "Room": -1 },
              "MapKey": 110, "PatchOrdinal": 0, "BedSlot": 3, "IsPot": false,
              "ClaimedAt": "2026-08-13T19:10:00+00:00",
              "LastTended": "2026-08-13T19:10:00+00:00",
              "RingStorage": [
                { "At": "2026-08-13T19:10:00+00:00", "SpeciesIndex": 65, "Stage": 1, "Source": "PlantReceipt" },
                { "At": "2026-08-14T19:10:00+00:00", "SpeciesIndex": 65, "Stage": 2, "Source": "MapSighting" }
              ],
              "Ring": [],
              "Latest": { "At": "2026-08-14T19:10:00+00:00", "SpeciesIndex": 65, "Stage": 2, "Source": "MapSighting" }
            }
          ],
          "Bindings": {}, "Estates": []
        }
        """;
        var store = LedgerStore.FromJson(json);
        var bed = Assert.Single(store.Beds);
        Assert.Equal(2, store.Version);
        Assert.Equal(2, bed.LegacyRing!.Count);
        Assert.Equal(2, bed.Latest!.Stage);
        Assert.Empty(bed.StageSeen);
    }
}
