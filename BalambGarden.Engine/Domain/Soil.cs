namespace BalambGarden.Engine.Domain;

/// <summary>
/// A gardening topsoil. Grade is the number in the name (1-3); it is the knob that
/// shifts crossbreed odds, so the plant chain picks by grade, not by display string.
/// </summary>
public sealed record Soil(uint ItemId, string Name, int Grade);
