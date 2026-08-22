using BalambGarden.Engine.Census;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BalambGarden.Game;

/// <summary>Estate identity from HousingManager, raw 0-based (verified via probe
/// 08-12/08-13/08-15). Three shapes come out of here, and every one of them is minted from
/// values that were read live at least once:
///
/// <list type="bullet">
/// <item>a HOUSE - (exterior district territory, ward, plot) - which reads the same in the
/// yard as in the living room (room 0 IS the house);</item>
/// <item>a PRIVATE ROOM in a house - the same, plus room 1..N - its own estate;</item>
/// <item>an APARTMENT - (apartment building territory, ward, division, room).</item>
/// </list>
///
/// <para>08-15 fix: the key used to take its territory from ClientState.TerritoryType, which
/// indoors is the HOUSE INTERIOR zone, not the district (Drift's ledger: 641 outside / 649
/// inside for Shirogane W4 P52; 340 / 344 for Lavender Beds W12 P33). Every walk indoors
/// minted a second estate record for the same plot. Worse, interior ids are shared
/// TEMPLATES - the same small-house interior id serves every district - so an interior id
/// can never carry district identity at all.</para>
///
/// <para>Indoors the district comes from HousingManager.GetCurrentIndoorHouseId(): HouseId
/// packs (WorldId, TerritoryTypeId, ward, plot/division, room) in 8 bytes. That its territory
/// is the EXTERIOR district is now RECEIPT-CONFIRMED (08-15, Drift's FC private chamber:
/// HouseId 0x0037015401CB0039 -> territory 340 = the Lavender Beds exterior district, while
/// the zone we were standing in was 385). The validation below stays anyway - it is three
/// comparisons, and it is what would catch the day the packing changes.</para>
///
/// <para>Anything that still does not fit a receipt fails CLOSED and says so out loud:
/// workshops (zero receipts), a HouseId with no territory, a HouseId that disagrees with
/// HousingManager about where we are.</para></summary>
internal static unsafe class EstateSensor
{
    /// <summary>What GetCurrentPlot() returns inside an apartment (08-15 receipt: territory
    /// 999, ward 7, plot -128, room 29). It is a sentinel, not a plot.</summary>
    private const int ApartmentPlotSentinel = -128;

    private static ulong lastRefusedHouseId = ulong.MaxValue;

    internal static EstateKey? Current()
    {
        var housing = HousingManager.Instance();
        if (housing == null || housing->CurrentTerritory == null)
            return null;

        var ward = housing->GetCurrentWard();
        var plot = housing->GetCurrentPlot();

        // ClientState hands territory out as uint; the ledger key is ushort (every real
        // territory id fits) - narrow here, at the boundary.
        var here = (ushort)Plugin.ClientState.TerritoryType;

        // Outdoors the zone IS the district: bench-verified 08-14 against the in-game
        // placard ("Plot 52, 4th Ward, Shirogane" == our Ward 4 Plot 52).
        if (!housing->IsInside())
            return ward < 0 || plot < 0 ? null : new EstateKey(here, ward, plot);

        var houseId = housing->GetCurrentIndoorHouseId();

        // A workshop is a fourth shape nobody has ever sensed. Refuse before anything else
        // reads a plot out of it.
        if (houseId.IsWorkshop)
            return RefuseIndoors(houseId, "workshop identity has no receipts");

        var district = houseId.TerritoryTypeId;

        if (district == 0)
            return RefuseIndoors(houseId, "HouseId carries no territory");

        // If the HouseId territory is the interior zone we are standing in, it is not the
        // district and there is nothing else to ask.
        if (district == here)
            return RefuseIndoors(houseId, $"HouseId territory {district} is this interior zone");

        var room = housing->GetCurrentRoom();

        if (houseId.IsApartment || plot == ApartmentPlotSentinel)
            return Apartment(houseId, ward, plot, room);

        if (ward < 0 || plot < 0)
            return null;

        // Corroboration, not identity: ward/plot for the key still come from the
        // bench-proven GetCurrentWard/GetCurrentPlot. HouseId's own ward/plot only have to
        // agree closely enough to prove this HouseId describes THIS house - its 0-based vs
        // 1-based convention is not receipt-confirmed, so either reading is accepted, and
        // an off-by-one within a ward cannot change which district the answer names.
        if (!Agrees(houseId.WardIndex, ward) || !Agrees(houseId.PlotIndex, plot))
            return RefuseIndoors(houseId,
                $"HouseId says ward {houseId.WardIndex} plot {houseId.PlotIndex}, "
                + $"HousingManager says ward {ward} plot {plot}");

        // A private room is its own estate (Drift's ruling 08-15 - an FC chamber's pots are
        // nobody else's), and both readers agreed on the number in the one receipt we have
        // (chamber: manager room 7, HouseId room 7), so a disagreement is a surprise worth
        // refusing over. Room 0 is the main floor, which IS the house.
        if (room <= 0)
            return new EstateKey(district, ward, plot);

        if (room != houseId.RoomNumber)
            return RefuseIndoors(houseId,
                $"HouseId says room {houseId.RoomNumber}, HousingManager says room {room}");

        return new EstateKey(district, ward, plot, room);
    }

    /// <summary>The apartment shape (08-15 receipt: HouseId 0x003703D307470080 -> territory
    /// 979 = the apartment BUILDING's own zone, ward 7, division 0, room 29). The building
    /// territory is district-unique - unlike the 999 interior template - so it is the piece
    /// that carries identity; ward and room come from the two readers that agreed, and the
    /// division says which of the ward's apartment buildings this is.</summary>
    private static EstateKey? Apartment(HouseId houseId, int ward, int plot, int room)
    {
        // The sentinel and the flag are two independent readings of the same fact. If they
        // ever disagree, we are somewhere nobody has been - say so rather than guess.
        if (!houseId.IsApartment)
            return RefuseIndoors(houseId,
                $"plot sentinel {plot} says apartment, HouseId says it is not");

        if (ward < 0 || !Agrees(houseId.WardIndex, ward))
            return RefuseIndoors(houseId,
                $"HouseId says ward {houseId.WardIndex}, HousingManager says ward {ward}");

        if (room <= 0 || room != houseId.RoomNumber)
            return RefuseIndoors(houseId,
                $"HouseId says room {houseId.RoomNumber}, HousingManager says room {room}");

        return EstateKey.Apartment(buildingTerritory: houseId.TerritoryTypeId, ward: ward,
            division: houseId.ApartmentDivision, room: room);
    }

    private static bool Agrees(int fromHouseId, int fromManager)
        => fromHouseId == fromManager || fromHouseId == fromManager + 1;

    /// <summary>No estate identity indoors. Says so out loud, once per distinct HouseId, with
    /// the raw value - a silent null here would read as "no house" and the next in-game look
    /// would have nothing to go on.</summary>
    private static EstateKey? RefuseIndoors(HouseId houseId, string why)
    {
        if (lastRefusedHouseId != houseId.Id)
        {
            lastRefusedHouseId = houseId.Id;
            Plugin.Log.Warning(
                $"[Estate] indoors with no district identity - {why} (HouseId 0x{houseId.Id:X16}). "
                + "Pot verbs and pot census are off until this is understood; step outside and "
                + "the plot's own record still holds.");
        }
        return null;
    }

    internal static bool IsInside()
    {
        var housing = HousingManager.Instance();
        return housing != null && housing->IsInside();
    }
}
