using System.Text.Json;

namespace BalambGarden.Engine.Census;

/// <summary>The access roster as last read in game, on disk: <c>access-roster.json</c> in the
/// plugin config directory. Saved by the census pump from the access-roster sensor's last read,
/// and only when that read differs from what is saved, so <see cref="ReadAt"/> is when the list
/// last changed, not when it was last seen. The plugin never reads it back in game (the game
/// answers there); it exists for readers outside the game, which cannot ask the game who may
/// enter where.</summary>
public sealed class SavedAccessRoster
{
    public int Version { get; init; } = 1;

    /// <summary>When the access-roster read that produced this list happened.</summary>
    public DateTimeOffset ReadAt { get; init; }

    /// <summary>The estates, in the order the read listed them.</summary>
    public List<RosterEstate> Estates { get; init; } = [];

    public static SavedAccessRoster Of(AccessRoster roster, DateTimeOffset readAt)
        => new() { ReadAt = readAt, Estates = [.. roster.Estates] };

    public AccessRoster ToRoster() => new(Estates);

    /// <summary>Whether an access-roster read must be saved: it lists at least one estate, and
    /// nothing is saved yet or the list differs from the saved one (an estate added or dropped,
    /// or the same estates in another order: the file mirrors the game's list as read, ruled
    /// 2026-11-07). An empty read is never saved: the teleport list settles a beat late, and an
    /// empty read racing it would blank every reader outside the game until the next change.</summary>
    public static bool NeedsSaving(SavedAccessRoster? saved, AccessRoster read)
        => read.Estates.Count > 0
           && (saved is null || !saved.Estates.SequenceEqual(read.Estates));

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Throws on a file that is not a saved access roster, including one with no read
    /// time (a stamp is what makes it a read).</summary>
    public static SavedAccessRoster FromJson(string json)
    {
        var saved = JsonSerializer.Deserialize<SavedAccessRoster>(json, Options)
                    ?? throw new InvalidOperationException("Access roster JSON deserialized to null");
        if (saved.ReadAt == default)
            throw new InvalidOperationException("Access roster JSON carries no read time");
        return saved;
    }
}
