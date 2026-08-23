namespace BalambGarden.Engine.Sync;

/// <summary>What a chat line is worth to the receipt parser. None is the overwhelming
/// majority and the safe default - a line the classifier does not recognise is a line
/// nothing happens for.</summary>
public enum ChatVerdict { None, FertilizeBed, FertilizePot }

/// <summary>The chat classifier: one pure function over (channel, who acted, words), no
/// Dalamud types, no game state, desk-testable. Built on the fertilize recon capture of
/// 2026-08-23 - the first chat verbs Balamb reads for receipts rather than for a debug dump.
///
/// <para>STRUCTURE BEATS VOCABULARY. The channel and the actor relation are filtered FIRST,
/// and only then are the two success sentences matched exactly. The words are the last and
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

    /// <summary>The bed success line, verbatim (capture 2026-08-23).</summary>
    public const string BedFertilizeText = "You fertilize the crop.";

    /// <summary>The pot success line, verbatim (capture 2026-08-23). Pots get their own
    /// sentence, which is why pot-ness is decided here and never guessed downstream.</summary>
    public const string PotFertilizeText = "Pomace added to flowerpot.";

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
            _ => ChatVerdict.None,
        };
    }

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
