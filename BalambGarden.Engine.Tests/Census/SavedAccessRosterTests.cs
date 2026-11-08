using BalambGarden.Engine.Census;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

public class SavedAccessRosterTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 11, 7, 21, 40, 0, TimeSpan.Zero);

    // One of each key shape (house, private room, apartment), in a deliberate non-sorted order.
    private static AccessRoster Roster() => new([
        new RosterEstate(new EstateKey(641, 3, 51), "PersonalEstate"),
        new RosterEstate(new EstateKey(340, 11, 57, 7), "PersonalChambers"),
        new RosterEstate(EstateKey.Apartment(979, 7, 0, 29), "ApartmentRoom"),
        new RosterEstate(new EstateKey(340, 11, 32), "SharedEstate"),
    ]);

    [Fact]
    public void ASavedRosterReadsBackTheSameEstatesInOrderWithItsReadTime()
    {
        var back = SavedAccessRoster.FromJson(SavedAccessRoster.Of(Roster(), ReadAt).ToJson());

        Assert.Equal(ReadAt, back.ReadAt);
        Assert.Equal(Roster().Estates, back.ToRoster().Estates);
    }

    [Fact]
    public void WithNothingSavedTheFirstReadIsSaved()
        => Assert.True(SavedAccessRoster.NeedsSaving(saved: null, Roster()));

    [Fact]
    public void TheSameListAsSavedIsNotRewritten()
        => Assert.False(SavedAccessRoster.NeedsSaving(Saved(Roster()), Roster()));

    [Fact]
    public void ADroppedEstateIsSaved()
        => Assert.True(SavedAccessRoster.NeedsSaving(Saved(Roster()), new AccessRoster(Roster().Estates.Skip(1).ToList())));

    [Fact]
    public void TheSameEstatesInAnotherOrderAreSaved()
        => Assert.True(SavedAccessRoster.NeedsSaving(Saved(Roster()), new AccessRoster(Roster().Estates.Reverse().ToList())));

    // The file a reader outside the game parses, written out by hand: pins the field names.
    private const string OnDisk = """
        {
          "Version": 1,
          "ReadAt": "2026-11-07T21:40:00+00:00",
          "Estates": [
            { "Key": { "TerritoryId": 979, "Ward": 7, "Plot": -1, "Room": 29 }, "Kind": "ApartmentRoom" },
            { "Key": { "TerritoryId": 340, "Ward": 11, "Plot": 57, "Room": 7 }, "Kind": "PersonalChambers" }
          ]
        }
        """;

    [Fact]
    public void TheFileOnDiskNamesEachEstateByItsKeyAndKind()
    {
        var saved = SavedAccessRoster.FromJson(OnDisk);

        Assert.Equal(ReadAt, saved.ReadAt);
        Assert.Equal(
            [new RosterEstate(EstateKey.Apartment(979, 7, 0, 29), "ApartmentRoom"),
             new RosterEstate(new EstateKey(340, 11, 57, 7), "PersonalChambers")],
            saved.Estates);
    }

    /// <summary>Through the file, as the plugin compares a fresh read against what is on disk.</summary>
    private static SavedAccessRoster Saved(AccessRoster roster)
        => SavedAccessRoster.FromJson(SavedAccessRoster.Of(roster, ReadAt).ToJson());
}
