using BalambGarden.Engine.Census;

namespace BalambGarden.Engine.Sync;

/// <summary>An act Balamb performed and has not yet heard the game confirm - the OPEN half of
/// the two-phase receipt (ratified 2026-08-23). Everything a history entry needs is captured
/// at open time, because the chain knows the identity and the chat line does not: the closing
/// line credits no object at all, so a pending that waited to be told who it was would have
/// nothing to ask.
///
/// <para>The census half is NOT here on purpose. Ledger state, feed strings and binding all
/// land immediately when the chain acts, exactly as before; only the append-only history waits,
/// because the honest source word - <see cref="SyncSource.Chain"/> or
/// <see cref="SyncSource.ChainUnconfirmed"/> - is not known until the window closes, and an
/// append-only log cannot go back and correct one.</para>
///
/// <para><paramref name="Delivered"/> is the ruling-4 consequence (2026-08-23): the pending now
/// opens AT THE ACT, before the chain's own receipt has run, so a pending can exist for an act
/// that never actually completed. The flag is the difference between "we acted and the census
/// landed, only the line is missing" and "we clicked and nothing came back at all" - and ruling
/// 6 reads it at the timeout to decide whether history hears about this act at all.</para></summary>
public sealed record PendingReceipt(
    SyncKind Kind,
    bool IsPot,
    DateTimeOffset OpenedAt,
    EstateKey Estate,
    int PatchOrdinal,
    int BedSlot,
    ushort PatchId,
    int PotKey,
    ushort Species,
    byte Stage,
    bool Delivered = false);

/// <summary>The join between an act and its confirmation: one pure rule set, no Dalamud types,
/// no game state, desk-testable (the receipt lifecycle, ratified 2026-08-23).
///
/// <para>THE SHAPE. A chain-driven action OPENS a pending; the game's own chat line CLOSES it
/// and the history entry is appended as <see cref="SyncSource.Chain"/> - confirmed. A chat line
/// that matches NO open pending is a human's own hands, and becomes a receipt in its own right
/// down the chat-identity path (previous-target + corroboration), as fertilize already did. A
/// pending nobody ever closes times out and is appended as
/// <see cref="SyncSource.ChainUnconfirmed"/>: today's chain-belief behaviour, kept, but labelled
/// honestly rather than passed off as a confirmation.</para>
///
/// <para>ONE PENDING IN FLIGHT, EVER. Chains are sequential - one bed at a time, one menu at a
/// time - so a second open means the first act's line never came. The desk force-closes the old
/// one as chain-unconfirmed before opening the new, so overlap cannot exist even at the pacing
/// boundary where a slow line and the next act would otherwise both be live. That is what lets
/// this matcher stay a rule about ONE pending instead of a queue with an ordering problem.</para>
///
/// <para>UNKNOWN LINES ARE NOT A FAILURE MODE, they are the capture instrument. Harvest and the
/// bed sow line have never been taped, so those acts will time out as chain-unconfirmed on
/// purpose until the adapter's unmatched-line logger prints their words.</para></summary>
public static class ReceiptLifecycle
{
    /// <summary>How long an act waits for its line. The capture put about 4 seconds between the
    /// menu click and the line landing, and chain pacing puts about 8 seconds between beds - so
    /// 6 clears the slow line and dies before the next act can be mistaken for its close.</summary>
    public static readonly TimeSpan CloseWindow = TimeSpan.FromSeconds(6);

