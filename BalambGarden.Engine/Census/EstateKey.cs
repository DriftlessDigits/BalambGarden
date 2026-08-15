namespace BalambGarden.Engine.Census;

/// <summary>Estate identity = the PHYSICAL PLOT: (exterior district territory, ward, plot).
/// Ward/Plot/Room stored RAW 0-based (HousingManager values); +1 happens ONLY in display
/// helpers. Room = -1 for houses, indoors and out - a house is one estate whether you are
/// in the yard or the living room (08-15 fix: the interior territory id is a different
/// number AND a shared template across districts, so it can never carry identity).
/// Apartments use Plot as building and Room as the apartment number - unsupported today,
/// the sensor fails closed rather than minting one.</summary>
public sealed record EstateKey(ushort TerritoryId, int Ward, int Plot, int Room = -1)
{
    public string DisplayWardPlot() => $"Ward {Ward + 1} Plot {Plot + 1}";

    /// <summary>Binding namespace. Pots and outdoor patches share one estate key now, and
    /// their map keys come from two different DataMaps that can hand out the same number
    /// (the 08-13 probe saw key=2 outdoors), so the pot space is named apart.</summary>
    public string BindingKey(int patchOrdinal, bool isPot = false)
        => $"{TerritoryId}:{Ward}:{Plot}:{Room}#{(isPot ? "pot" : "")}{patchOrdinal}";
}
