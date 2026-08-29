using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Sync;

/// <summary>One foreign receipt waiting for a local entity to exist (its estate not yet
/// censused under this phrase, or the entity genuinely orphaned). ParkedAt is LOCAL arrival
/// time and exists only for the prune - the receipt's own At stays the fact's time.</summary>
public sealed record ParkedForeign(SyncReceipt Receipt, DateTimeOffset ParkedAt);

/// <summary>What one page did: everything in the page lands in exactly one bucket.</summary>
public sealed record ForeignOutcome(int Applied, int OwnSkipped, int Parked, int Dropped);

/// <summary>Applies household receipts to the local ledger (piece 4, ruled 2026-08-29).
///
/// <para>THE LAWS, all inherited: a foreign receipt fills only what the game never publishes
/// (watered-at, the emptying witness) - a live map read still beats it everywhere the game
/// speaks (ruling 5). Derivations never branch on source; External is display vocabulary,
/// not a confidence tier (ruling 10). Own receipts skip by user id - they were applied when
/// they happened. Apply is IDEMPOTENT by construction, because a ledger restore rewinds the
/// cursor and deliberately replays the window (ruling 9): the clock takes max(), the ring
/// dedupes on an identical external observation, Rebase on an empty row is a no-op.</para></summary>
public static class ForeignApply
{
    /// <summary>Longer than the relay retains (21d), so nothing prunes while it could still
    /// re-arrive; short enough that orphans do not accumulate. Past it a receipt anchors
    /// nothing anyway - the longest grow is ~10 days (ruling 9's own cut).</summary>
    public static readonly TimeSpan ParkedLifetime = TimeSpan.FromDays(30);

    /// <summary>The whole inbox step, one call: apply the page, then advance the cursor.
    /// Coupled here on purpose - never advancing past a receipt that did not land SOMEWHERE
    /// (applied, parked, skipped, or loudly dropped) is the cursor design's load-bearing
    /// invariant, and it should not depend on statement order in a poller callback.</summary>
    public static ForeignOutcome AbsorbPage(
        LedgerStore ledger, RelayPage page, string ownUser, DateTimeOffset now)
    {
        var outcome = ApplyPage(ledger, page.Receipts, ownUser, now);
        ledger.RelayCursor = page.Cursor;
        return outcome;
    }

    public static ForeignOutcome ApplyPage(
        LedgerStore ledger, IReadOnlyList<SyncReceipt> page, string ownUser, DateTimeOffset now)
    {
        int applied = 0, ownSkipped = 0, parked = 0, dropped = 0;

        // One pass over the hold up front: the cursor-0 full-window replay on a fresh
        // install parks everything, and an Any() per receipt over a growing list is the
        // quadratic shape that would make that one framework-thread callback crawl.
        var heldIds = ledger.ForeignHold.Select(p => p.Receipt.Id).ToHashSet();

        foreach (var receipt in page)
        {
            if (!SyncId.TryParse(receipt.Id, out var user, out _, out _))
            {
                // Unparseable can never dedupe against anything - parking it would retry
                // forever. Dropping is honest: the count is the instrument.
                dropped++;
                continue;
            }

            if (user == ownUser)
            {
                ownSkipped++;
                continue;
            }

            if (TryApply(ledger, receipt))
            {
                applied++;
                continue;
            }

            parked++;
            if (heldIds.Add(receipt.Id))
                ledger.ForeignHold.Add(new ParkedForeign(receipt, now));
        }

        Prune(ledger, now);
        return new ForeignOutcome(applied, ownSkipped, parked, dropped);
    }

    /// <summary>Re-runs the parking lot - called after census mints new entity ids. Returns
    /// how many landed. (RemoveAll's predicate applies as it tests - one pass, and a landed
    /// receipt leaves the hold in the same breath.)</summary>
    public static int RetryParked(LedgerStore ledger, DateTimeOffset now)
    {
        var landed = ledger.ForeignHold.RemoveAll(p => TryApply(ledger, p.Receipt));
        Prune(ledger, now);
        return landed;
    }

    private static void Prune(LedgerStore ledger, DateTimeOffset now)
        => ledger.ForeignHold.RemoveAll(p => now - p.ParkedAt > ParkedLifetime);

    private static bool TryApply(LedgerStore ledger, SyncReceipt receipt)
    {
        var bed = ledger.Beds.FirstOrDefault(b => b.WireEntity == receipt.Entity);
        if (bed is null)
            return false;

        switch (receipt.Kind)
        {
            case SyncKind.Harvest:
                // The emptying witness, exactly as a local harvest receipt (ruled 08-28):
                // contents and clocks leave, the row stays. Idempotent on an empty row.
                bed.Rebase();
                return true;

            case SyncKind.Tend:
            case SyncKind.PotWater:
                if (bed.LastTended is null || receipt.At > bed.LastTended)
                    bed.LastTended = receipt.At;
                ObserveOnce(bed, receipt);
                return true;

            case SyncKind.Plant:
            case SyncKind.Stage:
            case SyncKind.Ripe:
                ObserveOnce(bed, receipt);
                return true;

            default:
                // Absorbed, honest no-op. Fertilize: a feed is not a reading locally either
                // (SourceFor returns null); the fed-check gap waits on its own ruling and
                // the game's refusal line is the backstop. PotWilt: wilt is map-published
                // locally and warnings always derive locally - a foreign claim adds nothing
                // a visit does not answer better. A future kind we matched but do not
                // understand lands here too.
                return true;
        }
    }

    /// <summary>The ring write, deduped through the SAME rule the display reads
    /// (<see cref="ClaimedBed.HasExternalAt"/>): an identical external observation is the
    /// same fact arriving twice, not two facts - what makes restore-replay safe. Stage:
    /// Ripe IS stage 4 (the shared constant); otherwise what the sender knew, 0 = unknown.</summary>
    private static void ObserveOnce(ClaimedBed bed, SyncReceipt receipt)
    {
        if (bed.HasExternalAt(receipt.At))
            return;
        var stage = receipt.Kind == SyncKind.Ripe
            ? GardenStage.Ripe
            : receipt.Data.Stage ?? 0;
        bed.Observe(new Observation(
            receipt.At, receipt.Data.Species ?? 0, stage, ObservationSource.External));
    }
}
