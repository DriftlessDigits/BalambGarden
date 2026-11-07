using System;
using System.Collections.Generic;
using System.Linq;
using BalambGarden.Engine.Census;
using Dalamud.Game.Chat;
using ECommons.DalamudServices;

namespace BalambGarden.Game;

/// <summary>
/// Listens for the chat obtain line - the only thing the game says when a harvested crop
/// is actually in the bag. Harvest fires on the menu selection with no confirm and no
/// closing Talk (capture 2026-08-15 F4), so a chain that receipted at selection would be
/// claiming a yield it never watched arrive.
///
/// <para>Passive and read-only (Scrooge-style chat parsing): it never handles, suppresses,
/// or answers a message. A chain arms a timestamp before it selects Harvest and asks
/// whether an obtain line has landed since - that timestamp is the scope, so one chain's
/// harvest can never be completed by another's line.</para>
/// </summary>
internal static class ObtainWatch
{
    private static DateTime lastObtainUtc = DateTime.MinValue;

    /// <summary>The last few obtained items' text with their instants, for the run log only.
    /// NOT species names ("bouquet of red sunflowers" is not "Red Sunflowers") and never matched
    /// against one. A list, not one slot: a crossing harvest prints the crop line and then the
    /// seed bag in the same millisecond (capture 2026-10-18), and a single "last item" named the
    /// seed bag as the harvest ("done (obtained bag of sun lemon seeds)").</summary>
    private static readonly List<(DateTime At, string Item)> recent = [];
    private const int Kept = 8;

    /// <summary>Everything obtained since a chain armed, in arrival order, joined for the run
    /// log: "12 mirror apples + bag of sun lemon seeds". Empty when nothing has landed.</summary>
    internal static string ItemsSince(DateTime armedAt)
        => string.Join(" + ", recent.Where(r => r.At > armedAt).Select(r => r.Item));

    internal static void Start() => Svc.Chat.ChatMessage += OnChatMessage;

    internal static void Stop() => Svc.Chat.ChatMessage -= OnChatMessage;

    /// <summary>Take a timestamp before firing a harvest; hand it back to
    /// <see cref="FiredSince"/> to ask whether the yield has landed.</summary>
    internal static DateTime Arm() => DateTime.UtcNow;

    internal static bool FiredSince(DateTime armedAt) => lastObtainUtc > armedAt;

    private static void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            var text = message.Message.TextValue;
            if (ObtainLine.Item(text) is not { } item)
                return;

            lastObtainUtc = DateTime.UtcNow;
            recent.Add((lastObtainUtc, item));
            if (recent.Count > Kept)
                recent.RemoveAt(0);
            // The chat type is logged, never filtered on: the recon has one live obtain
            // line and no reading of which channel it came down. A chain only consults
            // this inside a step it opened seconds earlier, so a stray line elsewhere in
            // chat has no window to be believed in. Narrow the filter once the bench says
            // which type it is.
            Plugin.Log.Information($"[Obtain] ({message.LogKind}) '{text}'");
        }
        catch (Exception ex)
        {
            // A chat handler that throws breaks everyone's chat - never ours to risk.
            Plugin.Log.Warning($"[Obtain] chat read failed: {ex.Message}");
        }
    }
}
