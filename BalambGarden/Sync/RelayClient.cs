namespace BalambGarden.Sync;

/// <summary>Shared relay-client constants. The stamp is the relay's speed bump against
/// scanner noise (open-household ruling 2026-08-29) - public like both repos, a filter and
/// not a lock; the relay's caps are the defense that holds.</summary>
internal static class RelayClient
{
    internal const string Stamp = "BalambGarden";
}
