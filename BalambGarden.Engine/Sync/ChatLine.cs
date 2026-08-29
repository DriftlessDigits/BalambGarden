namespace BalambGarden.Engine.Sync;

/// <summary>What a chat line is worth to the receipt parser. None is the overwhelming
/// majority and the safe default - a line the classifier does not recognise is a line
/// nothing happens for.
///
/// <para>The three verdicts after the fertilize pair were added for the receipt lifecycle
/// (ratified 2026-08-23) off the same day's field capture; HarvestObtain off the 2026-08-26
/// harvest capture. APPENDED, never reordered: the fertilize pair keeps its numbers, the way
/// <see cref="Census.ReceiptVerb"/> does, so a value that ever reaches a persisted surface
/// means the same thing forever.</para></summary>
public enum ChatVerdict { None, FertilizeBed, FertilizePot, PlantPot, TendBed, WaterPot, HarvestObtain }

/// <summary>The chat classifier: one pure function over (channel, who acted, words), no
/// Dalamud types, no game state, desk-testable. Built on the fertilize recon capture of
/// 2026-08-23 - the first chat verbs Balamb reads for receipts rather than for a debug dump -
/// and widened the same day to the plant/tend/water lines the receipt lifecycle closes on.
///
/// <para>STRUCTURE BEATS VOCABULARY. The channel and the actor relation are filtered FIRST,
/// and only then are the success sentences matched exactly. The words are the last and
/// weakest evidence: they are localised, they get reworded by patches, and a player can type
/// any of them into a chat box. The structure cannot be typed - a SystemMessage attributed
/// to the local player is the game speaking about something the player's own character just
/// did. An exact match rather than Contains for the same reason: "You fertilize the crop."
/// inside a longer sentence is somebody quoting us, not the game reporting.</para>
///
/// <para>REFUSALS ARE DELIBERATELY NOT A VERDICT. The capture also holds
/// "Additional pomace is not necessary in this flowerpot." and "This crop has already been
/// sufficiently fertilized." - both arrive as ErrorMessage (60) with no actor at all
/// (<see cref="ErrorMessageKind"/> is here so a future reader can see they were considered).
/// A refusal is the game declining to act, so there is nothing to receipt: writing one down
/// would put a fertilize in the history that never happened and hand a wrong "already fed"
/// answer to every pacing question afterward.</para></summary>
public static class ChatLine
{
    /// <summary>The channel both fertilize successes ride, numeric 57. Kept as a bare int so
    /// the Engine never takes a Dalamud reference for it - the adapter casts its own enum
    /// down at the boundary.</summary>
    public const int SystemMessageKind = 57;

    /// <summary>The refusal channel, numeric 60. Named but never matched - see the type
    /// summary: refusals are not actions.</summary>
    public const int ErrorMessageKind = 60;

    /// <summary>The loot channel, numeric 62 - where the harvest close line actually lands
    /// (capture 2026-08-26: 'You obtain 9 krakka roots.' arrived as LootNotice with a local
    /// player attribution and an ItemPayload, NOT as SystemMessage/57; the 57-only aperture is
    /// why the shipped self-capture never heard the one line it was built to catch).</summary>
    public const int LootNoticeKind = 62;

    /// <summary>The bed success line, verbatim (capture 2026-08-23).</summary>
    public const string BedFertilizeText = "You fertilize the crop.";

    /// <summary>The pot success line, verbatim (capture 2026-08-23). Pots get their own
    /// sentence, which is why pot-ness is decided here and never guessed downstream.</summary>
    public const string PotFertilizeText = "Pomace added to flowerpot.";

    /// <summary>The pot sow line, verbatim (capture 2026-08-23). The bed sow line is NOT KNOWN
    /// yet - a bed plant therefore opens a pending that times out as chain-unconfirmed until
    /// the unmatched-line logger hands us its words.</summary>
    public const string PotPlantText = "Seeds planted in flowerpot.";

