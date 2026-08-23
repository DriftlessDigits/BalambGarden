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
/// append-only log cannot go back and correct one.</para></summary>
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
    byte Stage);

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
    /// the line from a pot somebody else's hands were in.</para>
    ///
    /// <para>A negative elapsed is not "very recent": the clock moved under us, and that is not
    /// evidence - the same reading <see cref="FertilizeCorroboration"/> gives it.</para></summary>
    public static bool Matches(PendingReceipt pending, ChatVerdict verdict, DateTimeOffset now)
    {
        if (verdict == ChatVerdict.None)
            return false;
        if (ChatLine.KindOf(verdict) != pending.Kind || ChatLine.IsPotVerdict(verdict) != pending.IsPot)
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
}
