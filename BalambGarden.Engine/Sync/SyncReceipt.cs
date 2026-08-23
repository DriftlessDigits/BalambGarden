using System.Text.Json;
using System.Text.Json.Serialization;

namespace BalambGarden.Engine.Sync;

/// <summary>The ruled event vocabulary (sync design ruling 12 + schema ratification
/// 2026-08-23). Actions mirror the engine's receipt verbs 1:1 - PotWater stays its own
/// kind by ruling ("pots and beds seem like different mechanics at times"). Observations
/// (Stage/Ripe/PotWilt) send on CHANGE only; that discipline lives in the client, not
/// here. On the wire these are lowercase single words: "potwater", "potwilt".</summary>
public enum SyncKind { Plant, Harvest, Tend, Fertilize, PotWater, Stage, Ripe, PotWilt }

/// <summary>Where the receipt was born (schema field `source`). A Chain receipt is
/// "I did this to this bed" (armed identity); a Chat receipt is "somebody standing here
/// did this" (previous-target identity + species corroboration); a Sighting is a map
/// read. Travels on the wire deliberately - one honest word, keeps the relay debuggable -
/// but derivations never branch on it: a receipt is a receipt (ruling 10).</summary>
public enum SyncSource { Chain, Chat, Sighting }

/// <summary>Plaintext on the envelope (Drift's redline 2026-08-23): pot-ness is inside the
/// entity HMAC but opaque after it, and a receiving install must not need to have
/// censused an entity to classify its receipts. Reveals furniture category, never
/// location - the pseudonymity rulings guard WHOSE garden.</summary>
public enum SyncEntityType { Bed, Pot }

/// <summary>One harvest outcome line (schema: `harvest.yields[]`). RESERVED PENDING
/// RECEIPT - the field exists by ruling (pipeline tips need fed-vs-starved), but its
/// exact semantics wait on the harvest capture: whether obtain lines carry quantity,
/// whether crossbreed seeds print their own line, whether an ItemPayload id rides the
/// SeString. Item is the game item id when the line gave one.</summary>
public sealed record SyncYield(
    [property: JsonPropertyName("item")] uint Item,
    [property: JsonPropertyName("qty")] int Qty);

/// <summary>Per-kind payload, flat with nulls omitted on the wire. The relay never reads
/// past the envelope, so one flat shape serves every kind; which fields a kind fills is
/// the schema doc's table, and a field the sender did not know stays absent rather than
/// holding a guessed value.</summary>
public sealed record SyncData(
    [property: JsonPropertyName("species")] ushort? Species = null,
    [property: JsonPropertyName("soil")] uint? Soil = null,
    [property: JsonPropertyName("stage")] byte? Stage = null,
    [property: JsonPropertyName("wilt")] byte? Wilt = null,
    [property: JsonPropertyName("yields")] IReadOnlyList<SyncYield>? Yields = null)
{
    public static readonly SyncData Empty = new();
}

/// <summary>The envelope - the receipt as it travels ([[Balamb Garden - Sync Receipt
/// Schema - Design]], ratified 2026-08-23). Append-only, identical whichever side minted
/// it, and identical whether it came from a chain, a chat line, or a map sighting.
/// Id is "installId:counter" (ruling 8 - gap detection rides the sequence); Entity is
/// the HMAC pseudonym (<see cref="EntityName"/>); At is UTC, the moment the receipt
/// landed locally.</summary>
public sealed record SyncReceipt(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("entity")] string Entity,
    [property: JsonPropertyName("entityType")] SyncEntityType EntityType,
    [property: JsonPropertyName("kind")] SyncKind Kind,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("source")] SyncSource Source,
    [property: JsonPropertyName("data")] SyncData Data)
{
    /// <summary>Wire options, shared by every serializing surface so the client and any
    /// desk test speak byte-identical JSON: camelCase properties, lowercase enum words,
    /// nulls omitted. The relay stores the payload verbatim and never re-serializes.</summary>
    public static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(LowerCaseNamingPolicy.Instance) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, WireOptions);

    public static SyncReceipt? FromJson(string json)
        => JsonSerializer.Deserialize<SyncReceipt>(json, WireOptions);

    private sealed class LowerCaseNamingPolicy : JsonNamingPolicy
    {
        public static readonly LowerCaseNamingPolicy Instance = new();
        public override string ConvertName(string name) => name.ToLowerInvariant();
    }
}