    /// <summary>The bed tend line, verbatim (capture 2026-08-23). The tape's only tend
    /// variation is pot-vs-bed; whether a wilted crop prints something else is a GUESS and is
    /// deliberately not invented here - an unrecognised line is None, and the lifecycle's
    /// unmatched-line logger is what will capture it.</summary>
    public const string BedTendText = "This crop is doing well.";

    /// <summary>The pot tend line, verbatim (capture 2026-08-23). Tending a pot IS watering it
    /// - the game offers no separate verb - so this maps to
    /// <see cref="SyncKind.PotWater"/>, which is the kind the schema ratified for it.</summary>
    public const string PotWaterText = "The crops appear to be healthy.";

    /// <summary>Rules on one chat line.</summary>
    /// <param name="logKind">The chat channel, numeric.</param>
    /// <param name="fromLocalPlayer">Whether the game attributed the line to the local
    /// player's own character. A fertilize somebody else performed in earshot prints the
    /// same words under a different relation, and it is not our receipt to write.</param>
    /// <param name="text">The line's plain text.</param>
    public static ChatVerdict Classify(int logKind, bool fromLocalPlayer, string text)
    {
        if (logKind != SystemMessageKind || !fromLocalPlayer)
            return ChatVerdict.None;

        return text switch
        {
            BedFertilizeText => ChatVerdict.FertilizeBed,
            PotFertilizeText => ChatVerdict.FertilizePot,
            PotPlantText => ChatVerdict.PlantPot,
            BedTendText => ChatVerdict.TendBed,
            PotWaterText => ChatVerdict.WaterPot,
            _ => ChatVerdict.None,
        };
    }

    /// <summary>The obtain lane - the harvest close (capture 2026-08-26). A separate function
    /// rather than a wider <see cref="Classify"/> because the two lanes match on OPPOSITE
    /// principles: the 57 catalog is exact words on a fixed channel, this is a fixed channel
    /// with words that VARY by count and crop ('You obtain 9 krakka roots.') - so the text is
    /// only asked for its shape, never its vocabulary. One line lands in one lane, decided by
    /// the channel; the obtain words on 57 are nothing here, the same way a 57 sentence is
    /// nothing to Classify on 62.
    ///
    /// <para>NOTE WHAT THIS DOES NOT DECIDE. The shape is generic - venture yields, loot rolls
    /// and quest rewards ride it too - and the line names no container. Classification only
    /// says "an obtain-shaped line from our own hands"; whether it is a HARVEST is the desk's
    /// question (an open Harvest pending, chain branch) or the corroboration's
    /// (<see cref="HarvestCorroboration"/>, manual branch).</para></summary>
    public static ChatVerdict ClassifyObtain(int logKind, bool fromLocalPlayer, string text)
        => logKind == LootNoticeKind && fromLocalPlayer && Census.ObtainLine.IsObtain(text)
            ? ChatVerdict.HarvestObtain
            : ChatVerdict.None;

