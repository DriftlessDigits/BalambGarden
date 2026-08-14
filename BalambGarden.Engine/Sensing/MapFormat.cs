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
}
