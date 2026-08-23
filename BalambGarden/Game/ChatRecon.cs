#if DEBUG
using System;
using System.Linq;
using BalambGarden.Engine.Sensing;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;

namespace BalambGarden.Game;

/// <summary>
/// Chat recon (debug builds only) - the capture instrument for the manual-actions lap
/// (scoped 2026-08-22 S3): every chat line verbatim, and everything the future
/// chat-receipt parser could want to know about it, dumped to the log while a human runs
/// the verbs BY HAND (tend / plant / harvest / pot-water / fertilize / re-fertilize
/// refusal) with no Balamb chains running.
///
/// <para>Per line: LogKind (name + numeric - the parser's channel filter), the
/// source/target relation kinds (the game's own word on who acted), the sender, the
/// verbatim text, the SeString payload shape (an item link is not text, and a parser
/// that only ever saw TextValue would not know the seam existed), and the identity
/// question's raw material: the player's current hard target, soft target, and previous
/// target AT THE MOMENT THE LINE LANDED - decoded to bed gimmick or pot key where the
/// object is one of ours. Whether target-at-line-time carries bed identity IS the open
/// question; this dump is how it gets answered, not where it is assumed.</para>
///
/// <para>Passive and read-only, same law as ObtainWatch: never handles, suppresses, or
/// answers a message. Manual toggle from the Recon tab, no auto-arm - the circuit is
/// deliberate, not ambient - and it survives zone lines on purpose (the circuit spans
/// outdoor beds and indoor pots).</para>
/// </summary>
internal static unsafe class ChatRecon
{
    internal static bool Watching { get; private set; }

    internal static void StartWatching()
    {
        if (Watching)
            return;

        Watching = true;
        Svc.Chat.ChatMessage += OnChatMessage;
        Plugin.Log.Information("[ChatRecon] watching all chat (verbatim + target-at-line-time)");
    }

    internal static void StopWatching()
    {
        if (!Watching)
            return;

        Watching = false;
        Svc.Chat.ChatMessage -= OnChatMessage;
        Plugin.Log.Information("[ChatRecon] stopped");
    }

    private static void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            var sender = message.Sender.TextValue;
            Plugin.Log.Information(
                $"[ChatRecon] kind={message.LogKind}/{(int)message.LogKind}"
                + $" src={message.SourceKind} tgt={message.TargetKind}"
                + (sender.Length > 0 ? $" sender='{sender}'" : "")
                + $" text='{message.Message.TextValue}'");

            // Payload shape, not payload dump: the parser needs to know WHERE the seams
            // are (text | item link | text), the verbatim line above already carries the
            // words. GetType().Name so the dump can never disagree with the API's own
            // taxonomy.
            var shape = string.Join("|", message.Message.Payloads.Select(p => p.GetType().Name));
            if (shape.Length > 0 && shape != "TextPayload")
                Plugin.Log.Information($"[ChatRecon]   payloads: {shape}");

            Plugin.Log.Information(
                $"[ChatRecon]   zone={Plugin.ClientState.TerritoryType}"
                + $" target={Describe(Svc.Targets.Target)}"
                + $" soft={Describe(Svc.Targets.SoftTarget)}"
                + $" prev={Describe(Svc.Targets.PreviousTarget)}");
        }
        catch (Exception ex)
        {
            // A chat handler that throws breaks everyone's chat - never ours to risk.
            Plugin.Log.Warning($"[ChatRecon] chat read failed: {ex.Message}");
        }
    }

    /// <summary>One object, one token: name + kind + the decode that matters when it is
    /// one of ours - bed gimmick (patch/bed/patch-id, the identity a chat receipt would
    /// bind by) or pot map key (the DataMap entry the ledger keys pots by).</summary>
    private static string Describe(IGameObject? obj)
    {
        if (obj is null || !obj.IsValid())
            return "none";

        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
        if (native == null)
            return $"'{obj.Name.TextValue}'({obj.ObjectKind})";

        if (obj.BaseId == ObjectSensor.GardenBedDataId)
        {
            var g = GimmickId.Decode(native->GimmickId);
            return $"'{obj.Name.TextValue}'(bed patch={g.PatchOrdinal} bed={g.BedIndex} id=0x{g.PatchId:X4})";
        }

        if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.HousingEventObject
            && FlowerpotSheet.Rows.Contains(native->BaseId))
        {
            var housing = (FFXIVClientStructs.FFXIV.Client.Game.Object.HousingObject*)obj.Address;
            return $"'{obj.Name.TextValue}'(pot key={housing->HousingFurnitureIndex})";
        }

        return $"'{obj.Name.TextValue}'({obj.ObjectKind} base={obj.BaseId})";
    }
}
#endif
