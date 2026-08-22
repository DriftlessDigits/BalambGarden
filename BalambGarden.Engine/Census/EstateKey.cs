namespace BalambGarden.Engine.Census;

/// <summary>Estate identity, in three shapes - all of them keyed on values that read the
/// same every visit, and all of them stored RAW (HousingManager's own 0-based numbers; the
/// +1 for humans happens ONLY in <see cref="DisplayLabel"/>).
///
/// <list type="number">
/// <item><b>House / plot</b> - (exterior district territory, ward, plot), Room = -1. One
/// estate whether you are in the yard or the living room: the interior territory id is a
/// different number AND a shared template across districts, so it can never carry identity
/// (08-15 fix). Room 0 - a house's main floor - IS the house, so it keys as Room = -1.</item>
///
/// <item><b>Private room in a house</b> - (district, ward, plot, Room = 1..N). Its own
/// estate, distinct from the house it sits in (Drift's ruling 08-15: an FC private chamber
/// gets its own tab; its pots are nobody else's). Receipt: FC chamber HouseId
/// 0x0037015401CB0039 -> territory 340 (Lavender Beds exterior), ward 11, plot 57, room 7,
/// IsApartment=False - the same receipt that finally confirms a house HouseId's
/// TerritoryTypeId is the EXTERIOR district.</item>
///
/// <item><b>Apartment</b> - (apartment BUILDING territory, ward, apartment sentinel in Plot,
/// Room = the apartment number). Receipt: HouseId 0x003703D307470080 -> territory 979 (the
/// building's own zone, district-unique - NOT the 999 interior template), world 55, ward 7,
/// division 0, room 29, IsApartment=True. Apartments have no plot, so <see cref="Plot"/>
/// carries the division instead, as a NEGATIVE sentinel: Plot = -1 - division (division 0 ->
/// -1, the subdivision's building -> -2). Why a sentinel and not a fifth field: <see
/// cref="BindingKey"/> strings are already written into Drift's live ledger, and widening the
/// key would orphan every binding in it. A real plot is never negative, so the two shapes
/// can never collide, and two divisions of one ward stay distinct keys.</item>
/// </list></summary>
public sealed record EstateKey(ushort TerritoryId, int Ward, int Plot, int Room = -1)
{
    /// <summary>Apartments live at a negative Plot; a house plot is 0-based and never is.</summary>
    public bool IsApartment => Plot < 0;

    /// <summary>Which apartment building in the ward (0 = the main one, 1 = the
    /// subdivision's). -1 when this is not an apartment.</summary>
    public int ApartmentDivision => IsApartment ? -Plot - 1 : -1;

    /// <summary>An apartment or a private room: four walls and no yard, so nothing outdoor
    /// can ever belong to it.</summary>
    public bool IsIndoorOnly => IsApartment || Room > 0;

    /// <summary>The apartment shape, from the fields HouseId hands out.</summary>
    public static EstateKey Apartment(ushort buildingTerritory, int ward, int division, int room)
        => new(buildingTerritory, ward, -1 - division, room);

    /// <summary>The one label for any shape.
    ///
    /// <para>Ward and plot render +1: bench-verified 08-14 against the in-game placard
    /// ("Plot 52, 4th Ward, Shirogane" == our Ward 4 Plot 52).</para>
    ///
    /// <para>Room numbers render RAW - what HousingManager and HouseId both said (the FC
    /// chamber read room 7, the apartment room 29). UNVERIFIED: nobody has read a room
    /// number off an in-game placard yet, so whether the game would call that same room
    /// "Room 8" is unknown. Raw is the honest choice - it is the number we actually
    /// measured - and this comment is the receipt that says so.</para></summary>
    public string DisplayLabel()
    {
        if (IsApartment)
        {
            // "Apartment W8 R29". Short form on purpose: it is not a plot, and dressing it
            // up as one ("Ward 8 Plot ...") would be inventing a place.
            var division = ApartmentDivision > 0 ? $" div {ApartmentDivision}" : "";
            return $"Apartment W{Ward + 1} R{Room}{division}";
        }

        var label = $"Ward {Ward + 1} Plot {Plot + 1}";
        return Room > 0 ? $"{label} Room {Room}" : label;
    }

    /// <summary>Binding namespace. Pots and outdoor patches share one estate key now, and
    /// their map keys come from two different DataMaps that can hand out the same number
    /// (the 08-13 probe saw key=2 outdoors), so the pot space is named apart.</summary>
    public string BindingKey(int patchOrdinal, bool isPot = false)
        => $"{TerritoryId}:{Ward}:{Plot}:{Room}#{(isPot ? "pot" : "")}{patchOrdinal}";
}
