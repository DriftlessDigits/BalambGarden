using BalambGarden.Engine.Census;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

public class AccessRosterTests
{
    // Receipts, captures/2026-08-15-roster-recon.log: roster HouseIds decode to RAW
    // ward/plot (Gardener ward=11 plot=32; FC ward=11 plot=57; Papa's t641 w3 p51;
    // apartment t979 w7 room=29 div=0).
    private static AccessRoster SamsRoster() => new([
        new RosterEstate(new EstateKey(340, 11, 32), "SharedEstate"),
        new RosterEstate(new EstateKey(340, 11, 57), "FreeCompanyEstate"),
        new RosterEstate(new EstateKey(641, 3, 51), "PersonalEstate"),
        new RosterEstate(EstateKey.Apartment(979, 7, 0, 29), "ApartmentRoom"),
    ]);

    [Fact]
    public void HousePartsWithRoomZeroKeyAsTheHouse()
        => Assert.Equal(new EstateKey(340, 11, 32),
            AccessRoster.FromHouseParts(340, 11, 32, room: 0, isApartment: false, division: 0));

    [Fact]
    public void HousePartsWithARealRoomKeyAsThatRoom()
        => Assert.Equal(new EstateKey(340, 11, 57, 7),
            AccessRoster.FromHouseParts(340, 11, 57, room: 7, isApartment: false, division: 0));

    [Fact]
    public void ApartmentPartsUseTheApartmentShape()
        => Assert.Equal(EstateKey.Apartment(979, 7, 0, 29),
            AccessRoster.FromHouseParts(979, 7, 0, room: 29, isApartment: true, division: 0));

    [Theory]
    [InlineData(0, 11, 32, 0, false)]   // no territory
    [InlineData(340, -1, 32, 0, false)] // no ward
    [InlineData(340, 11, -5, 0, false)] // negative plot on a non-apartment
    [InlineData(979, 7, 0, 0, true)]    // apartment with no room number
    public void UnreceiptedShapesConvertToNull(
        ushort territory, int ward, int plot, int room, bool isApartment)
        => Assert.Null(AccessRoster.FromHouseParts(territory, ward, plot, room, isApartment, 0));

    [Fact]
    public void CoversARosteredHouseExactly()
        => Assert.True(SamsRoster().Covers(new EstateKey(340, 11, 32)));

    [Fact]
    public void AHouseRowCoversItsRooms()   // FC chambers room 7 rode the FC row (recon: no chambers row exists)
        => Assert.True(SamsRoster().Covers(new EstateKey(340, 11, 57, 7)));

    [Fact]
    public void CoversTheApartmentRoom()
        => Assert.True(SamsRoster().Covers(EstateKey.Apartment(979, 7, 0, 29)));

    [Fact]
    public void AnApartmentRowCoversOnlyItsOwnRoom()
        => Assert.False(SamsRoster().Covers(EstateKey.Apartment(979, 7, 0, 30)));

    [Fact]
    public void DoesNotCoverARandosPlot()   // recon negative: raw W3 P50 Shirogane matched nothing
        => Assert.False(SamsRoster().Covers(new EstateKey(641, 3, 50)));

    [Fact]
    public void EmptyRosterCoversNothing()
        => Assert.False(AccessRoster.Empty.Covers(new EstateKey(340, 11, 32)));
}
