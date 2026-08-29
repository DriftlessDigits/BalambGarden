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
/// <para><see cref="Seq"/> is the counter half of the wire id "user:era:counter" (the identity
/// amendment, ruled 2026-08-23; gap detection rides the sequence), assigned by
/// <see cref="ReceiptLog.Append"/> and never by a caller. <see cref="Era"/> is the middle
/// third, stamped from the log's era at Append time so an entry carries the life it was born
/// under forever: that is what lets <see cref="ReceiptLog.Unsent"/> tell this era's outbox from
/// a retired era's history. The user third never touches the Engine - it lives in config and
/// arrives as a parameter at send time.</para>
///
/// <para><see cref="HouseId"/> is the raw 8-byte HouseId, banked at ACT time (ruled
/// 2026-08-29, option A): the wire canonical hashes the game's own identity, and EstateKey is
/// a lossy decomposition that cannot give those bytes back. 0 = unknown (a pre-upgrade entry,
/// or a read that never answered) - the send path refuses it, the same law as patch id 0.
/// LOCAL-PLAINTEXT ONLY like everything here; on the wire it exists solely inside the entity
/// HMAC.</para></summary>
public sealed record LoggedReceipt(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("era")] string Era,
    [property: JsonPropertyName("kind")] SyncKind Kind,
    [property: JsonPropertyName("source")] SyncSource Source,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("estate")] EstateKey Estate,
    [property: JsonPropertyName("isPot")] bool IsPot,
    [property: JsonPropertyName("patchOrdinal")] int PatchOrdinal,
    [property: JsonPropertyName("bedSlot")] int BedSlot,
    [property: JsonPropertyName("patchId")] ushort PatchId,
    [property: JsonPropertyName("potKey")] int PotKey,
    [property: JsonPropertyName("data")] SyncData Data,
    [property: JsonPropertyName("houseId")] ulong HouseId = 0);

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

    /// <summary>The name of the counter's current life, empty until first minted. Lives HERE,
    /// beside the counter it protects, because the two only mean anything together: an era
    /// names the number-space a Seq was drawn from. The user third of a wire id is deliberately
    /// absent - it lives in plugin config and arrives as a parameter at send time, so the
    /// Engine never stores who anyone is.</summary>
    public string Era { get; set; } = "";

    /// <summary>Last <see cref="LoggedReceipt.Seq"/> handed out. Persisted so a restart never
    /// re-issues a number under the same <see cref="Era"/>.</summary>
    public long Counter { get; set; }

    /// <summary>Outbox high-water mark: everything at or below this Seq has been sent.
    /// 0 = nothing sent.</summary>
    public long LastSentSeq { get; set; }

    /// <summary>INBOX position: the relay's opaque cursor from the last page this install
    /// applied; "0" = never read, full replay of the retained window. Deliberately NOT
    /// touched by <see cref="MintEra"/> - eras name the OUTBOX's id-space, and where we have
    /// read to in the household stream is a different fact entirely (re-reading after every
    /// restore-triggered mint would replay foreign receipts already applied).</summary>
    public string RelayCursor { get; set; } = "0";

    public List<LoggedReceipt> Entries { get; set; } = [];

    /// <summary>Assigns the next <see cref="Seq"/>, stamps the current <see cref="Era"/>, and
    /// appends. The only way an entry is born.
    ///
    /// <para>Throws when no era has been minted: a receipt with no era has no id-space to be
    /// unique within, and an id-less fact would either block the outbox or ship under whatever
    /// era happened to be minted later - both worse than refusing. The id-space is not open
    /// until <see cref="MintEra"/> opens it.</para></summary>
    /// <exception cref="InvalidOperationException"><see cref="Era"/> is empty.</exception>
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
        SyncData? data = null,
        ulong houseId = 0)
    {
        if (Era.Length == 0)
            throw new InvalidOperationException(
                "No receipts before an era exists - the id-space is not open yet. Call MintEra first.");

        var entry = new LoggedReceipt(
            Seq: ++Counter,
            Era: Era,
            Kind: kind,
            Source: source,
            At: at,
            Estate: estate,
            IsPot: isPot,
            PatchOrdinal: patchOrdinal,
            BedSlot: bedSlot,
            PatchId: patchId,
            PotKey: potKey,
            Data: data ?? SyncData.Empty,
            HouseId: houseId);
        Entries.Add(entry);
        return entry;
    }

    /// <summary>Opens a fresh id-space: names a new era and zeroes the counter under it.
    /// Zeroing is safe ONLY because the era changes with it - the triple "user:era:counter"
    /// stays unique forever. <see cref="LastSentSeq"/> zeroes too, since the relay has never
    /// heard a number from this era.
    ///
    /// <para>ERAS ARE CHEAP, COLLISIONS ARE NOT. A spurious mint costs one slug and a counter
    /// restart; a missed mint hands the relay two different facts under one id. Every trigger
    /// check (see <c>EraGuard</c>) therefore fails toward minting.</para>
    ///
    /// <para>Entries survive deliberately: their history value ("fed at 3am") is local and
    /// still true, and <see cref="LastFertilizedAt"/> keeps answering from them. They keep the
    /// era they were stamped with, so they are history and nothing else - <see cref="Unsent"/>
    /// speaks only for the current era, and a retired era's entries never re-ship even though
    /// their Seq numbers now sit above a zeroed <see cref="LastSentSeq"/>.</para></summary>
    public void MintEra(string era)
    {
        // The boundary check lives here, not at Append: an era is either a minted slug or
        // absent, and a blank or colon-bearing one would corrupt every id in its life.
        if (string.IsNullOrWhiteSpace(era) || era.Contains(':'))
            throw new ArgumentException($"Not a mintable era slug: '{era}'", nameof(era));

        Era = era;
        Counter = 0;
        LastSentSeq = 0;
    }

    /// <summary>When this exact entity was last fertilized, or null if it never was. Identity
    /// is pot-ness first: a pot and a bed carrying the same numbers are different gardens
    /// (EstateKey's binding namespaces say so) and must never answer for each other.
    ///
    /// <para>Era-BLIND on purpose: history is history whichever era minted it.</para></summary>
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

    /// <summary>The outbox: everything the relay has not been told about, in Seq order.
    ///
    /// <para>Scoped to the CURRENT era. A retired era's entries are history, never cargo: their
    /// Seq numbers belong to a number-space this log no longer counts in, so comparing them
    /// against a zeroed <see cref="LastSentSeq"/> would re-ship facts the relay already holds
    /// under a second identity. Era scoping is the whole answer - no retired-space bookkeeping
    /// needed.</para></summary>
    public IReadOnlyList<LoggedReceipt> Unsent()
        => Entries.Where(e => e.Era == Era && e.Seq > LastSentSeq).OrderBy(e => e.Seq).ToList();

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
