using System;
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

    /// <summary>The last obtained item's text, for the run log only. It is NOT a species
    /// name ("bouquet of red sunflowers" is not "Red Sunflowers") and is never matched
    /// against one.</summary>
    internal static string LastItem { get; private set; } = "";

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
            LastItem = item;
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
