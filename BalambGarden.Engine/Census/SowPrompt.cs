using System.Text.RegularExpressions;

namespace BalambGarden.Engine.Census;

/// <summary>The two item names a sow confirmation names, exactly as the game wrote them.</summary>
public sealed record SowPromptParts(string Soil, string Seed);

/// <summary>Verdict on a sow confirmation: whether the dialog describes the plant we
/// planned. <see cref="Reason"/> is the honest sentence for the run log when it does not.</summary>
public sealed record SowCheck(bool Ok, string? Reason, SowPromptParts? Parts);

/// <summary>
/// Reads the sow confirmation prompt. This is the ONLY surface that names what is about
/// to be planted: the picker addon (<c>HousingGardening</c>) exposes zero AtkValues
/// (capture 2026-08-15: "--- HousingGardening (0 values) ---"), because the player fills
/// its two slots from inventory. The prompt is generated from the filled slots, so
/// parsing it is how the chain learns what the human actually put in.
///
/// <para>The game calls a flowerpot a "bed" here too (capture F3) - nothing in this text
/// distinguishes pot from outdoor bed, and this parser never tries to.</para>
/// </summary>
public static partial class SowPrompt
{
    // capture 2026-08-15: SelectYesno AtkValues[0] =
    //   'Prepare the bed with a bag of potting soil and a bag of daisy seeds?'
    [GeneratedRegex(
        @"^Prepare the bed with a bag of (.+?) and a bag of (.+?) seeds\?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex Prompt();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Null when the text is not a sow confirmation at all.</summary>
    public static SowPromptParts? Parse(string prompt)
    {
        var m = Prompt().Match(Flatten(prompt));
        return m.Success
            ? new SowPromptParts(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim())
            : null;
    }

    /// <summary>
    /// Does the prompt describe what we planned? A null expectation is not a claim -
    /// the human chose that slot and the parse is simply reported back (pot planting has
    /// no plan-side seed, and flowerpot flowers are absent from the crop table entirely).
    /// </summary>
    public static SowCheck Check(string prompt, string? expectedSoil, string? expectedSeed)
    {
        if (Parse(prompt) is not { } parts)
            return new SowCheck(false, $"unrecognized sow prompt: '{Flatten(prompt)}'", null);

        if (expectedSoil is { Length: > 0 } && !NamesMatch(parts.Soil, expectedSoil))
            return new SowCheck(
                false, $"planted item mismatch: expected {expectedSoil}, dialog says {parts.Soil}", parts);

        if (expectedSeed is { Length: > 0 } && !NamesMatch(parts.Seed, expectedSeed))
            return new SowCheck(
                false, $"planted item mismatch: expected {expectedSeed}, dialog says {parts.Seed}", parts);

        return new SowCheck(true, null, parts);
    }

    /// <summary>Item names in the prompt are lower-cased prose and the regex already ate
    /// the trailing " seeds", while the tables hold title-case item names ("Daisy Seeds").
    /// Compare on the shared part, case-insensitively; anything else would fail a match
    /// that is plainly correct to a human reading the box.</summary>
    private static bool NamesMatch(string fromPrompt, string expected)
        => Normalize(fromPrompt) == Normalize(expected);

    private static string Normalize(string name)
    {
        var flat = Flatten(name).ToLowerInvariant();
        if (flat.EndsWith(" seeds", StringComparison.Ordinal))
            flat = flat[..^6].Trim();
        else if (flat.EndsWith(" seed", StringComparison.Ordinal))
            flat = flat[..^5].Trim();
        return flat;
    }

    /// <summary>Dialogue text arrives with newlines; comparison and matching want one line.</summary>
    private static string Flatten(string text)
        => Whitespace().Replace(text.Replace('\n', ' ').Replace('\r', ' '), " ").Trim();
}
