using System;
using System.Linq;
using BalambGarden.Engine.Sensing;
using BalambGarden.Engine.Sync;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using ECommons.DalamudServices;

namespace BalambGarden.Game;

/// <summary>
/// The chat-receipt reader - the CLOSING half of the two-phase receipt lifecycle (design
/// ratified 2026-08-23; born as the fertilize watcher, widened the same day once the capture
/// gave us the plant, tend and water lines). Every gardening act the game confirms says so in
/// chat, whether Balamb's hands or the player's did it, so one listener serves both.
///
/// <para>Passive and read-only, the same law as <see cref="ChatRecon"/> and
/// <see cref="ObtainWatch"/>: it never handles, suppresses, or answers a message. Unlike
/// ChatRecon it is NOT debug-only - it ships, and it runs for the life of the plugin, because
/// the receipt it writes is the product.</para>
///
/// <para>TWO BRANCHES, DECIDED BY THE PENDING DESK. A recognised line first asks
/// <see cref="CensusPump.TryCloseChain"/> whether an act of ours is waiting on it. If one is,
/// the line is that act's confirmation and we are DONE here - the chain already delivered the
/// census when it acted, so the desk only has the history left to append, under the honest
/// source word. If nothing is waiting, the line is a HUMAN's own hands, and it becomes a
/// receipt in its own right down the chat-identity path below - census and history together,
/// the way the fertilize path has always worked.</para>
///
/// <para>IDENTITY COMES FROM THE PREVIOUS-TARGET, on the manual branch only. The success line
/// credits no object and the hard target is already cleared when it prints (capture
/// 2026-08-23), so the acted-on thing rides <c>Svc.Targets.PreviousTarget</c>. That slot can be
/// overwritten by fast retargeting within a couple of seconds, which is the poisoning window,
/// and the two defenses against it are both fail-closed:</para>
/// <para>1. KIND VS OBJECT - a bed line whose previous-target is not a bed receipts NOTHING,
/// and the same for a pot line against a non-pot. The game named the category in its own
/// sentence; if the object disagrees, the target moved.</para>
/// <para>2. SPECIES CORROBORATION (BEDS ONLY) - interacting with a planted bed prints the crop
/// name to chat as its own unattributed line first, so a bed-for-bed swap is catchable: if that
/// name disagrees with what the map says grows in the previous-target's bed, nothing is
/// receipted. The asymmetry is the game's, not a shortcut - a pot's species appears only inside
/// an addon dialog and never in chat, so there is no pot line to corroborate with, and pots run
/// on the cross-check alone. Absence of a name line is not a mismatch;
/// <see cref="FertilizeCorroboration"/> owns that rule and is tested at the desk.</para>
///
/// <para>THE SELF-CAPTURE INSTRUMENT (ruled into the lifecycle design). Harvest and the bed sow
/// line have never been taped, so those acts are expected to time out as chain-unconfirmed. To
/// end that honestly rather than never, any line wearing the success SHAPE (SystemMessage,
/// local player) that the classifier does not recognise is logged WHILE one of our acts is
/// open - a candidate close text, printed at the one moment we already know what was just
/// done. The missing lines capture themselves during ordinary play.</para>
///
/// <para>Never guess. A receipt we are not sure of is worse than no receipt: it becomes local
/// history, then it becomes a wire fact under an id nobody can retract.</para>
/// </summary>
internal static unsafe class ChatReceipts
{
    /// <summary>The most recent crop name chat resolved to a real species, and when. Session
    /// state only - it is evidence about the last few seconds, never anything to persist.</summary>
    private static ushort inspectedSpecies;
    private static DateTimeOffset? inspectedAt;

    internal static void Start()
    {
        Svc.Chat.ChatMessage += OnChatMessage;
        Plugin.Log.Information("[ChatReceipts] watching for gardening receipts");
    }

    internal static void Stop() => Svc.Chat.ChatMessage -= OnChatMessage;

    private static void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            var logKind = (int)message.LogKind;
            var fromLocalPlayer = message.SourceKind == XivChatRelationKind.LocalPlayer;
            var text = message.Message.TextValue;
            var verdict = ChatLine.Classify(logKind, fromLocalPlayer, text);

            if (verdict == ChatVerdict.None)
            {
                RememberCropName(logKind, message.SourceKind, text);
                NoteUnmatched(logKind, fromLocalPlayer, text);
                return;
            }

            // The pending desk FIRST: a line that closes one of our own acts is that act's
            // confirmation and nothing else. Asking before the identity decode is deliberate -
            // the chain already knows exactly which bed it acted on, so running the
            // previous-target ladder against a chain's own line could only ever produce a
            // second, weaker answer to a question already settled.
            if (CensusPump.TryCloseChain(verdict))
                return;

