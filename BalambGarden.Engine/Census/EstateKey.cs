namespace BalambGarden.Engine.Census;

/// <summary>Estate identity. Ward/Plot/Room stored RAW 0-based (HousingManager values);
/// +1 happens ONLY in display helpers. Room = -1 for houses; apartments use Plot as
/// building and Room as the apartment number.</summary>
public sealed record EstateKey(ushort TerritoryId, int Ward, int Plot, int Room = -1)
{
    public string DisplayWardPlot() => $"Ward {Ward + 1} Plot {Plot + 1}";
    public string BindingKey(int patchOrdinal) => $"{TerritoryId}:{Ward}:{Plot}:{Room}#{patchOrdinal}";
}
