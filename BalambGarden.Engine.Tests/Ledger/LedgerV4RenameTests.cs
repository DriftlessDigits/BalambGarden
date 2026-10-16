using Xunit;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Tests.Ledger;

/// <summary>v3 -> v4 (2026-10-15): the fence fields were renamed on disk when "tenancy" became
/// "planting". A v3 file must load with its fences intact under the new names, the rung must be
/// idempotent, and the legacy seam must never write back.</summary>
public class LedgerV4RenameTests
{
    private const string V3Row = """
        {
          "Version": 3,
          "Beds": [
            {
              "Estate": { "TerritoryId": 641, "Ward": 3, "Plot": 51, "Room": -1, "IsApartment": false, "ApartmentDivision": -1, "IsIndoorOnly": false },
              "MapKey": 1038, "PatchOrdinal": 0, "BedSlot": 1, "IsPot": false,
              "TenancyResetAt": "2026-10-15T01:00:00+00:00",
              "TenancyResetReason": "NewTenancySighted"
            },
            {
              "Estate": { "TerritoryId": 641, "Ward": 3, "Plot": 51, "Room": -1, "IsApartment": false, "ApartmentDivision": -1, "IsIndoorOnly": false },
              "MapKey": 1038, "PatchOrdinal": 0, "BedSlot": 2, "IsPot": false
            }
          ]
        }
        """;

    [Fact]
    public void A_v3_fence_moves_onto_the_new_names_and_the_renamed_reason_maps()
    {
        var store = LedgerStore.FromJson(V3Row);
        var moved = LedgerMigration.RenameFenceFields(store);

        Assert.Equal(1, moved);
        Assert.Equal(LedgerStore.CurrentVersion, store.Version);
        var fenced = store.Beds.Single(b => b.BedSlot == 1);
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T01:00:00+00:00"), fenced.PlantingResetAt);
        Assert.Equal(RebaseReason.NewPlantingSighted, fenced.PlantingResetReason);
        Assert.Null(fenced.LegacyFenceAt);
        Assert.Null(fenced.LegacyFenceReason);
        var bare = store.Beds.Single(b => b.BedSlot == 2);
        Assert.Null(bare.PlantingResetAt);
    }

    [Fact]
    public void The_rung_is_idempotent_and_the_legacy_names_never_write_back()
    {
        var store = LedgerStore.FromJson(V3Row);
        LedgerMigration.RenameFenceFields(store);
        var json = store.ToJson();

        Assert.DoesNotContain("TenancyResetAt", json);
        Assert.DoesNotContain("TenancyResetReason", json);
        Assert.Contains("PlantingResetAt", json);

        var again = LedgerStore.FromJson(json);
        Assert.Equal(0, LedgerMigration.RenameFenceFields(again));
        Assert.Equal(RebaseReason.NewPlantingSighted, again.Beds.Single(b => b.BedSlot == 1).PlantingResetReason);
    }

    [Fact]
    public void A_new_name_already_present_wins_over_the_legacy_value()
    {
        var json = V3Row.Replace("\"TenancyResetAt\": \"2026-10-15T01:00:00+00:00\",",
            "\"TenancyResetAt\": \"2026-10-15T01:00:00+00:00\", \"PlantingResetAt\": \"2026-10-15T02:00:00+00:00\", \"PlantingResetReason\": \"ReadsEmpty\",");
        var store = LedgerStore.FromJson(json);
        LedgerMigration.RenameFenceFields(store);
        var row = store.Beds.Single(b => b.BedSlot == 1);
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T02:00:00+00:00"), row.PlantingResetAt);
        Assert.Equal(RebaseReason.ReadsEmpty, row.PlantingResetReason);
    }
}
