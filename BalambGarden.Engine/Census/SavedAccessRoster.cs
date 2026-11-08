using System.Text.Json;

namespace BalambGarden.Engine.Census;

/// <summary>The access roster as last read in game, on disk: <c>access-roster.json</c> in the
/// plugin config directory. Written only by the roster reader, and only when a fresh read
/// differs from what is saved, so <see cref="ReadAt"/> is when the list last changed, not when
/// it was last seen. The plugin never reads it back in game (the game answers there); it exists
/// for readers outside the game, which cannot ask the game who may enter where.</summary>
public sealed class SavedAccessRoster
{
    public int Version { get; init; } = 1;

    /// <summary>When the read that produced this list happened.</summary>
    public DateTimeOffset ReadAt { get; init; }

    /// <summary>The estates, in the order the read listed them.</summary>
    public List<RosterEstate> Estates { get; init; } = [];

    public static SavedAccessRoster Of(AccessRoster roster, DateTimeOffset readAt)
        => new() { ReadAt = readAt, Estates = [.. roster.Estates] };

    public AccessRoster ToRoster() => new(Estates);

    /// <summary>Whether a fresh read must be written: nothing is saved yet, or the list differs
    /// from the saved one (an estate added or dropped, or the same estates in another order:
    /// the file mirrors the game's list as read, ruled 2026-11-07).</summary>
    public static bool NeedsSaving(SavedAccessRoster? saved, AccessRoster fresh)
        => saved is null || !saved.Estates.SequenceEqual(fresh.Estates);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static SavedAccessRoster FromJson(string json)
        => JsonSerializer.Deserialize<SavedAccessRoster>(json, Options)
           ?? throw new InvalidOperationException("Access roster JSON deserialized to null");
}
