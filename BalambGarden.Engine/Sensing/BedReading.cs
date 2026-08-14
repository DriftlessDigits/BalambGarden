namespace BalambGarden.Engine.Sensing;

/// <summary>One decoded map slot. Extra = raw byte 3, preserved un-interpreted
/// (indoor pigment suspect, hypothesis unbound as of 2026-08-13).</summary>
public sealed record BedReading(int Slot, ushort SpeciesIndex, byte Stage, byte Extra, bool Occupied);