    /// <summary>The kind a verdict is a receipt FOR - the join the lifecycle closes on
    /// (ratified 2026-08-23). Pure and here rather than in the adapter because the mapping is
    /// the same fact the classifier already knows: the words named the act.</summary>
    /// <exception cref="ArgumentOutOfRangeException">None is not an act, so it has no kind.
    /// Asking is a caller bug, not a runtime condition - a None line is dropped before this.
    /// </exception>
    public static SyncKind KindOf(ChatVerdict v) => v switch
    {
        ChatVerdict.FertilizeBed or ChatVerdict.FertilizePot => SyncKind.Fertilize,
        ChatVerdict.PlantPot => SyncKind.Plant,
        ChatVerdict.TendBed => SyncKind.Tend,
        ChatVerdict.WaterPot => SyncKind.PotWater,
        ChatVerdict.HarvestObtain => SyncKind.Harvest,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "No receipt kind for this verdict"),
    };

    /// <summary>Whether the verdict's own sentence named a container at all. Every 57-catalog
    /// sentence does ("...in this flowerpot", "This crop...") - the obtain line does not: 'You
    /// obtain 9 krakka roots.' is silent on bed-vs-pot, so the lifecycle's container check has
    /// nothing to check and must not invent an answer (capture 2026-08-26).</summary>
    /// <exception cref="ArgumentOutOfRangeException">None named nothing at all.</exception>
    public static bool NamesContainer(ChatVerdict v) => v switch
    {
        ChatVerdict.HarvestObtain => false,
        ChatVerdict.FertilizeBed or ChatVerdict.FertilizePot or ChatVerdict.PlantPot
            or ChatVerdict.TendBed or ChatVerdict.WaterPot => true,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "No sentence behind this verdict"),
    };

    /// <summary>Whether the verdict's own sentence says a POT was acted on. The game names the
    /// container in its words, which is why container-ness is decided at the classifier and
    /// never guessed downstream - and why the lifecycle can refuse to close a bed pending on a
    /// pot line even when the kinds agree.</summary>
    /// <exception cref="ArgumentOutOfRangeException">None names no container - and neither
    /// does HarvestObtain (<see cref="NamesContainer"/>), so asking either is a caller bug.
    /// </exception>
    public static bool IsPotVerdict(ChatVerdict v) => v switch
    {
        ChatVerdict.FertilizePot or ChatVerdict.PlantPot or ChatVerdict.WaterPot => true,
        ChatVerdict.FertilizeBed or ChatVerdict.TendBed => false,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "No container for this verdict"),
    };

    /// <summary>Whether this line could be the crop-name line the game prints when a planted
    /// BED is interacted with - the corroboration material for
    /// <see cref="FertilizeCorroboration"/> (capture 2026-08-23: text='Royal Kukuru Bean' at
    /// zone 641, SystemMessage/57, no actor attribution, colour payloads wrapped around the
    /// name). This says only "the shape is right"; whether the words name a real crop is the
    /// adapter's question, because the species table lives outside this classifier.
    ///
    /// <para>Unattributed rather than local-player: the game is naming a thing, not reporting
    /// an act, so it credits nobody. That is also what keeps this from ever colliding with
    /// <see cref="Classify"/> - the two shapes are mutually exclusive on the same line.</para></summary>
    public static bool IsInspectLine(int logKind, bool unattributed)
        => logKind == SystemMessageKind && unattributed;
}

/// <summary>The bed anti-poisoning check (ruled from the 2026-08-23 capture analysis).
///
/// <para>THE HAZARD: a fertilize line credits no object, so identity comes from the
/// previous-target - the hard target is already cleared by the time the line prints. Fast
/// retargeting can replace that within a couple of seconds, so the previous-target can name
/// a DIFFERENT bed than the one that was fed. The kind-vs-object cross-check (a bed line
/// must find a bed) catches the wild cases; it cannot catch bed-for-bed.</para>
///
/// <para>THE CORROBORATION: interacting with a planted bed prints the crop's name to chat
/// as its own line just before the menu opens. If that name disagrees with what we know is
/// growing in the bed the previous-target named, the two are not the same bed and nothing is
/// receipted. ASYMMETRIC BY THE GAME'S DESIGN, not by ours: pots print no such line - their
/// species only ever appears inside an addon - so pot receipts run on the cross-check alone
/// and never come through here.</para>
///
/// <para>ABSENCE IS NOT A MISMATCH. No recent name line, a stale one, or a bed whose species
/// we do not know (unbound patch, unread map) all PROCEED. Only a live contradiction stops a
/// receipt. Demanding corroboration instead would silently kill every receipt at an unbound
/// patch, which is exactly where the early ones happen.</para></summary>
public static class FertilizeCorroboration
{
    /// <summary>How long a crop-name line stays evidence. The name prints as the menu opens
    /// and the fertilize lands a few interactions later, so this covers a human's pace at one
    /// bed with room to spare - and expires well before the next bed's own name line would
    /// have replaced it anyway.</summary>
    public static readonly TimeSpan InspectWindow = TimeSpan.FromSeconds(10);

