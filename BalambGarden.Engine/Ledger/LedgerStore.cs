using System.Text.Json;
using System.Text.Json.Serialization;

namespace BalambGarden.Engine.Ledger;

/// <summary>The persisted census: claimed beds + receipt-bound patch->key bindings.
/// Fresh file in v0.2; the POC ledger is never read.</summary>
public sealed class LedgerStore
{
    public int Version { get; set; } = 2;
    public List<ClaimedBed> Beds { get; set; } = [];
    public Dictionary<string, int> Bindings { get; set; } = [];
    public List<EstateRecord> Estates { get; set; } = [];

    /// <summary>Frame 2: visiting an estate is how it enters the roster; revisits only
    /// move LastVisited, so FirstSeen stays the discovery receipt.</summary>
    public EstateRecord UpsertEstate(Census.EstateKey key, DateTimeOffset now)
    {
        var record = Estates.FirstOrDefault(e => e.Key == key);
        if (record is null)
        {
            record = new EstateRecord { Key = key, FirstSeen = now, LastVisited = now };
            Estates.Add(record);
        }
        else
        {
            record.LastVisited = now;
        }
        return record;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static LedgerStore FromJson(string json)
        => JsonSerializer.Deserialize<LedgerStore>(json, Options)
           ?? throw new InvalidOperationException("Ledger JSON deserialized to null");
}
