using System.Text.RegularExpressions;

namespace BalambGarden.Engine.Census;

/// <summary>
/// The harvest completion signal. Harvesting fires on the menu selection itself - no
/// yes/no, no closing Talk (capture 2026-08-15 F4: "no SelectYesno and no completion
/// Talk after 'Harvest Crop'") - so the only thing the game says when the crop is
/// actually in the bag is the chat obtain line: "You obtain a bouquet of red sunflowers."
///
/// <para>TIMING, NOT IDENTITY. The obtained item ("bouquet of red sunflowers") is not the
/// species ("Red Sunflowers"); the plant's name comes from the harvest Talk header or the
/// ledger. Nothing here is ever matched against a species name.</para>
/// </summary>
public static partial class ObtainLine
{
    // Live receipt (Drift, 2026-08-15): "You obtain a bouquet of red sunflowers."
    // Quantities and articles vary ("You obtain 3 kukuru beans."), the stem does not.
    [GeneratedRegex(@"^You obtain (?:an?\s+|the\s+|\d+\s+)?(.+?)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex Obtain();

    /// <summary>The obtained item's text, or null when the line is not an obtain line.
    /// Useful for the run log only - never for identifying a species.</summary>
    public static string? Item(string line)
    {
        var m = Obtain().Match(line.Trim());
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    public static bool IsObtain(string line) => Item(line) is not null;
}