    /// <summary>Rules on whether a bed fertilize may be receipted.</summary>
    /// <param name="inspectedSpecies">Species from the most recent crop-name line, 0 if none
    /// was ever resolved.</param>
    /// <param name="inspectedAt">When that line landed, or null if there has not been one.</param>
    /// <param name="bedSpecies">What we believe is growing in the bed the previous-target
    /// named, 0 when unknown (unbound patch, unread map, empty bed).</param>
    /// <param name="now">The moment the fertilize line landed.</param>
    /// <returns>Proceed, plus the reason - the reason is logged either way, so a field report
    /// says which branch fired rather than leaving it to be re-derived.</returns>
    public static (bool Proceed, string Reason) Check(
        ushort inspectedSpecies, DateTimeOffset? inspectedAt, ushort bedSpecies, DateTimeOffset now)
    {
        if (inspectedAt is null || inspectedSpecies == 0)
            return (true, "no crop-name line to corroborate against");

        var elapsed = now - inspectedAt.Value;
        // A negative elapsed means the clock moved under us; that is not evidence either.
        if (elapsed > InspectWindow || elapsed < TimeSpan.Zero)
            return (true, "crop-name line too old to corroborate against");

        if (bedSpecies == 0)
            return (true, "bed species unknown - nothing to contradict");

        return inspectedSpecies == bedSpecies
            ? (true, "crop name agrees with the bed")
            : (false, "crop name disagrees with the bed");
    }
}

/// <summary>The manual-harvest check (built from the 2026-08-26 harvest capture) - MANDATORY
/// two-witness agreement, deliberately stricter than <see cref="FertilizeCorroboration"/>'s
/// absence-proceeds.
///
/// <para>THE ASYMMETRY HAS A RECEIPT. The fertilize sentences are garden-specific words
/// nothing else ever prints, so their weak point is only WHICH bed - absence of corroboration
/// can proceed. The obtain shape is the game's generic loot channel: quest rewards, coffers,
/// and a from-inventory lockbox that needs NO target ride the same structure, so a stale bed
/// or pot on previous-target plus an absence-proceeds rule would mint phantom harvests.
/// Here BOTH witnesses must speak and agree: the line's ItemPayload names what arrived, the
/// last map read names what grew in the previous-target's container, and only their agreement
/// is a harvest. Anything less is logged and dropped.</para>
///
/// <para>NAMED COST: manual FLOWER harvests never receipt - their obtain names an item
/// ("bouquet of red sunflowers") that resolves to no species. A mod flower harvest still
/// closes through its pending, where the armed identity carries the claim. Reopen trigger:
/// a receipted bouquet-to-species join (TalkAliases is the reveal-never-invent home).</para></summary>
public static class HarvestCorroboration
{
    /// <summary>Rules on whether a manual harvest may be receipted.</summary>
    /// <param name="itemSpecies">The species the obtained item resolved to, 0 when it
    /// resolved to none (not a crop item, or a flower bouquet).</param>
    /// <param name="containerSpecies">What the last map read said grew in the container the
    /// previous-target named, 0 when unknown (unread side, unbound patch, empty).</param>
    public static (bool Proceed, string Reason) Check(ushort itemSpecies, ushort containerSpecies)
    {
        if (itemSpecies == 0)
            return (false, "obtained item resolves to no crop species - not receipted (generic obtain line)");
        if (containerSpecies == 0)
            return (false, "container species unknown - one witness is not enough on a generic line");

        return itemSpecies == containerSpecies
            ? (true, "obtained item agrees with the container's crop")
            : (false, "obtained item disagrees with the container's crop");
    }
}
