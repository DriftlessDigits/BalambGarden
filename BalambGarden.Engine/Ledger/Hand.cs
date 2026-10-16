using System.Text.Json;
using System.Text.Json.Serialization;

namespace BalambGarden.Engine.Ledger;

/// <summary>Whose hands did a thing: this install ("own") or a household install, named by
/// its WIRE SLUG and nothing else. The slug is the random id every receipt already carries
/// (<see cref="Sync.SyncId.NewSlug"/>) - derived from nothing, so it says only "the same source
/// as that other one" and never "this person". No name, character, or account exists anywhere
/// in the Engine or its files (sync ruling 10, reaffirmed 2026-10-14: "no hard coded, player, or
/// personal names. period."). A LOCAL alias for a slug, if the player types one, lives in plugin
/// config only and never serializes onto the wire, into the ledger, or into the history.</summary>
[JsonConverter(typeof(HandJsonConverter))]
public readonly record struct Hand(string Slug)
{
    public const string OwnSlug = "own";
    public static readonly Hand Own = new(OwnSlug);

    [JsonIgnore] public bool IsOwn => Slug == OwnSlug;

    public override string ToString() => Slug;
}

/// <summary>Writes a hand as its bare slug ("PlantedBy": "own") - the file stays readable and
/// the slug stays the only thing stored.</summary>
public sealed class HandJsonConverter : JsonConverter<Hand>
{
    public override Hand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new Hand(reader.GetString() ?? Hand.OwnSlug);

        // Tolerate the object shape ({"Slug": "..."}) in case a file was ever written without
        // this converter; nothing else is accepted.
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            string? slug = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.GetString() == nameof(Hand.Slug))
                {
                    reader.Read();
                    slug = reader.GetString();
                }
                else
                {
                    reader.Skip();
                }
            }
            return new Hand(slug ?? Hand.OwnSlug);
        }

        throw new JsonException("A Hand is a string slug.");
    }

    public override void Write(Utf8JsonWriter writer, Hand value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Slug);
}

/// <summary>First and last instant a bed was SEEN at one stage, for the current planting.
/// Bounded (one per stage), idempotent (min/max), never evicted - the ring this replaces held
/// about one minute of per-read sightings and evicted every anchor (data inventory 2026-10-14).</summary>
public sealed record StageBracket(DateTimeOffset FirstSeen, DateTimeOffset LastSeen)
{
    public StageBracket Fold(DateTimeOffset at)
        => new(at < FirstSeen ? at : FirstSeen, at > LastSeen ? at : LastSeen);
}

/// <summary>Why a row's planting was reset - every rebase names one (ruled 2026-10-14: rebase is
/// reasoned, logged, time-aware). The reason is persisted on the row with the fence instant.</summary>
public enum RebaseReason
{
    /// <summary>A harvest receipt, either hand - the game's own line said the crop left.</summary>
    HarvestWitnessed,
    /// <summary>A live read found the slot empty while the row still had a planting.</summary>
    ReadsEmpty,
    /// <summary>Live or wire evidence contradicted the species of record (or regressed its stage).</summary>
    NewPlantingSighted,
    /// <summary>A recorded pot's key vanished from a settled indoor read - what a harvest looks like.</summary>
    PotAbsent,
    /// <summary>A plant receipt - a sowing at a known instant starts a new planting there.</summary>
    PlantReceipt,
}

/// <summary>One rebase as it happened, handed to the caller so the plugin can log it at the right
/// level and append it to the trail. <see cref="RemovedAnything"/> false = a bare row was
/// (re)planted, which is not noise worth an INF line.</summary>
public sealed record RebaseEvent(
    ClaimedBed Bed, RebaseReason Reason, DateTimeOffset Cut, Hand? By, bool RemovedAnything);

/// <summary>What <see cref="ClaimedBed.Absorb"/> did with a wire sighting - the caller counts the
/// contradictions, because a late receipt that disagrees with the ledger is replant evidence
/// worth a log line even though it changes nothing.</summary>
public enum AbsorbOutcome
{
    /// <summary>At or before the fence - the plant it describes already left.</summary>
    Fenced,
    /// <summary>Older than the newest evidence and consistent with it - stage bracket tightened.</summary>
    FoldedOlder,
    /// <summary>Older than the newest evidence and inconsistent with it - history only, ledger untouched.</summary>
    Contradicted,
    /// <summary>Newer than everything known - folded, Latest moved forward (after a rebase if it contradicted).</summary>
    Applied,
}