    /// <summary>Whether this line closes this act. Three agreements, all required: same kind,
    /// same container, inside the window.
    ///
    /// <para>The container check is not redundant with the kind check - a pot fertilize and a
    /// bed fertilize are the same <see cref="SyncKind"/>, and the game's own sentence is the
    /// only thing that tells them apart. Requiring both is what stops a bed act being closed by
    /// the line from a pot somebody else's hands were in. The obtain line is the one sentence
    /// that names NO container (capture 2026-08-26), so for it the check has nothing to check
    /// and the pending's own container is the only claim standing -
    /// <see cref="ChatLine.NamesContainer"/> decides which reading applies.</para>
    ///
    /// <para><paramref name="itemSpecies"/> is the obtain lane's extra witness: the ItemPayload
    /// names what arrived. The shape is generic (venture yields and loot rolls ride it), so a
    /// resolvable item that CONTRADICTS the species the pending banked refuses the close - the
    /// pending then times out chain-unconfirmed, the honest word. 0 on either side is absence,
    /// and absence is not a mismatch: a flower's bouquet resolves to nothing, and the armed
    /// identity plus the window carry the claim alone.</para>
    ///
    /// <para>A negative elapsed is not "very recent": the clock moved under us, and that is not
    /// evidence - the same reading <see cref="FertilizeCorroboration"/> gives it.</para></summary>
    public static bool Matches(
        PendingReceipt pending, ChatVerdict verdict, DateTimeOffset now, ushort itemSpecies = 0)
    {
        if (verdict == ChatVerdict.None)
            return false;
        if (ChatLine.KindOf(verdict) != pending.Kind)
            return false;
        if (ChatLine.NamesContainer(verdict) && ChatLine.IsPotVerdict(verdict) != pending.IsPot)
            return false;
        if (itemSpecies != 0 && pending.Species != 0 && itemSpecies != pending.Species)
            return false;

        var elapsed = now - pending.OpenedAt;
        return elapsed >= TimeSpan.Zero && elapsed <= CloseWindow;
    }

    /// <summary>Whether this act has waited too long to still be closable. The exact edge -
    /// elapsed == <see cref="CloseWindow"/> - is NOT expired, so the window is inclusive at both
    /// ends and this is the exact complement of <see cref="Matches"/>'s timing test: no instant
    /// exists where a line would be refused as late while the sweep still called the pending
    /// live, or the reverse.</summary>
    public static bool Expired(PendingReceipt pending, DateTimeOffset now)
        => now - pending.OpenedAt > CloseWindow;

    /// <summary>What a pending nobody closed has earned - see <see cref="OnTimeout"/>.</summary>
    public enum TimeoutOutcome
    {
        /// <summary>The act happened and only its confirming line is missing: one honest history
        /// line under <see cref="SyncSource.ChainUnconfirmed"/>, exactly as before.</summary>
        WriteChainUnconfirmed,

        /// <summary>The act never landed: nothing is written at all.</summary>
        Cancel,
    }

    /// <summary>RULING 6 (2026-08-23): an UNDELIVERED pending cancels instead of writing.
    ///
    /// <para>Since ruling 4 the pending opens at the ACT, not at the delivery, so the desk now
    /// holds pendings for acts that may never have completed - a menu that closed under us, a
    /// sow the prompt refused, a harvest whose yield never arrived. Two absent witnesses are not
    /// a fact: no chain receipt AND no chat line means nobody saw this act happen, and a refused
    /// or failed act must not enter an append-only history that has no way to retract it.</para>
    ///
    /// <para>A DELIVERED pending is the old case unchanged - the chain's own receipt did land,
    /// the census already believes it, and only the game's sentence is missing - so it still
    /// writes, loudly labelled chain-unconfirmed rather than passed off as a confirmation.</para></summary>
    public static TimeoutOutcome OnTimeout(PendingReceipt pending)
        => pending.Delivered ? TimeoutOutcome.WriteChainUnconfirmed : TimeoutOutcome.Cancel;

    /// <summary>The desk's memory of the last pending a chat line actually closed. One slot,
    /// session-only: it answers a question about the last few seconds and nothing longer.</summary>
    public readonly record struct RecentClose(SyncKind Kind, bool IsPot, DateTimeOffset At);

    /// <summary>THE REVERSE RACE. A chain act whose delivery runs LATE - the pot paths wait on a
    /// map settle - can have its confirming line arrive first, close the pending, and write the
    /// history line before the chain's own receipt has run at all. When the delivery finally
    /// arrives it finds no pending open, and without this rule it would read that as the
    /// late-identity fallback and open a SECOND pending for an act already in history: one act,
    /// two entries, which is the 17:28 field receipt's failure wearing the other face.
    ///
    /// <para>Same kind, same container, inside the same <see cref="CloseWindow"/> the close half
    /// runs on - so the suppression is exactly as wide as the race it answers and no wider. A
    /// negative elapsed is not "very recent" here either: the clock moved under us, and
    /// <see cref="Matches"/> gives that the same reading.</para></summary>
    public static bool DeliveryAlreadyClosed(
        SyncKind kind, bool isPot, RecentClose? lastClose, DateTimeOffset now)
    {
        if (lastClose is not { } closed)
            return false;
        if (closed.Kind != kind || closed.IsPot != isPot)
            return false;

        var since = now - closed.At;
        return since >= TimeSpan.Zero && since <= CloseWindow;
    }
}
