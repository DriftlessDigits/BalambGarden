using System.Collections.Generic;
using BalambGarden.Engine.Census;

namespace BalambGarden.Game;

/// <summary>
/// Which pot object is which map entry. The chain is the only thing that ever knows: it
/// holds the <see cref="PotObject"/> it drove while the map diff produces the key, and
/// that pairing is true for exactly one instant unless somebody writes it down. This is
/// where it gets written down.
///
/// <para>SESSION-SCOPED AND NOT PERSISTED, on purpose. A furniture EntityId is an index
/// into the object table the client happens to have built this visit; whether the same
/// flowerpot comes back with the same id after a zone reload, a plugin reload or a
/// relog has ZERO receipts today. Persisting an unstable id would silently label the
/// wrong pot with somebody else's plant, which is the one failure a fail-closed surface
/// must not have. So the cache lives and dies with the plugin, and a reload honestly
/// forgets.</para>
///
/// <para>What would earn persistence: the pairing log line below, observed twice - the
/// SAME entity id re-binding to the SAME map key on a later visit (ideally across a
/// relog, not just a zone hop). Two of those on different pots and the id is evidence
/// rather than a guess; until then it stays in memory.</para>
/// </summary>
internal static class PotIdentity
{
    private static readonly Dictionary<(EstateKey Estate, uint EntityId), int> pairings = [];

    /// <summary>Records the pot the chain just drove against the key its diff produced.
    /// Also the instrument: this line is the only place the id-stability question can be
    /// answered from, so it prints every time even when the pairing is already known.</summary>
    internal static void Remember(EstateKey estate, uint entityId, int mapKey)
    {
        pairings[(estate, entityId)] = mapKey;
        Plugin.Log.Information(
            $"[Census] pot pairing: entity 0x{entityId:X8} -> key {mapKey} at {estate.DisplayLabel()}");
    }

    /// <summary>The key this pot object was last seen to be, or null. Null is the honest
    /// answer for every pot nobody has acted on this session, including ones the ledger
    /// remembers perfectly well - the ledger knows the key, not which object in the room
    /// wears it.</summary>
    internal static int? KeyFor(EstateKey estate, uint entityId)
        => pairings.TryGetValue((estate, entityId), out var key) ? key : null;
}
