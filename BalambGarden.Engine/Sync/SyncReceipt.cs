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
/// but derivations never branch on it: a receipt is a receipt (ruling 10).
///
/// <para>ChainUnconfirmed is the receipt lifecycle's honest fourth word (ratified 2026-08-23,
/// schema amended the same day so the word lands before the relay ships): the chain acted, the
/// game's confirming line never arrived inside the window, and we are telling the history that
/// instead of calling a belief a confirmation. Persisted BY NAME (JsonStringEnumConverter on every
/// store and the wire), so renaming a member is the hazard, not reordering (corrected 2026-10-14).</para></summary>
public enum SyncSource { Chain, Chat, Sighting, ChainUnconfirmed }

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
    [property: JsonPropertyName("yields")] IReadOnlyList<SyncYield>? Yields = null,
    // Harvest only (ruled 2026-10-18): the cross seed. qty 0 + no item = heard, missed; both
    // absent = not heard. Additive: the relay stores data verbatim and older installs ignore
    // fields they do not know. See HarvestGather.
    [property: JsonPropertyName("seedQty")] int? SeedQty = null,
    [property: JsonPropertyName("seedItem")] uint? SeedItem = null)
{
    public static readonly SyncData Empty = new();
}

/// <summary>The envelope - the receipt as it travels ([[Balamb Garden - Sync Receipt
/// Schema - Design]], ratified 2026-08-23). Append-only, identical whichever side minted
/// it, and identical whether it came from a chain, a chat line, or a map sighting.
/// Id is "user:era:counter" (identity amendment 2026-08-23 - gap detection rides the
/// sequence within an era; <see cref="SyncId"/> owns the format); Entity is
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
        Converters = { new JsonStringEnumConverter(WireWordPolicy.Instance) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, WireOptions);

    public static SyncReceipt? FromJson(string json)
        => JsonSerializer.Deserialize<SyncReceipt>(json, WireOptions);

    /// <summary>Enum names to wire words. Kebab-case by default - PascalCase split into
    /// lowercase words joined by '-' - which leaves every single-word name (chain, chat,
    /// sighting, tend, plant, harvest, bed, pot) byte-identical to what shipped, and turns
    /// <see cref="SyncSource.ChainUnconfirmed"/> into "chain-unconfirmed" exactly as the schema
    /// amendment of 2026-08-23 requires.
    ///
    /// <para>PINNED WORDS COME FIRST, and they are the reason this is a policy rather than a
    /// one-line rule. "potwater" and "potwilt" were RATIFIED as single words by the schema doc
    /// before kebab-case existed, and the wire word is ratified text, not derived text - a
    /// naming rule adopted afterward does not get to rename facts already agreed with the
    /// relay. Deriving them would silently ship "pot-water" and split the vocabulary in two.
    /// The pin list is therefore closed and historical: new multi-word names take kebab.</para></summary>
    private sealed class WireWordPolicy : JsonNamingPolicy
    {
        public static readonly WireWordPolicy Instance = new();

        private static readonly Dictionary<string, string> Ratified = new()
        {
            [nameof(SyncKind.PotWater)] = "potwater",
            [nameof(SyncKind.PotWilt)] = "potwilt",
        };

        public override string ConvertName(string name)
        {
            if (Ratified.TryGetValue(name, out var pinned))
                return pinned;

            var wire = new System.Text.StringBuilder(name.Length + 2);
            for (var i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]))
                    wire.Append('-');
                wire.Append(char.ToLowerInvariant(name[i]));
            }
            return wire.ToString();
        }
    }
}
