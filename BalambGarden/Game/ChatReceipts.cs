using System;
using System.Linq;
using BalambGarden.Engine.Census;
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
/// source word. If nothing is waiting AND no chain is driving, the line is a HUMAN's own hands,
/// and it becomes a receipt in its own right down the chat-identity path below - census and
/// history together, the way the fertilize path has always worked. If a chain IS driving, the
/// manual branch is closed to it by ruling 5 (2026-08-23) and the line is logged and dropped:
/// one character has one pair of hands, so a mid-run line our own desk could not match is a gap
/// in the desk, not evidence of a second gardener.</para>
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
                verdict = ChatLine.ClassifyObtain(logKind, fromLocalPlayer, text);

            if (verdict == ChatVerdict.None)
            {
                RememberCropName(logKind, message.SourceKind, text);
                NoteUnmatched(logKind, fromLocalPlayer, text);
                return;
            }

            // The obtain lane's extra witness: the ItemPayload rides the line (capture
            // 2026-08-26 - count in text, id in payload), and resolving it through the same
            // species join the Talk path uses gives the desk and the manual branch something
            // the generic obtain words cannot: what actually arrived.
            var itemSpecies = verdict == ChatVerdict.HarvestObtain
                ? ItemSpeciesOf(message) : (ushort)0;

            // The yield (Q3, ruled 2026-10-14): the payload names the item, the text carries the
            // count. Banked whichever branch closes the act; absent when the line gave neither.
            var yield = verdict == ChatVerdict.HarvestObtain ? YieldOf(message, text) : null;

            // The cross seed (ruled 2026-10-18): a seed-bag obtain line rides the same channel as
            // the crop line, a moment behind it, and belongs ON the harvest that is listening.
            // Asked before the pending desk so it can never close or become a harvest of its own.
            // Named edge: Garlic Cloves is both a crop's harvest and a seed item, so a garlic CROP
            // line inside another harvest's two-second window would be taken as that harvest's
            // seed - a manual harvest needs an interaction first, which makes the window
            // unreachable by hand.
            if (verdict == ChatVerdict.HarvestObtain && SeedLineOf(message, text) is { } seed)
            {
                if (CensusPump.OfferSeed(seed.Item, seed.Qty))
                    return;
                // Nothing listening. A seed item that is ALSO a crop (garlic) may be a harvest of
                // its own and goes on down the harvest path; a plain seed bag stops here, loud.
                if (itemSpecies == 0)
                {
                    Plugin.Log.Warning(
                        $"[ChatReceipts] seed line with no harvest listening (late, or a non-garden seed): '{text}'");
                    return;
                }
            }

            // The sow line names its bed (capture 2026-10-18): the close and the manual branch
            // both hold it against what they think was acted on.
            var lineBed = verdict == ChatVerdict.PlantBed ? ChatLine.ParseBedSow(text) : null;

            // The pending desk FIRST: a line that closes one of our own acts is that act's
            // confirmation and nothing else. Asking before the identity decode is deliberate -
            // the chain already knows exactly which bed it acted on, so running the
            // previous-target ladder against a chain's own line could only ever produce a
            // second, weaker answer to a question already settled.
            if (CensusPump.TryCloseChain(verdict, itemSpecies, yield, lineBed))
                return;

            // RULING 5 (2026-08-23): the manual branch never runs while a chain is driving. One
            // character, one pair of hands - a gardening line printed mid-run is OUR act by
            // elimination, so reading it as somebody's own hands would credit a second history
            // entry to the act already on the desk (the 17:28 field lap's double receipt). No
            // pending matched it, so there is nothing left to close either: it is logged, so the
            // mismatch can be read later, and dropped.
            if (Plugin.AnyChainDriving)
            {
                Plugin.Log.Information(
                    $"[ChatReceipts] {verdict} line while a chain is driving and no pending "
                    + "matched - dropped (one character, one pair of hands)");
                return;
            }

            var previous = Svc.Targets.PreviousTarget;
            if (verdict == ChatVerdict.HarvestObtain)
                OnManualHarvest(previous, itemSpecies, yield);
            else if (verdict == ChatVerdict.PlantBed)
                OnBedSow(previous, lineBed!.Value);
            else if (ChatLine.IsPotVerdict(verdict))
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
    /// evidence and is left un-banked rather than remembered as a zero.
    ///
    /// <para>FIRST LINE ONLY: the crop-name message carries a second text payload behind a
    /// newline (capture 2026-08-23, re-read at the 17:06 field lap after corroboration
    /// reported "no crop-name line" on a tend the capture said prints one - the whole
    /// TextValue never resolves, so the instrument sat inert). The name is the first
    /// line; whatever the game says after it is not the name's problem.</para></summary>
    private static void RememberCropName(int logKind, XivChatRelationKind sourceKind, string text)
    {
        if (!ChatLine.IsInspectLine(logKind, sourceKind == XivChatRelationKind.None))
            return;

        var cut = text.IndexOf('\n');
        var name = (cut >= 0 ? text[..cut] : text).Trim();
        if (Plugin.Tables.SpeciesIndexByName(name) is not { } species || species == 0)
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
        // BOTH success shapes, by the 2026-08-26 lesson: the harvest close rode LootNotice/62
        // and this instrument's 57-only aperture made it structurally deaf to the one line it
        // was built to catch - the mod harvest's pending sat open while the obtain line sailed
        // past unlogged. Any channel a close has ever ridden stays in the aperture.
        if (logKind != ChatLine.SystemMessageKind && logKind != ChatLine.LootNoticeKind)
            return;
        if (!fromLocalPlayer)
            return;
        if (CensusPump.OpenPendingKind is not { } kind)
            return;

        Plugin.Log.Information(
            $"[ChatReceipts] unmatched line while {kind} receipt open - candidate close text: '{text}'");
    }

    /// <summary>The manual harvest branch - an obtain line that closed no act of ours while no
    /// chain was driving. The obtain shape is GENERIC (loot rolls, quest rewards, lockboxes
    /// opened from inventory with no target at all), so unlike the 57 verbs this branch demands
    /// <see cref="HarvestCorroboration"/>'s two agreeing witnesses before anything is receipted:
    /// the ItemPayload's species and the last map read of the previous-target's container. A
    /// non-garden previous-target is ordinary gameplay here, not a finding - it goes at debug,
    /// where the fertilize branch's equivalent warns.</summary>
    private static void OnManualHarvest(IGameObject? previous, ushort itemSpecies, SyncYield? yield)
    {
        bool isPot;
        int potKey = -1;
        int patchOrdinal = 0, bedSlot = 0;
        ushort patchId = 0, containerSpecies;

        if (BedOf(previous) is { } bed)
        {
            isPot = false;
            patchOrdinal = bed.PatchOrdinal;
            bedSlot = bed.BedIndex;
            patchId = bed.PatchId;
            containerSpecies = KnownBedSpecies(bed.PatchOrdinal, bed.BedIndex);
        }
        else if (PotKeyOf(previous) is { } key)
        {
            isPot = true;
            potKey = key;
            containerSpecies = CensusPump.LastIndoor.TryGetValue(key, out var pot)
                ? pot.SpeciesIndex : (ushort)0;
        }
        else
        {
            Plugin.Log.Debug(
                "[ChatReceipts] obtain line with no bed or flowerpot on previous-target - not a harvest");
            return;
        }

        var (proceed, reason) = HarvestCorroboration.Check(itemSpecies, containerSpecies);
        if (!proceed)
        {
            // Every refusal at a garden target is LOUD, at least for the shakedown (ruled
            // 2026-08-28): a disagreement is the poisoning case caught in the act, and the two
            // absence cases are how a gate that refuses a REAL harvest - an alias gap, an
            // unread map - would surface as a pile in the log instead of hiding at info for
            // weeks (the fertilize corroboration sat inert since birth for exactly this class,
            // 08-23). Quiet the absence pair back down once the gate is field-proven. The
            // no-garden-target case above stays debug - that one is every loot roll in the game.
            Plugin.Log.Warning($"[ChatReceipts] manual harvest refused (fail closed): {reason}");
            return;
        }

        Plugin.Log.Information($"[ChatReceipts] manual harvest corroboration: {reason}");
        CensusPump.OnChatReceipt(
            SyncKind.Harvest, isPot, potKey, patchOrdinal, bedSlot, patchId,
            knownSpecies: itemSpecies, yields: yield is { } y ? [y] : null);
    }

    /// <summary>The obtain line as a yield row: the ItemPayload's id (capture 2026-08-26) and
    /// the count in the text ("You obtain 9 krakka roots." - an article counts as 1). Null when
    /// the line carried no payload: a count without an item is not a yield. OPEN CAPTURE
    /// QUESTION: whether a harvest ever prints more than one obtain line (crop + seed) - if it
    /// does, the second line is a lost yield row, never a phantom harvest, because the
    /// just-emptied container reads species 0 and the corroboration refuses it.</summary>
    private static SyncYield? YieldOf(IHandleableChatMessage message, string text)
    {
        var payload = message.Message.Payloads
            .OfType<Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload>()
            .FirstOrDefault();
        if (payload is null)
            return null;
        return new SyncYield(payload.ItemId, ObtainLine.Quantity(text) ?? 1);
    }

    /// <summary>The obtain line as a seed bag: the payload's item is a seed in the crop table.
    /// Null for anything else (crops, loot, venture yields).</summary>
    private static SyncYield? SeedLineOf(IHandleableChatMessage message, string text)
        => YieldOf(message, text) is { } y && Plugin.Tables.CropBySeedId(y.Item) is not null ? y : null;

    /// <summary>The species the obtained item resolves to, 0 when it resolves to none. The
    /// id rides the line's ItemPayload (capture 2026-08-26); the name comes off the game's own
    /// Item sheet; the join is the same one the Talk path uses, so its pigment stripping and
    /// receipted aliases apply here too. A flower's bouquet is an item that is not a species,
    /// and it resolves - honestly - to nothing.</summary>
    private static ushort ItemSpeciesOf(IHandleableChatMessage message)
    {
        var payload = message.Message.Payloads
            .OfType<Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload>()
            .FirstOrDefault();
        if (payload is null)
            return 0;

        var name = Chains.PlantFlow.ItemName(payload.ItemId);
        if (name.Length == 0)
            return 0;

        return Plugin.Tables.SpeciesIndexByName(name) ?? 0;
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

    /// <summary>The manual bed sow. Two identity witnesses, both required: the line's own words
    /// name the bed, and the previous-target must be that same bed. They disagree -> nothing
    /// (fail closed, loud). No species corroboration here, unlike the other bed verbs: the bed
    /// was empty a moment ago, so there is no crop name to agree with - the map names the seed
    /// after the line (<see cref="CensusPump.OnSowLine"/>).</summary>
    private static void OnBedSow(IGameObject? previous, (int PatchOrdinal, int BedSlot) named)
    {
        if (BedOf(previous) is not { } bed)
        {
            Plugin.Log.Warning(
                $"[ChatReceipts] sow line naming patch {named.PatchOrdinal + 1} bed {named.BedSlot + 1} "
                + "with no bed on previous-target - not receipted (fail closed)");
            return;
        }
        if (bed.PatchOrdinal != named.PatchOrdinal || bed.BedIndex != named.BedSlot)
        {
            Plugin.Log.Warning(
                $"[ChatReceipts] sow line names patch {named.PatchOrdinal + 1} bed {named.BedSlot + 1} but "
                + $"previous-target is patch {bed.PatchOrdinal + 1} bed {bed.BedIndex + 1} - not receipted (fail closed)");
            return;
        }

        Plugin.Log.Information(
            $"[ChatReceipts] bed sow: line and previous-target agree on patch {bed.PatchOrdinal + 1} bed {bed.BedIndex + 1}");
        CensusPump.OnSowLine(bed.PatchOrdinal, bed.BedIndex, bed.PatchId);
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
