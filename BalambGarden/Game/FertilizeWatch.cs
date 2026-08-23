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
/// The first chat-receipt parser (schema design ratified 2026-08-23; fertilize is the ruled
/// prerequisite for sync - a feed nobody watched is a hole in the history the relay cannot
/// fill). Fertilize has no chain and no closing dialogue: the chat line is the whole event,
/// so this listens for it whether Balamb is driving or a human is doing it by hand.
///
/// <para>Passive and read-only, the same law as <see cref="ChatRecon"/> and
/// <see cref="ObtainWatch"/>: it never handles, suppresses, or answers a message. Unlike
/// ChatRecon it is NOT debug-only - it ships, and it runs for the life of the plugin, because
/// the receipt it writes is the product.</para>
///
/// <para>IDENTITY COMES FROM THE PREVIOUS-TARGET. The success line credits no object and the
/// hard target is already cleared when it prints (capture 2026-08-23), so the acted-on thing
/// rides <c>Svc.Targets.PreviousTarget</c>. That slot can be overwritten by fast retargeting
/// within a couple of seconds, which is the poisoning window, and the two defenses against it
/// are both fail-closed:</para>
/// <para>1. KIND VS OBJECT - a bed line whose previous-target is not a bed receipts NOTHING,
/// and the same for a pot line against a non-pot. The game named the category in its own
/// sentence; if the object disagrees, the target moved.</para>
/// <para>2. SPECIES CORROBORATION (BEDS ONLY) - interacting with a planted bed prints the
/// crop name to chat as its own unattributed line first, so a bed-for-bed swap is catchable:
/// if that name disagrees with what the map says grows in the previous-target's bed, nothing
/// is receipted. The asymmetry is the game's, not a shortcut - a pot's species appears only
/// inside an addon dialog and never in chat, so there is no pot line to corroborate with, and
/// pots run on the cross-check alone. Absence of a name line is not a mismatch;
/// <see cref="FertilizeCorroboration"/> owns that rule and is tested at the desk.</para>
///
/// <para>Never guess. A receipt we are not sure of is worse than no receipt: it becomes local
/// history, then it becomes a wire fact under an id nobody can retract.</para>
/// </summary>
internal static unsafe class FertilizeWatch
{
    /// <summary>The most recent crop name chat resolved to a real species, and when. Session
    /// state only - it is evidence about the last few seconds, never anything to persist.</summary>
    private static ushort inspectedSpecies;
    private static DateTimeOffset? inspectedAt;

    internal static void Start()
    {
        Svc.Chat.ChatMessage += OnChatMessage;
        Plugin.Log.Information("[Fertilize] watching for fertilize receipts");
    }

    internal static void Stop() => Svc.Chat.ChatMessage -= OnChatMessage;

    private static void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            var logKind = (int)message.LogKind;
            var text = message.Message.TextValue;
            var verdict = ChatLine.Classify(
                logKind, message.SourceKind == XivChatRelationKind.LocalPlayer, text);

            if (verdict == ChatVerdict.None)
            {
                RememberCropName(logKind, message.SourceKind, text);
                return;
            }

            var previous = Svc.Targets.PreviousTarget;
            if (verdict == ChatVerdict.FertilizeBed)
                OnBed(previous);
            else
                OnPot(previous);
        }
        catch (Exception ex)
        {
            // A chat handler that throws breaks everyone's chat - never ours to risk.
            Plugin.Log.Warning($"[Fertilize] chat read failed: {ex.Message}");
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

    private static void OnBed(IGameObject? previous)
    {
        if (BedOf(previous) is not { } bed)
        {
            Plugin.Log.Warning(
                "[Fertilize] fertilize line with no bed on previous-target - not receipted (fail closed)");
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
                $"[Fertilize] species corroboration failed - somebody fertilized, bed uncertain "
                + $"(fail closed): {reason}");
            return;
        }

        Plugin.Log.Information($"[Fertilize] bed corroboration: {reason}");
        CensusPump.OnChatFertilize(
            isPot: false, potKey: -1,
            patchOrdinal: bed.PatchOrdinal, bedSlot: bed.BedIndex, patchId: bed.PatchId);
    }

    private static void OnPot(IGameObject? previous)
    {
        if (PotKeyOf(previous) is not { } key)
        {
            Plugin.Log.Warning(
                "[Fertilize] fertilize line with no flowerpot on previous-target - not receipted (fail closed)");
            return;
        }

        CensusPump.OnChatFertilize(
            isPot: true, potKey: key, patchOrdinal: 0, bedSlot: 0, patchId: 0);
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
