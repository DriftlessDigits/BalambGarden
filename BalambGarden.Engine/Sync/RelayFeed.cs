using System.Text.Json;

namespace BalambGarden.Engine.Sync;

/// <summary>One page of the household stream, as the relay's GET answered it.</summary>
public sealed record RelayPage(IReadOnlyList<SyncReceipt> Receipts, string Cursor);

/// <summary>Reads the relay's GET answer: <c>{"receipts":[...],"cursor":"N"}</c>. The relay
/// stores payloads verbatim, so the envelopes come back as the byte-exact wire JSON the
/// senders minted - the shared <see cref="SyncReceipt.WireOptions"/> read them.
///
/// <para>ALL OR NOTHING: a malformed answer - bad JSON, a missing cursor, an envelope with
/// no id or entity - parses to null, never to a half-page. A poller that acted on a partial
/// parse could advance its cursor past receipts it never saw, and a silently skipped foreign
/// fact is exactly the class of loss the cursor exists to prevent.</para></summary>
public static class RelayFeed
{
    private sealed record Wire(List<SyncReceipt>? Receipts, string? Cursor);

    public static RelayPage? Parse(string json)
    {
        Wire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(json, SyncReceipt.WireOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (wire?.Receipts is null || string.IsNullOrEmpty(wire.Cursor))
            return null;
        if (wire.Receipts.Any(r => r is null || string.IsNullOrEmpty(r.Id) || string.IsNullOrEmpty(r.Entity)))
            return null;

        return new RelayPage(wire.Receipts, wire.Cursor);
    }
}
