using BalambGarden.Engine.Census;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BalambGarden.Game;

/// <summary>Estate identity from HousingManager, raw 0-based (verified via probe
/// 08-12/08-13). An estate is the PHYSICAL PLOT - (exterior district territory, ward, plot)
/// - and reads the same in the yard as in the living room.
///
/// 08-15 fix: the key used to take its territory from ClientState.TerritoryType, which
/// indoors is the HOUSE INTERIOR zone, not the district (Sam's ledger: 641 outside / 649
/// inside for Shirogane W4 P52; 340 / 344 for Lavender Beds W12 P33). Every walk indoors
/// minted a second estate record for the same plot. Worse, interior ids are shared
/// TEMPLATES - the same small-house interior id serves every district - so an interior id
/// can never carry district identity at all.
///
/// Indoors the district comes from HousingManager.GetCurrentIndoorHouseId(): HouseId packs
/// (WorldId, TerritoryTypeId, ward, plot, room) in 8 bytes - TerritoryTypeId at offset 4,
/// per the FFXIVClientStructs assembly shipped in the Dalamud dev dir. That its territory
/// is the EXTERIOR district is NOT confirmed from source, so it is not trusted on faith:
/// the value is checked before it is used, and a check that fails takes the estate to null
/// rather than minting a wrong key.</summary>
internal static unsafe class EstateSensor
{
    private static ulong lastRefusedHouseId = ulong.MaxValue;

    internal static EstateKey? Current()
    {
        var housing = HousingManager.Instance();
        if (housing == null || housing->CurrentTerritory == null)
            return null;

        var ward = housing->GetCurrentWard();
        var plot = housing->GetCurrentPlot();
        if (ward < 0 || plot < 0)
            return null;

        // ClientState hands territory out as uint; the ledger key is ushort (every real
        // territory id fits) - narrow here, at the boundary.
        var here = (ushort)Plugin.ClientState.TerritoryType;

        // Outdoors the zone IS the district: bench-verified 08-14 against the in-game
        // placard ("Plot 52, 4th Ward, Shirogane" == our Ward 4 Plot 52).
        if (!housing->IsInside())
            return new EstateKey(here, ward, plot);

        var houseId = housing->GetCurrentIndoorHouseId();
        var district = houseId.TerritoryTypeId;

        // Apartments are a different identity shape (Plot = building, Room = the apartment)
        // and we have never sensed one. Fail closed instead of filing an apartment as
        // somebody's house.
        if (houseId.IsApartment)
            return RefuseIndoors(houseId, "apartment identity is unsupported");

        if (district == 0)
            return RefuseIndoors(houseId, "HouseId carries no territory");

        // If the HouseId territory is the interior zone we are standing in, it is not the
        // district and there is nothing else to ask.
        if (district == here)
            return RefuseIndoors(houseId, $"HouseId territory {district} is this interior zone");

        // Corroboration, not identity: ward/plot for the key still come from the
        // bench-proven GetCurrentWard/GetCurrentPlot. HouseId's own ward/plot only have to
        // agree closely enough to prove this HouseId describes THIS house - its 0-based vs
        // 1-based convention is not receipt-confirmed, so either reading is accepted, and
        // an off-by-one within a ward cannot change which district the answer names.
        if (!Agrees(houseId.WardIndex, ward) || !Agrees(houseId.PlotIndex, plot))
            return RefuseIndoors(houseId,
                $"HouseId says ward {houseId.WardIndex} plot {houseId.PlotIndex}, "
                + $"HousingManager says ward {ward} plot {plot}");

        return new EstateKey(district, ward, plot);
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
