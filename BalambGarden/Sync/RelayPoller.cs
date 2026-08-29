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
/// <para>THE CURSOR ADVANCES ONLY PAST APPLIED RECEIPTS. The page goes to
/// <see cref="ForeignReplay.ApplyPage"/>; if it reports anything less than fully applied, the
/// cursor holds and the next poll re-reads the same page - the relay's forward-ordered GET
/// makes that free, and a cursor that outran an unapplied receipt would lose a household
/// fact silently. Until piece 4 lands the replay, every page holds deliberately.</para></summary>
internal static class RelayPoller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly TimeSpan AtEstatePace = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdlePace = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ZoneInSettle = TimeSpan.FromSeconds(8);

    private static DateTime nextPollUtc = DateTime.MinValue;
    private static bool inFlight;
    private static string lastHeldCursor = "";

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
        var cursor = Plugin.Garden.Receipts.RelayCursor;
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

                    if (ForeignReplay.ApplyPage(page))
                    {
                        Plugin.Garden.Receipts.RelayCursor = page.Cursor;
                        Plugin.Garden.SaveReceipts();
                        lastHeldCursor = "";
                        return;
                    }

                    // Held: say so once per page, not once per poll - the same page will
                    // come back every cycle until the replay can take it.
                    if (lastHeldCursor != page.Cursor)
                    {
                        lastHeldCursor = page.Cursor;
                        Plugin.Log.Information(
                            $"[Sync] {page.Receipts.Count} foreign receipt(s) waiting at the "
                            + "relay - cursor holds until the replay path applies them");
                    }
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

/// <summary>Piece 4's seam, standing but deliberately inert: applying a foreign receipt means
/// routing it through the pump with own-user skip and the game-wins law, and none of that is
/// built yet. Refusing every page keeps the cursor honest - nothing is marked read before
/// something can actually read it.</summary>
internal static class ForeignReplay
{
    internal static bool ApplyPage(RelayPage page) => false;
}
