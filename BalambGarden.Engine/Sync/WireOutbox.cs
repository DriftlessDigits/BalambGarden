using System.Text.Json;

namespace BalambGarden.Engine.Sync;

/// <summary>One send attempt's worth of wire envelopes. <see cref="ThroughSeq"/> is what
/// <see cref="ReceiptLog.MarkSent"/> takes when the relay acks - the highest Seq actually
/// inside <see cref="Receipts"/>, never further. <see cref="Refusal"/> names the dam when
/// one exists; a full batch is not a dam (the remainder ships next send).</summary>
public sealed record WireBatch(
    IReadOnlyList<SyncReceipt> Receipts, long ThroughSeq, string? Refusal);

/// <summary>Send-time minting: unsent log entries become wire <see cref="SyncReceipt"/>s.
/// This is the one place entity pseudonyms are minted (the log stays local-plaintext) and
/// the one guard's home (schema ruling 2026-08-23 + option A 2026-08-29): an entry whose
/// HMAC input is incomplete - patch id 0 on a bed, house id 0 on anything - is REFUSED,
/// fail closed, no resolution machinery. A 0 in the canonical would mint a phantom entity
/// no other install can independently derive.
///
/// <para>The dam stops the batch AT the refused entry rather than skipping it, because the
/// outbox's high-water mark is a single number: sending past a skipped entry would mark it
/// delivered, and a silently dropped fact is worse than a loudly stuck one. Pre-upgrade
/// entries cannot reach this guard in practice - the client arc's first load mints a fresh
/// era, retiring them to history - so a refusal here is a live bug's signature, not
/// routine.</para></summary>
public static class WireOutbox
{
    /// <summary>The relay's own batch cap (lib/validate.js MAX_BATCH).</summary>
    public const int MaxBatch = 500;

    public static WireBatch Build(ReceiptLog log, string userId, byte[] namingKey)
    {
        var envelopes = new List<SyncReceipt>();
        long throughSeq = 0;
        string? refusal = null;

        foreach (var e in log.Unsent())
        {
            if (envelopes.Count == MaxBatch)
                break;

            if (e.HouseId == 0)
            {
                refusal = $"entry seq {e.Seq} carries house id 0 - cannot mint its entity";
                break;
            }
            if (!e.IsPot && e.PatchId == 0)
            {
                refusal = $"entry seq {e.Seq} carries patch id 0 - cannot mint its entity";
                break;
            }

            var entity = e.IsPot
                ? EntityName.ForPot(namingKey, e.HouseId, e.PotKey)
                : EntityName.ForBed(namingKey, e.HouseId, e.PatchId, e.BedSlot);

            envelopes.Add(new SyncReceipt(
                Id: SyncId.Format(userId, e.Era, e.Seq),
                Entity: entity,
                EntityType: e.IsPot ? SyncEntityType.Pot : SyncEntityType.Bed,
                Kind: e.Kind,
                At: e.At,
                Source: e.Source,
                Data: e.Data));
            throughSeq = e.Seq;
        }

        return new WireBatch(envelopes, throughSeq, refusal);
    }

    /// <summary>The POST body, byte-exact wire JSON: <c>{"receipts":[...]}</c>.</summary>
    public static string ToJson(IReadOnlyList<SyncReceipt> receipts)
        => JsonSerializer.Serialize(new { receipts }, SyncReceipt.WireOptions);
}
