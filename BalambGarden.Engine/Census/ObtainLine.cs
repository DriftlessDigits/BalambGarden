using System.Text.RegularExpressions;

namespace BalambGarden.Engine.Census;

/// <summary>
/// The harvest completion signal. Harvesting fires on the menu selection itself - no
/// yes/no, no closing Talk (capture 2026-08-15 F4: "no SelectYesno and no completion
/// Talk after 'Harvest Crop'") - so the only thing the game says when the crop is
/// actually in the bag is the chat obtain line: "You obtain a bouquet of red sunflowers."
///
/// <para>TIMING AND QUANTITY, NOT IDENTITY. The obtained item ("bouquet of red sunflowers") is
/// not the species ("Red Sunflowers"); the plant's name comes from the harvest Talk header or
/// the ledger, and the item's id rides the line's ItemPayload (capture 2026-08-26). Nothing
/// here is ever matched against a species name. The COUNT is kept (Q3, ruled 2026-10-14:
/// track harvest amounts) - it is the quantity half of the wire's <c>harvest.yields</c>.</para>
/// </summary>
public static partial class ObtainLine
{
    // Live receipt (Drift, 2026-08-15): "You obtain a bouquet of red sunflowers."
    // Quantities and articles vary ("You obtain 3 kukuru beans."), the stem does not.
    [GeneratedRegex(@"^You obtain (?:(?<n>\d+)\s+|an?\s+|the\s+)?(?<item>.+?)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex Obtain();

    /// <summary>The obtained item's text and count, or null when the line is not an obtain
    /// line. An article ("a bouquet", "the thing") or no leading word at all counts as 1.</summary>
    public static (string Item, int Qty)? Parse(string line)
    {
        var m = Obtain().Match(line.Trim());
        if (!m.Success)
            return null;
        var qty = m.Groups["n"].Success && int.TryParse(m.Groups["n"].Value, out var n) && n > 0 ? n : 1;
        return (m.Groups["item"].Value.Trim(), qty);
    }

    /// <summary>The obtained item's text, or null when the line is not an obtain line.
    /// Useful for the run log only - never for identifying a species.</summary>
    public static string? Item(string line) => Parse(line)?.Item;

    /// <summary>The count the line carried, or null when the line is not an obtain line.</summary>
    public static int? Quantity(string line) => Parse(line)?.Qty;

    public static bool IsObtain(string line) => Parse(line) is not null;
}
