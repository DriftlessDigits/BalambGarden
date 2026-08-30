using System;
using System.Net.Http;
using System.Threading.Tasks;
using BalambGarden.Engine.Sync;
using ECommons.DalamudServices;

namespace BalambGarden.Sync;

/// <summary>The inbox half (client arc piece 3): reads the household stream forward from the
/// persisted cursor. Same threading law as <see cref="RelaySender"/> - framework-thread
/// tick, HTTP off-thread via <see cref="RelayClient"/>, everything that touches the ledger
/// marshaled back.
///
/// <para>CADENCE (handoff-ruled 60s/5min/zone-in): 60 seconds while standing somewhere with
/// estate identity - that is where a foreign receipt would change what the dashboard says
/// right now - 5 minutes anywhere else, and a zone line pulls the next poll close (the settle
/// beat, not instantly: the pump itself waits for the map, and a poll racing the loading
/// screen would burn its attempt on a client that cannot use the answer yet).</para>
///
/// <para>EVERY RECEIPT LANDS SOMEWHERE BEFORE THE CURSOR MOVES: applied to a matched row,
/// parked in the ledger's ForeignHold for a census visit to unlock, skipped as our own echo,
/// or dropped loudly as unparseable. <see cref="ForeignApply.AbsorbPage"/> owns that
/// invariant - apply and cursor advance are one tested Engine call, not statement order
/// here.</para></summary>
internal static class RelayPoller
{
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
        var url = RelayClient.Endpoint(
            "?since=" + Uri.EscapeDataString(Plugin.Garden.Ledger.RelayCursor));

        Task.Run(async () =>
        {
            try
            {
                var (status, payload) =
                    await RelayClient.Send(HttpMethod.Get, url, phrase).ConfigureAwait(false);

                if (status is < 200 or >= 300)
                {
                    Backoff($"relay answered {status}");
                    return;
                }

                var page = RelayFeed.Parse(payload);
                if (page is null)
                {
                    Backoff("relay answer did not parse - cursor holds, nothing skipped");
                    return;
                }

                if (page.Receipts.Count == 0)
                    return;   // caught up; the echoed cursor equals ours

                await Svc.Framework.RunOnFrameworkThread(() =>
                {
                    var outcome = ForeignApply.AbsorbPage(
                        Plugin.Garden.Ledger, page,
                        Plugin.Configuration.SyncUserId, DateTimeOffset.UtcNow);
                    Plugin.Garden.Save();
                    Report(outcome);
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

    private static void Report(ForeignOutcome outcome)
    {
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

    private static void Backoff(string why)
    {
        nextPollUtc = DateTime.UtcNow + FailureBackoff;
        Plugin.Log.Warning($"[Sync] poll failed ({why}) - retrying in {FailureBackoff.TotalMinutes:0}m");
    }
}
