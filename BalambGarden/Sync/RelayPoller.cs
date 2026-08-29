using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using BalambGarden.Engine.Sync;
using ECommons.DalamudServices;

namespace BalambGarden.Sync;

/// <summary>The inbox half (client arc piece 3): reads the household stream forward from the
/// persisted cursor. Same threading law as <see cref="RelaySender"/> - framework-thread tick,
/// HTTP off-thread, everything that touches the log marshaled back.
///
/// <para>CADENCE (handoff-ruled 60s/5min/zone-in): 60 seconds while standing somewhere with
/// estate identity - that is where a foreign receipt would change what the dashboard says
/// right now - 5 minutes anywhere else, and a zone line pulls the next poll close (the settle
/// beat, not instantly: the pump itself waits for the map, and a poll racing the loading
/// screen would burn its attempt on a client that cannot use the answer yet).</para>
///
/// <para>EVERY RECEIPT LANDS SOMEWHERE BEFORE THE CURSOR MOVES (piece 4 ruling): applied to
/// a matched row, parked in the ledger's ForeignHold for a census visit to unlock, skipped
/// as our own echo, or dropped loudly as unparseable. Parking is what lets the cursor
/// advance without loss - hold-forever would dam the whole household stream on one
/// orphaned entity.</para></summary>
internal static class RelayPoller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly TimeSpan AtEstatePace = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdlePace = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ZoneInSettle = TimeSpan.FromSeconds(8);

    private static DateTime nextPollUtc = DateTime.MinValue;
    private static bool inFlight;

    /// <summary>A zone line: pull the next poll to just past the settle beat, never push it
    /// out (a poll already due stays due).</summary>
    internal static void NoteZoneIn()
    {
        var soon = DateTime.UtcNow + ZoneInSettle;
        if (soon < nextPollUtc)
            nextPollUtc = soon;
    }

    internal static void Tick()
    {
        if (inFlight || DateTime.UtcNow < nextPollUtc)
            return;

        var phrase = Plugin.Configuration.SyncPassphrase;
        if (phrase.Length == 0)
            return;

        nextPollUtc = DateTime.UtcNow
            + (Game.EstateSensor.Current() is null ? IdlePace : AtEstatePace);

        inFlight = true;
        var token = HouseholdKeys.AuthToken(phrase);
        var cursor = Plugin.Garden.Ledger.RelayCursor;   // rides the ledger file (ruling 9)
        var url = Plugin.Configuration.SyncRelayUrl.TrimEnd('/')
            + "/receipts?since=" + Uri.EscapeDataString(cursor);

        Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var response = await Http.SendAsync(request).ConfigureAwait(false);
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    OnFailure($"relay answered {(int)response.StatusCode}");
                    return;
                }

                var page = RelayFeed.Parse(payload);
                if (page is null)
                {
                    OnFailure("relay answer did not parse - cursor holds, nothing skipped");
                    return;
                }

                await Svc.Framework.RunOnFrameworkThread(() =>
                {
                    inFlight = false;

                    if (page.Receipts.Count == 0)
                        return;   // caught up; the echoed cursor equals ours

                    // Apply-then-advance, one ledger save for both: unmatched receipts
                    // PARKED inside the same file, so advancing past them loses nothing
                    // (piece 4 ruling - the stream never dams).
                    ForeignReplay.ApplyPage(page);
                    Plugin.Garden.Ledger.RelayCursor = page.Cursor;
                    Plugin.Garden.Save();
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnFailure(ex.Message);
            }
        });
    }

    private static void OnFailure(string why)
    {
        nextPollUtc = DateTime.UtcNow + FailureBackoff;
        inFlight = false;
        Plugin.Log.Warning($"[Sync] poll failed ({why}) - retrying in {FailureBackoff.TotalMinutes:0}m");
    }
}

/// <summary>The apply half (piece 4): pure Engine logic does the work; this only feeds it
/// the page and says what happened. Runs on the framework thread (the poller marshals), so
/// ledger mutation is single-threaded like every other write path.</summary>
internal static class ForeignReplay
{
    internal static void ApplyPage(RelayPage page)
    {
        var outcome = ForeignApply.ApplyPage(
            Plugin.Garden.Ledger, page.Receipts,
            Plugin.Configuration.SyncUserId, DateTimeOffset.UtcNow);

        if (outcome.Applied > 0 || outcome.Parked > 0)
            Plugin.Log.Information(
                $"[Sync] household receipts: {outcome.Applied} applied"
                + (outcome.Parked > 0
                    ? $", {outcome.Parked} parked (their estates await a census visit)" : "")
                + (outcome.OwnSkipped > 0 ? $", {outcome.OwnSkipped} own echoed back" : ""));

        if (outcome.Dropped > 0)
            Plugin.Log.Warning(
                $"[Sync] {outcome.Dropped} relay receipt(s) carried unparseable ids - dropped "
                + "(the count is the instrument; a growing number means a sender is minting bad ids)");
    }
}
