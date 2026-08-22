using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

/// <summary>The three estate shapes, all built from the 08-15 live receipts: Drift's house
/// plot, his FC private chamber (HouseId 0x0037015401CB0039 -> district 340, ward 11, plot
/// 57, room 7), and his apartment (HouseId 0x003703D307470080 -> building territory 979,
/// ward 7, division 0, room 29).</summary>
public class EstateShapeTests
{
    private static readonly EstateKey House = new(340, 11, 57);
    private static readonly EstateKey Room = new(340, 11, 57, Room: 7);
    private static readonly EstateKey Apartment = EstateKey.Apartment(979, ward: 7, division: 0, room: 29);
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-15T16:30:00Z");

    [Fact] // ward/plot +1 is placard-verified; room renders raw (no placard receipt exists)
    public void EachShapeHasItsOwnLabel()
    {
        Assert.Equal("Ward 12 Plot 58", House.DisplayLabel());
        Assert.Equal("Ward 12 Plot 58 Room 7", Room.DisplayLabel());
        Assert.Equal("Apartment W8 R29", Apartment.DisplayLabel());
    }

    [Fact] // the subdivision's building is a different place, and says so
    public void SubdivisionApartmentNamesItsDivision()
    {
        var subdivision = EstateKey.Apartment(979, ward: 7, division: 1, room: 29);
        Assert.Equal("Apartment W8 R29 div 1", subdivision.DisplayLabel());
        Assert.NotEqual(Apartment, subdivision);
        Assert.Equal(1, subdivision.ApartmentDivision);
    }

    [Fact] // a room is NOT its house, and a house's main floor (room 0 -> -1) IS its house
    public void RoomAndHouseAreDifferentEstates()
    {
        Assert.NotEqual(House, Room);
        Assert.NotEqual(House.BindingKey(0), Room.BindingKey(0));
        Assert.False(House.IsIndoorOnly);
        Assert.True(Room.IsIndoorOnly);
        Assert.True(Apartment.IsIndoorOnly);
    }

    [Fact] // the sentinel: apartments carry their division in a negative Plot
    public void ApartmentSentinelDecodes()
    {
        Assert.True(Apartment.IsApartment);
        Assert.Equal(-1, Apartment.Plot);
        Assert.Equal(0, Apartment.ApartmentDivision);
        Assert.Equal(29, Apartment.Room);
        Assert.Equal(979, Apartment.TerritoryId);

        Assert.False(House.IsApartment);
        Assert.False(Room.IsApartment);
        Assert.Equal(-1, House.ApartmentDivision);   // "not an apartment", not division -1
    }

    [Fact] // three shapes in one ledger, three rows, all of them back out of JSON intact
    public void AllThreeShapesRoundTripThroughTheLedger()
    {
        var store = new LedgerStore();
        store.UpsertEstate(House, T0);
        store.UpsertEstate(Room, T0).Nickname = "the chamber";
        store.UpsertEstate(Apartment, T0);
        store.Bindings[Apartment.BindingKey(0, isPot: true)] = 0;   // apartment pot keys start at 0
        store.Beds.Add(new ClaimedBed
        {
            Estate = Apartment, MapKey = 0, PatchOrdinal = 0, BedSlot = 0,
            IsPot = true, FirstRecorded = T0,
        });

        var restored = LedgerStore.FromJson(store.ToJson());

        Assert.Equal(3, restored.Estates.Count);
        Assert.Contains(restored.Estates, e => e.Key == House);
        Assert.Contains(restored.Estates, e => e.Key == Room);
        var apartment = Assert.Single(restored.Estates, e => e.Key == Apartment);
        Assert.True(apartment.Key.IsApartment);
        Assert.Equal(0, apartment.Key.ApartmentDivision);
        Assert.Equal("Apartment W8 R29", apartment.DisplayName);
        Assert.Equal("the chamber", Assert.Single(restored.Estates, e => e.Key == Room).Nickname);
        Assert.Equal(0, restored.Bindings[Apartment.BindingKey(0, isPot: true)]);
        Assert.Equal(Apartment, Assert.Single(restored.Beds).Estate);
    }

    [Fact] // the binding-key string is what Drift's live ledger already holds - do not move it
    public void BindingKeyShapeIsUnchangedForHouses()
    {
        Assert.Equal("340:11:57:-1#0", House.BindingKey(0));
        Assert.Equal("340:11:57:-1#pot3", House.BindingKey(3, isPot: true));
        Assert.Equal("979:7:-1:29#pot0", Apartment.BindingKey(0, isPot: true));
    }
}
