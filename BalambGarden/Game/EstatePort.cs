using System;
using System.Collections.Generic;
using BalambGarden.Engine.Census;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace BalambGarden.Game;

/// <summary>
/// Estate teleports - the Scrooge PortOrchestrator pattern (its receipts ride along:
/// the empty-list-fails-silent trap, the game's-own-answer cast gate) pointed at the
/// housing entries of the player's own teleport list.
///
/// <para>THE LAW (inherited verbatim): <see cref="Fire"/>'s only caller is a button
/// click. Nothing in this plugin may cast a teleport on a timer, a completion event,
/// or a stage transition.</para>
///
/// <para>The estate join is HouseId decomposition against the census key - the same
/// corroboration idiom as <see cref="EstateSensor"/>: district territory must match
/// exactly (receipt-confirmed to be the exterior district, 08-15), ward/plot accept
/// the same off-by-one tolerance (HouseId's 0- vs 1-based convention is not
/// receipt-confirmed). Room is ignored on house shapes deliberately: the teleport
/// reaches the PLOT, which serves the house and every room in it. And the census's
/// own binding law holds here too: exactly one surviving match or no button - an
/// ambiguous match under tolerance refuses rather than guesses.</para>
/// </summary>
internal static unsafe class EstatePort
{
    /// <summary>Action id 5 = Teleport, the shared "cast it" action every aetheryte
    /// port uses (Scrooge receipt).</summary>
    private const uint TeleportActionId = 5;

    /// <summary>What the button needs to exist and what <see cref="Fire"/> needs to cast.</summary>
    internal readonly record struct Destination(uint AetheryteId, byte SubIndex, uint GilCost);

    // The teleport list is read every frame the window draws, but rebuilt (and matched)
    // on a throttle: UpdateAetheryteList is what populates it (a session that never
    // opened the game's teleport window holds an EMPTY list, and Teleport() on an empty
    // list fails silently - Scrooge's receipt), and once every few seconds is exactly as
    // fresh as a list that only changes when the player buys a house.
    private const double RefreshSeconds = 5.0;
    private static DateTime _nextRefresh = DateTime.MinValue;
    private static readonly List<TeleportInfo> Cache = [];

    /// <summary>The unique teleport destination for this estate, or null (not attuned,
    /// not on the list, or ambiguous under the ward/plot tolerance).</summary>
    internal static Destination? FindFor(EstateKey key)
    {
        RefreshCache();

        Destination? survivor = null;
        foreach (var entry in Cache)
        {
            if (!Matches(entry, key))
                continue;
            if (survivor is not null)
            {
                // Two entries claiming one estate (or one entry two ways) is the
                // off-by-one tolerance biting - refuse, exactly like JoinConfirm.
                Plugin.Log.Warning(
                    $"[EstatePort] more than one teleport entry matches {key.DisplayLabel()} - refusing the button");
                return null;
            }
            survivor = new Destination(entry.AetheryteId, entry.SubIndex, entry.GilCost);
        }

        return survivor;
    }

    private static void RefreshCache()
    {
        if (DateTime.UtcNow < _nextRefresh)
            return;

        // No player, no native call. A zone transition destroys the local player while
        // the window keeps drawing, and UpdateAetheryteList dereferences player state -
        // Telepo.Instance() survives the teardown, so it alone is not a sufficient gate
        // (receipt: appcrash 2026-08-23 08:51, refresh tick ~5s after ZoneInit killed
        // the client). The throttle is deliberately NOT consumed here: the first frame
        // with a player back gets a fresh list immediately.
        if (!ECommons.GameHelpers.Player.Available)
            return;

        _nextRefresh = DateTime.UtcNow.AddSeconds(RefreshSeconds);

        Cache.Clear();
        var telepo = Telepo.Instance();
        if (telepo == null)
            return;

        telepo->UpdateAetheryteList();
        foreach (var entry in telepo->TeleportList)
        {
            // Only housing entries can name an estate; city aetherytes carry no HouseId.
            if (entry.HouseId.Id != 0)
                Cache.Add(entry);
        }
    }

    private static bool Matches(in TeleportInfo entry, EstateKey key)
    {
        var houseId = entry.HouseId;

        if (key.IsApartment)
            return houseId.IsApartment
                && houseId.TerritoryTypeId == key.TerritoryId
                && Agrees(houseId.WardIndex, key.Ward)
                && houseId.ApartmentDivision == key.ApartmentDivision;

        return !houseId.IsApartment
            && houseId.TerritoryTypeId == key.TerritoryId
            && Agrees(houseId.WardIndex, key.Ward)
            && Agrees(houseId.PlotIndex, key.Plot);
    }

    /// <summary>Same tolerance as EstateSensor: HouseId's 0- vs 1-based convention is
    /// unconfirmed, so either reading is accepted; an off-by-one cannot change which
    /// district the answer names, and the one-survivor rule catches it if it ever
    /// makes a match ambiguous.</summary>
    private static bool Agrees(int fromHouseId, int fromCensus)
        => fromHouseId == fromCensus || fromHouseId == fromCensus + 1;

    /// <summary>Whether the game will let Teleport be cast right now. Status 0 =
    /// castable; anything else is the game's own refusal (combat, occupied, duty-bound,
    /// mid-cast) - one precondition that stays true when the game grows new reasons.</summary>
    internal static bool Castable(out string why)
    {
        var am = ActionManager.Instance();
        if (am == null)
        {
            why = "the game isn't answering about your actions";
            return false;
        }

        var status = am->GetActionStatus(ActionType.Action, TeleportActionId);
        if (status == 0)
        {
            why = "Teleport is ready";
            return true;
        }

        why = "the game won't let you teleport right now";
        return false;
    }

    /// <summary>Casts the teleport. THE ONLY CALLER IS A BUTTON CLICK. Fails loud and
    /// named on every path, and a failure moves nobody.</summary>
    internal static bool Fire(Destination dest, string estateName)
    {
        // Same guard as RefreshCache: a click can only land with a player present, but
        // the native call is unsafe without one, so the gate is stated, not assumed.
        var telepo = ECommons.GameHelpers.Player.Available ? Telepo.Instance() : null;
        if (telepo == null)
        {
            Svc.Chat.PrintError("[Balamb] Can't teleport - the game's teleport system isn't answering.");
            return false;
        }

        // Re-verify against a fresh list at cast time: the cache is for buttons, the
        // cast trusts only what the game says right now.
        telepo->UpdateAetheryteList();
        var present = false;
        foreach (var entry in telepo->TeleportList)
        {
            if (entry.AetheryteId != dest.AetheryteId || entry.SubIndex != dest.SubIndex)
                continue;
            present = true;
            break;
        }

        if (!present)
        {
            Svc.Chat.PrintError(
                $"[Balamb] Can't teleport - {estateName} isn't on your teleport list any more.");
            return false;
        }

        if (!telepo->Teleport(dest.AetheryteId, dest.SubIndex))
        {
            Svc.Chat.PrintError($"[Balamb] The teleport to {estateName} was refused. Try again in a moment.");
            return false;
        }

        Svc.Chat.Print($"[Balamb] Teleporting to {estateName}.");
        return true;
    }
}
