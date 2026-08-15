using System.Text.RegularExpressions;

namespace BalambGarden.Engine.Census;

/// <summary>Pure parsing of the game's receipt strings. "Nth Bed" is 1-based in
/// dialogue (verified 08-12); slots and ordinals are 0-based everywhere in code.</summary>
public static partial class ReceiptParser
{
    [GeneratedRegex(@"^(\d+)(?:st|nd|rd|th) Bed, (\d+)(?:st|nd|rd|th) Patch$", RegexOptions.IgnoreCase)]
    private static partial Regex BedHeader();

    public static (int BedSlot, int PatchOrdinal)? ParseBedHeader(string header)
    {
        var m = BedHeader().Match(header.Trim());
        if (!m.Success)
            return null;
        var bed = int.Parse(m.Groups[1].Value);
        var patch = int.Parse(m.Groups[2].Value);
        if (bed < 1 || patch < 1)
            return null;
        return (bed - 1, patch - 1);
    }
}
