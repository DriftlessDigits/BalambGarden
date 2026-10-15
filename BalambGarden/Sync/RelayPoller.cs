using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BalambGarden.Engine.Ledger;
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

                await Svc.Framework.RunOnFrameworkThread(() =>
                {
                    if (page.Receipts.Count == 0)
                    {
                        // Caught up; the echoed cursor equals ours. The one thing still owed
                        // on a quiet poll is a history write that failed last time - rows
                        // already in memory, cursor held until they are on disk.
                        if (Plugin.Garden.History.Dirty && Plugin.Garden.SaveHistory())
                            Plugin.Log.Information("[Sync] history save retried and landed");
                        return;
                    }

                    var now = DateTimeOffset.UtcNow;
                    var outcome = ForeignApply.AbsorbPage(
                        Plugin.Garden.Ledger, page, Plugin.Configuration.SyncUserId, now,
                        Plugin.Garden.History, Plugin.Garden.SaveHistory);

                    // The parking lot re-runs on every page while anything is held, not only
                    // after a mint (pass-3 #5): a row held back by a failed history save
                    // must not wait days for the next census visit.
                    var rebases = new List<RebaseEvent>(outcome.Rebases);
                    var landed = Plugin.Garden.Ledger.ForeignHold.Count > 0
                        ? ForeignApply.RetryParked(
                            Plugin.Garden.Ledger, now, Plugin.Garden.History, Plugin.Garden.SaveHistory, rebases)
                        : 0;

                    Plugin.Garden.Save();
                    Game.CensusPump.ReportRebases(rebases);
                    Report(outcome, landed);
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

    private static void Report(ForeignOutcome outcome, int landed)
    {
        if (outcome.Applied > 0 || outcome.Parked > 0 || landed > 0)
            Plugin.Log.Information(
                $"[Sync] household receipts: {outcome.Applied} applied"
                + (outcome.Parked > 0
                    ? $", {outcome.Parked} parked (their estates await a census visit)" : "")
                + (landed > 0 ? $", {landed} parked earlier now landed" : "")
                + (outcome.HistoryToRecord.Count > 0
                    ? $", {outcome.HistoryToRecord.Count} plant/harvest row(s) to history" : "")
                + (outcome.Contradicted > 0
                    ? $", {outcome.Contradicted} older sighting(s) contradicted the record (replant evidence, ledger untouched)" : "")
                + (outcome.OwnSkipped > 0 ? $", {outcome.OwnSkipped} own echoed back" : ""));

        if (!outcome.CursorAdvanced)
            Plugin.Log.Warning(
                $"[Sync] history save failed - cursor held at {Plugin.Garden.Ledger.RelayCursor}; "
                + "the page is fetched again next poll and re-recorded by id");

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
