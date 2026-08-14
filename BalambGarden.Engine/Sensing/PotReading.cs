namespace BalambGarden.Engine.Sensing;

/// <summary>One decoded indoor pot. Recognized=false -> species newer than our index:
/// track it, display "Unknown (0xNN)", never guess.</summary>
public sealed record PotReading(ushort SpeciesIndex, byte Stage, byte Extra, bool Occupied, bool Recognized);
