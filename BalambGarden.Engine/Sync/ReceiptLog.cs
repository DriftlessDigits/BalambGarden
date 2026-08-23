using System.Text.Json;
using System.Text.Json.Serialization;
using BalambGarden.Engine.Census;

namespace BalambGarden.Engine.Sync;

/// <summary>One line of local history (design ruled 2026-08-23, Drift's shape: "the receipts
/// ARE the history"). Facts append; nobody edits, nobody reorders. Bed identity is carried
/// the way the ledger knows it (<see cref="PatchOrdinal"/> + <see cref="BedSlot"/>) AND the
/// way the wire canonical needs it (<see cref="PatchId"/>, <see cref="PotKey"/>), because an
/// entry minted today must still be sendable after a restart that lost the sighting context.
///
/// <para><see cref="Seq"/> is the counter half of the wire id "installId:counter" (sync
/// ruling 8 - gap detection rides the sequence), assigned by <see cref="ReceiptLog.Append"/>
/// and never by a caller.</para></summary>
public sealed record LoggedReceipt(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("kind")] SyncKind Kind,
    [property: JsonPropertyName("source")] SyncSource Source,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("estate")] EstateKey Estate,
    [property: JsonPropertyName("isPot")] bool IsPot,
    [property: JsonPropertyName("patchOrdinal")] int PatchOrdinal,
    [property: JsonPropertyName("bedSlot")] int BedSlot,
    [property: JsonPropertyName("patchId")] ushort PatchId,
    [property: JsonPropertyName("potKey")] int PotKey,
    [property: JsonPropertyName("data")] SyncData Data);

/// <summary>The local append-only receipt log. One structure, two duties: (a) local history -
/// "already fed within the hour" and future pacing analysis read it; (b) the sync outbox
/// source - unsent entries become wire <see cref="SyncReceipt"/>s at send time. The ledger
/// (ClaimedBed) keeps current state and the observation anchors; the history lives here, and
/// the two never duplicate each other's job.
///
/// <para>Append-only is a law, not a habit: no method on this type edits or removes an entry.
/// Corrections arrive as later facts.</para></summary>
public sealed class ReceiptLog
{
    public int Version { get; set; } = 1;

    /// <summary>This install's id-space, empty until minted. Half of every wire id.</summary>
    public string InstallId { get; set; } = "";

    /// <summary>Last <see cref="LoggedReceipt.Seq"/> handed out. Persisted so a restart never
    /// re-issues a number under the same <see cref="InstallId"/>.</summary>
    public long Counter { get; set; }

    /// <summary>Outbox high-water mark: everything at or below this Seq has been sent.
    /// 0 = nothing sent.</summary>
    public long LastSentSeq { get; set; }

    public List<LoggedReceipt> Entries { get; set; } = [];

    /// <summary>Assigns the next <see cref="Seq"/> and appends. The only way an entry is
    /// born.</summary>
    public LoggedReceipt Append(
        SyncKind kind,
        SyncSource source,
        DateTimeOffset at,
        EstateKey estate,
        bool isPot,
        int patchOrdinal,
        int bedSlot,
        ushort patchId,
        int potKey,
        SyncData? data = null)
    {
        var entry = new LoggedReceipt(
            Seq: ++Counter,
            Kind: kind,
            Source: source,
            At: at,
            Estate: estate,
            IsPot: isPot,
            PatchOrdinal: patchOrdinal,
            BedSlot: bedSlot,
            PatchId: patchId,
            PotKey: potKey,
            Data: data ?? SyncData.Empty);
        Entries.Add(entry);
        return entry;
    }

    /// <summary>Opens a fresh id-space (sync ruling 8's restore rule: a rewound counter must
    /// never reuse an id). Zeroing the counter is safe ONLY because the id changes with it -
    /// the pair "installId:counter" stays unique forever. <see cref="LastSentSeq"/> zeroes
    /// too, since the relay has never heard from this id.
    ///
    /// <para>Entries survive deliberately: their history value ("fed at 3am") is local and
    /// still true, and <see cref="LastFertilizedAt"/> keeps answering from them.</para>
    ///
    /// <para>KNOWN EDGE, unruled: the surviving entries carry Seq numbers from the retired
    /// id-space, so a rewound <see cref="LastSentSeq"/> makes <see cref="Unsent"/> offer them
    /// again - under the NEW id, which is a fresh number-space, so nothing duplicates an id
    /// the relay already holds (ruling 8 is satisfied); the relay would simply see the old
    /// facts a second time under a new identity. Fixing that needs a retired-space marker the
    /// design has not ruled on. Callers that must not re-send should mint before appending.</para></summary>
    public void MintInstallId(string id)
    {
        InstallId = id;
        Counter = 0;
        LastSentSeq = 0;
    }

    /// <summary>When this exact entity was last fertilized, or null if it never was. Identity
    /// is pot-ness first: a pot and a bed carrying the same numbers are different gardens
    /// (EstateKey's binding namespaces say so) and must never answer for each other.</summary>
    public DateTimeOffset? LastFertilizedAt(
        EstateKey estate, bool isPot, int potKey, int patchOrdinal, int bedSlot)
    {
        DateTimeOffset? latest = null;
        foreach (var e in Entries)
        {
            if (e.Kind != SyncKind.Fertilize || e.Estate != estate || e.IsPot != isPot)
                continue;
            if (isPot ? e.PotKey != potKey : e.PatchOrdinal != patchOrdinal || e.BedSlot != bedSlot)
                continue;
            if (latest is null || e.At > latest)
                latest = e.At;
        }
        return latest;
    }

    /// <summary>The outbox: everything the relay has not been told about, in Seq order.</summary>
    public IReadOnlyList<LoggedReceipt> Unsent()
        => Entries.Where(e => e.Seq > LastSentSeq).OrderBy(e => e.Seq).ToList();

    /// <summary>Raises the high-water mark. A lower value is ignored, never obeyed: a send
    /// that reports less progress than a previous one is a stale ack, and honoring it would
    /// re-send receipts the relay already holds.</summary>
    public void MarkSent(long throughSeq)
    {
        if (throughSeq > LastSentSeq)
            LastSentSeq = throughSeq;
    }

    // Persistence options, mirroring LedgerStore rather than SyncReceipt.WireOptions: this
    // file is a local store read only by us, so it takes the ledger's shape (indented for a
    // human opening it, plain enum names) instead of the byte-exact wire contract. The wire
    // format is minted at send time from these entries; the two need not match.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ReceiptLog FromJson(string json)
        => JsonSerializer.Deserialize<ReceiptLog>(json, Options)
           ?? throw new InvalidOperationException("Receipt log JSON deserialized to null");
}
