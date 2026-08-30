using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using BalambGarden.Engine.Sync;
using ECommons.DalamudServices;

namespace BalambGarden.Sync;

/// <summary>The outbox's ride to the relay (client arc piece 2). Ticks on the framework
/// thread, self-throttled like the pump; the HTTP round-trip runs off-thread via
/// <see cref="RelayClient"/> and only the ack - MarkSent + save - comes back to the
/// framework thread, so the log is never touched from two threads.
///
/// <para>OFFLINE IS THE OUTBOX, not a second queue: receipts.json already persists every
/// unsent entry, so a send that never happens costs nothing and the next tick simply tries
/// again. Failure backs off; a DAMMED batch (WireOutbox refusal - an entry that cannot mint
/// its entity) warns once per reason and backs off long, because it is a bug's signature,
/// not weather.</para>
///
/// <para>Empty passphrase = sync off = zero network surface (ruling 16). The check is first,
/// every tick.</para></summary>
internal static class RelaySender
{
    private static readonly TimeSpan IdlePace = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DamBackoff = TimeSpan.FromMinutes(15);

    private static DateTime nextAttemptUtc = DateTime.MinValue;
    private static bool inFlight;
    private static string lastRefusalWarned = "";

    internal static void Tick()
    {
        if (inFlight || DateTime.UtcNow < nextAttemptUtc)
            return;

        var phrase = Plugin.Configuration.SyncPassphrase;
        if (phrase.Length == 0)
            return;

        // Pace before peeking: Unsent() walks the entries, and an empty outbox at 60fps
        // must not mean 60 walks a second.
        nextAttemptUtc = DateTime.UtcNow + IdlePace;

        var user = Plugin.Configuration.SyncUserId;
        if (user.Length == 0)
            return;   // WireSyncIdentity mints at load; empty here means load never ran

        var batch = WireOutbox.Build(
            Plugin.Garden.Receipts, user, HouseholdKeys.NamingKey(phrase));

        if (batch.Refusal is { } refusal)
        {
            // Warn once per distinct reason, not once per tick - the dam does not move
            // until code or data does, and a log pile would bury the signature.
            if (refusal != lastRefusalWarned)
            {
                lastRefusalWarned = refusal;
                Plugin.Log.Warning(
                    $"[Sync] outbox DAMMED: {refusal} - nothing sends until this entry is "
                    + "understood (this should be impossible on a v2 log; it is a bug's signature)");
            }
            nextAttemptUtc = DateTime.UtcNow + DamBackoff;
            if (batch.Receipts.Count == 0)
                return;
            // Entries ahead of the dam still ship - facts the relay can hold already.
        }

        if (batch.Receipts.Count == 0)
            return;

        inFlight = true;
        var body = WireOutbox.ToJson(batch.Receipts);
        var count = batch.Receipts.Count;
        var throughSeq = batch.ThroughSeq;

        Task.Run(async () =>
        {
            try
            {
                var (status, payload) =
                    await RelayClient.Send(HttpMethod.Post, RelayClient.Endpoint(), phrase, body)
                        .ConfigureAwait(false);

                if (status is < 200 or >= 300)
                {
                    Backoff($"relay answered {status}: {Truncate(payload)}");
                    return;
                }

                var inserted = ParseInserted(payload);
                await Svc.Framework.RunOnFrameworkThread(() =>
                {
                    // The ack is the relay saying it HOLDS these ids - duplicates included
                    // (kept acks re-sends too), so the mark is safe on any 200.
                    Plugin.Garden.Receipts.MarkSent(throughSeq);
                    Plugin.Garden.SaveReceipts();
                    Plugin.Log.Information(
                        $"[Sync] sent {count} receipt(s) through seq {throughSeq}"
                        + (inserted is { } n ? $" ({n} new to the relay)" : ""));
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Backoff(ex.Message);
            }
            finally
            {
                inFlight = false;   // the one place the flag clears, whatever path ran
            }
        });
    }

    /// <summary>Failure never touches the log: the outbox still holds everything, and the
    /// next attempt re-builds the same batch - the relay's idempotent insert makes a repeat
    /// harmless even if this failure was actually a lost ack.</summary>
    private static void Backoff(string why)
    {
        nextAttemptUtc = DateTime.UtcNow + FailureBackoff;
        Plugin.Log.Warning(
            $"[Sync] send failed ({why}) - outbox keeps everything, retrying in {FailureBackoff.TotalMinutes:0}m");
    }

    private static int? ParseInserted(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("inserted", out var n) ? n.GetInt32() : null;
        }
        catch
        {
            return null;   // cosmetic only - the 2xx already carried the ack
        }
    }

    private static string Truncate(string s)
        => s.Length <= 120 ? s : s[..120] + "...";
}
