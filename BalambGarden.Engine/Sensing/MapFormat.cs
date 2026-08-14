namespace BalambGarden.Engine.Sensing;

/// <summary>Pure decoders for the gardening DataMap entry formats (receipt-verified 08-12/08-13).
/// Outdoor: 48 bytes = 8 beds x 6-byte stride [species u16 LE][stage][b3][b4][b5].
/// Bytes 4-5 carry allocator junk (02/CD columns) - never read them.</summary>
public static class MapFormat
{
    public const int OutdoorEntrySize = 48;
    public const int Stride = 6;
    public const int BedsPerPatch = 8;

    public static IReadOnlyList<BedReading> DecodeOutdoorEntry(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != OutdoorEntrySize)
            throw new ArgumentException($"Outdoor entry must be {OutdoorEntrySize} bytes, got {bytes.Length}");

        var beds = new List<BedReading>(BedsPerPatch);
        for (var slot = 0; slot < BedsPerPatch; slot++)
        {
            var off = slot * Stride;
            var species = (ushort)(bytes[off] | (bytes[off + 1] << 8));
            beds.Add(new BedReading(slot, species, bytes[off + 2], bytes[off + 3], species != 0));
        }
        return beds;
    }

    public static bool LooksEmpty(ReadOnlySpan<byte> bytes)
        => DecodeOutdoorEntry(bytes).All(b => !b.Occupied);

    /// <summary>Indoor entries share the 48-byte block but a pot uses only sub-entry 0.
    /// Entries with data in sub-entries 1+ are other furniture (aquariums etc.) - rejected.
    /// Trailing 7F/01/44 columns are the indoor junk pattern - never read.</summary>
    public static PotReading? DecodeIndoorEntry(ReadOnlySpan<byte> bytes, Func<ushort, bool> knownSpecies)
    {
        if (bytes.Length != OutdoorEntrySize)
            throw new ArgumentException($"Indoor entry must be {OutdoorEntrySize} bytes, got {bytes.Length}");

        // Sub-entries 1..3 must be empty for a single-plant pot (offsets 6/12/18; junk lives past 28).
        for (var sub = 1; sub <= 3; sub++)
        {
            var off = sub * Stride;
            if ((ushort)(bytes[off] | (bytes[off + 1] << 8)) != 0)
                return null;
        }

        var species = (ushort)(bytes[0] | (bytes[1] << 8));
        if (species == 0)
            return new PotReading(0, 0, 0, Occupied: false, Recognized: false);

        return new PotReading(species, bytes[2], bytes[3], Occupied: true, Recognized: knownSpecies(species));
    }
}