            var previous = Svc.Targets.PreviousTarget;
            if (ChatLine.IsPotVerdict(verdict))
                OnPot(verdict, previous);
            else
                OnBed(verdict, previous);
        }
        catch (Exception ex)
        {
            // A chat handler that throws breaks everyone's chat - never ours to risk.
            Plugin.Log.Warning($"[ChatReceipts] chat read failed: {ex.Message}");
        }
    }

    /// <summary>Banks the crop-name line a bed interaction prints. Resolution runs through
    /// the same species table the Talk-name path uses, so its colour-word stripping and
    /// aliases apply here too; anything that does not resolve to a real species is simply not
    /// evidence and is left un-banked rather than remembered as a zero.</summary>
    private static void RememberCropName(int logKind, XivChatRelationKind sourceKind, string text)
    {
        if (!ChatLine.IsInspectLine(logKind, sourceKind == XivChatRelationKind.None))
            return;
        if (Plugin.Tables.SpeciesIndexByName(text) is not { } species || species == 0)
            return;

        inspectedSpecies = species;
        inspectedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>The self-capture instrument: an unrecognised line wearing the success shape,
    /// printed while one of our acts is waiting on its confirmation. This is how the harvest
    /// and bed-sow lines - the two the 08-23 capture never caught - are meant to be found, with
    /// the open act's own kind naming what the words must mean.
    ///
    /// <para>Gated on an OPEN pending on purpose: the same shape carries gil spends and every
    /// other system notice ("You spent 303 gil." is SystemMessage from the local player too),
    /// so logging all of it would be a firehose nobody reads. Inside the window it is a
    /// shortlist of a line or two.</para></summary>
    private static void NoteUnmatched(int logKind, bool fromLocalPlayer, string text)
    {
        if (logKind != ChatLine.SystemMessageKind || !fromLocalPlayer)
            return;
        if (CensusPump.OpenPendingKind is not { } kind)
            return;

        Plugin.Log.Information(
            $"[ChatReceipts] unmatched line while {kind} receipt open - candidate close text: '{text}'");
    }

    private static void OnBed(ChatVerdict verdict, IGameObject? previous)
    {
        if (BedOf(previous) is not { } bed)
        {
            Plugin.Log.Warning(
                $"[ChatReceipts] {verdict} line with no bed on previous-target - not receipted (fail closed)");
            return;
        }

        // Read BEFORE the receipt, because the receipt's own SightNow refreshes the map: the
        // question is what was growing there at line time, not afterward.
        var known = KnownBedSpecies(bed.PatchOrdinal, bed.BedIndex);
        var (proceed, reason) = FertilizeCorroboration.Check(
            inspectedSpecies, inspectedAt, known, DateTimeOffset.UtcNow);
        if (!proceed)
        {
            Plugin.Log.Warning(
                $"[ChatReceipts] species corroboration failed - somebody acted, bed uncertain "
                + $"(fail closed): {reason}");
            return;
        }

        Plugin.Log.Information($"[ChatReceipts] bed corroboration: {reason}");
        CensusPump.OnChatReceipt(
            ChatLine.KindOf(verdict), isPot: false, potKey: -1,
            patchOrdinal: bed.PatchOrdinal, bedSlot: bed.BedIndex, patchId: bed.PatchId);
    }

    private static void OnPot(ChatVerdict verdict, IGameObject? previous)
    {
        if (PotKeyOf(previous) is not { } key)
        {
            Plugin.Log.Warning(
                $"[ChatReceipts] {verdict} line with no flowerpot on previous-target - not receipted (fail closed)");
            return;
        }

        CensusPump.OnChatReceipt(
            ChatLine.KindOf(verdict), isPot: true, potKey: key,
            patchOrdinal: 0, bedSlot: 0, patchId: 0);
    }

    /// <summary>The bed identity behind an object, or null when it is not one of our beds.
    /// Same decode as the recon dump: the gimmick carries patch ordinal, bed index and patch
    /// id, which is exactly the triple a receipt binds by.</summary>
    private static BedGimmick? BedOf(IGameObject? obj)
    {
        if (obj is null || !obj.IsValid() || obj.BaseId != ObjectSensor.GardenBedDataId)
            return null;

        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
        if (native == null)
            return null;

        return GimmickId.Decode(native->GimmickId);
    }

    /// <summary>The pot's map key, or null when the object is not a flowerpot. The pot-gate
    /// whitelist decides pot-ness off the game's own sheet, never off the name.</summary>
    private static int? PotKeyOf(IGameObject? obj)
    {
        if (obj is null || !obj.IsValid() || obj.ObjectKind != ObjectKind.HousingEventObject)
            return null;

        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
        if (native == null || !FlowerpotSheet.Rows.Contains(native->BaseId))
            return null;

        var housing = (FFXIVClientStructs.FFXIV.Client.Game.Object.HousingObject*)obj.Address;
        return housing->HousingFurnitureIndex;
    }

    /// <summary>What the last map read says grows in this bed, or 0 when we cannot know -
    /// no estate underfoot, an unbound patch, a slot the read did not cover. 0 means UNKNOWN
    /// and is never a species; the corroboration rule reads it that way.</summary>
    private static ushort KnownBedSpecies(int patchOrdinal, int bedSlot)
    {
        if (EstateSensor.Current() is not { } estate)
            return 0;
        if (Plugin.Garden.Census.BoundKey(estate, patchOrdinal) is not { } key)
            return 0;
        if (!CensusPump.LastOutdoor.TryGetValue(key, out var beds) || bedSlot >= beds.Count)
            return 0;
        return beds[bedSlot].SpeciesIndex;
    }
}
