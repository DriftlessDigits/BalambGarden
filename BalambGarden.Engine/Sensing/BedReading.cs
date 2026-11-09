namespace BalambGarden.Engine.Sensing;

/// <summary>One decoded map slot. Extra = raw byte 3, preserved un-interpreted
/// (indoor pigment suspect, hypothesis unbound as of 2026-08-13). Wilt = a pot's raw byte 4
/// (<see cref="PotReading.Wilt"/>), handed on so the census can record a wilt sighting; beds
/// publish no wilt, so theirs is always 0.</summary>
public sealed record BedReading(int Slot, ushort SpeciesIndex, byte Stage, byte Extra, bool Occupied, byte Wilt = 0);
