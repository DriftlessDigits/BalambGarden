using System.Security.Cryptography;

namespace BalambGarden.Engine.Sync;

/// <summary>The wire id: "user:era:counter" (identity amendment, ruled 2026-08-23). USER marks
/// mine-vs-external forever and lives in plugin config; ERA names the counter's current life
/// and lives in the receipt log; COUNTER is monotonic within its era. Three parts, one colon
/// between each - which is exactly why a slug may not contain a colon, and why
/// <see cref="Format"/> refuses one at the boundary rather than minting an id that parses back
/// into different pieces than it was built from.</summary>
public static class SyncId
{
    /// <summary>Slug alphabet: lowercase, no vowel-adjacent ambiguity, no character pair a
    /// human reads wrong off a screen (no i/l/1, no o/0, no u). 30 symbols.</summary>
    private const string Alphabet = "abcdefghjkmnpqrstvwxyz23456789";

    /// <summary>Six symbols from a 30-character alphabet - about 29.5 bits, drawn from a
    /// cryptographic source.</summary>
    private const int SlugLength = 6;

    /// <summary>Mints a slug for a user or an era. The value is derived from NOTHING - not a
    /// character name, not a content id, not a machine fingerprint, not a clock. That is a
    /// PRIVACY PROPERTY, not a convenience: a derived slug would let anyone holding the input
    /// confirm the identity, and a slug travels on every receipt. Random means the id says only
    /// "the same source as that other one" and never "this source".</summary>
    public static string NewSlug()
    {
        var chars = new char[SlugLength];
        for (var i = 0; i < SlugLength; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>Builds the wire id.</summary>
    /// <exception cref="ArgumentException">A slug is empty or contains ':'.</exception>
    public static string Format(string user, string era, long counter)
    {
        Require(user, nameof(user));
        Require(era, nameof(era));
        return $"{user}:{era}:{counter}";

        static void Require(string part, string name)
        {
            if (string.IsNullOrEmpty(part))
                throw new ArgumentException("A wire id slug may not be empty.", name);
            if (part.Contains(':'))
                throw new ArgumentException(
                    "A wire id slug may not contain ':' - it is the id's own separator.", name);
        }
    }

    /// <summary>Reads a wire id back. False for anything that is not exactly three non-empty
    /// parts with a counter of 1 or more: counters start at 1 (the first <c>Append</c>
    /// pre-increments), so a 0 or negative counter is a malformed id, not an edge case.</summary>
    public static bool TryParse(string id, out string user, out string era, out long counter)
    {
        user = "";
        era = "";
        counter = 0;

        if (string.IsNullOrEmpty(id))
            return false;

        var parts = id.Split(':');
        if (parts.Length != 3)
            return false;
        if (parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
            return false;
        if (!long.TryParse(parts[2], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return false;
        if (parsed < 1)
            return false;

        user = parts[0];
        era = parts[1];
        counter = parsed;
        return true;
    }
}
