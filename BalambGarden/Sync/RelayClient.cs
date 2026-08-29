using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using BalambGarden.Engine.Sync;

namespace BalambGarden.Sync;

/// <summary>The one relay transport (QA pass 2026-08-29 - sender and poller had each grown
/// their own copy of the client, the auth preamble, the URL join and the backoff wording;
/// an auth change had two places to silently miss). One socket pool, one place the bearer
/// and stamp are attached; callers keep only their cadence and their body/ack handling.
///
/// <para>The stamp is the relay's speed bump against scanner noise (open-household ruling
/// 2026-08-29) - public like both repos, a filter and not a lock; the relay's caps are the
/// defense that holds.</para></summary>
internal static class RelayClient
{
    internal const string Stamp = "BalambGarden";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    internal static string Endpoint(string query = "")
        => Plugin.Configuration.SyncRelayUrl.TrimEnd('/') + "/receipts" + query;

    /// <summary>One authenticated round-trip. Throws what HttpClient throws - the caller's
    /// catch is the backoff.</summary>
    internal static async Task<(int Status, string Body)> Send(
        HttpMethod method, string url, string phrase, string? json = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", HouseholdKeys.AuthToken(phrase));
        request.Headers.Add("X-Balamb-Client", Stamp);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ((int)response.StatusCode, body);
    }
}
