using System.Text.Json;
using System.Text.Json.Serialization;

namespace BalambGarden.Engine.Ledger;

/// <summary>The persisted census: claimed beds + receipt-bound patch->key bindings.
/// Fresh file in v0.2; the POC ledger is never read. Version 3 (2026-10-14): rows carry a
/// tenancy record instead of an observation ring - a v2 file loads through
/// <see cref="LedgerMigration.FoldLegacyRings"/> once and is backed up beside itself first.</summary>
public sealed class LedgerStore
{
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;
    public List<ClaimedBed> Beds { get; set; } = [];
    public Dictionary<string, int> Bindings { get; set; } = [];
    public List<EstateRecord> Estates { get; set; } = [];

    /// <summary>INBOX position in the household stream (sync ruling 9: the cursor is a claim
    /// about what THIS FILE absorbed, so it rides this file - stored anywhere else it would
    /// point past receipts a restored ledger never saw). "0" = full replay of the relay's
    /// retained window.</summary>
    public string RelayCursor { get; set; } = "0";

    /// <summary>Foreign receipts that matched no local entity yet - the estate has not been
    /// censused under this phrase, or the entity is genuinely orphaned. They wait HERE, beside
    /// the cursor, for the same restore-coherence reason, and are retried whenever census
    /// mints new entity ids. The stream never dams on them (piece 4 ruling, 2026-08-29).</summary>
    public List<Sync.ParkedForeign> ForeignHold { get; set; } = [];

    /// <summary>The phrase-change invariant (open-household ruling 2026-08-29; hoisted here
    /// from the config UI at the QA pass): a new phrase is a NEW HOUSEHOLD, and everything
    /// scoped to the old one leaves TOGETHER - the entity cache (minted under the old naming
    /// key, can never match again), the parked receipts (came from the old stream), the
    /// cursor (points into it), and - ruled 2026-10-14 - WHOSE hands planted and watered
    /// (slugs from the old household mean nothing in the new one). The timestamps themselves
    /// stay: they are facts about the garden, not about the household. The plugin's alias map
    /// is cleared by the same caller. Any future household-scoped field joins this method.</summary>
    public void ResetHousehold()
    {
        foreach (var bed in Beds)
        {
            bed.WireEntity = null;
            bed.PlantedBy = null;
            bed.LastTendedBy = null;
        }
        ForeignHold.Clear();
        RelayCursor = "0";
    }

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
