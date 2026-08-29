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

    public static ForeignOutcome ApplyPage(
        LedgerStore ledger, IReadOnlyList<SyncReceipt> page, string ownUser, DateTimeOffset now)
    {
        int applied = 0, ownSkipped = 0, parked = 0, dropped = 0;

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
            if (!ledger.ForeignHold.Any(p => p.Receipt.Id == receipt.Id))
                ledger.ForeignHold.Add(new ParkedForeign(receipt, now));
        }

        Prune(ledger, now);
        return new ForeignOutcome(applied, ownSkipped, parked, dropped);
    }

    /// <summary>Re-runs the parking lot - called after census mints new entity ids. Returns
    /// how many landed.</summary>
    public static int RetryParked(LedgerStore ledger, DateTimeOffset now)
    {
        var landed = ledger.ForeignHold.Where(p => TryApply(ledger, p.Receipt)).ToList();
        foreach (var p in landed)
            ledger.ForeignHold.Remove(p);
        Prune(ledger, now);
        return landed.Count;
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
                ObserveOnce(bed, receipt, receipt.Data.Stage ?? 0);
                return true;

            case SyncKind.Plant:
                ObserveOnce(bed, receipt, stage: 0);
                return true;

            case SyncKind.Stage:
                ObserveOnce(bed, receipt, receipt.Data.Stage ?? 0);
                return true;

            case SyncKind.Ripe:
                ObserveOnce(bed, receipt, stage: 4);
                return true;

            case SyncKind.Fertilize:
                // Applied-as-noop: a feed is not a reading locally either (SourceFor returns
                // null for it). The named gap - the fed-check not hearing foreign feeds -
                // waits on its own ruling; the game's own refusal line is the backstop.
                return true;

            case SyncKind.PotWilt:
                // Applied-as-noop: pot wilt is map-published locally and warnings always
                // derive locally (design law) - a foreign wilt claim adds nothing a visit
                // does not already answer better.
                return true;

            default:
                return true;   // a future kind we matched but do not understand: absorbed, honest no-op
        }
    }

    /// <summary>The ring dedup that makes restore-replay safe: an identical external
    /// observation (same instant) is the same fact arriving twice, not two facts.</summary>
    private static void ObserveOnce(ClaimedBed bed, SyncReceipt receipt, byte stage)
    {
        if (bed.Ring.Any(o => o.Source == ObservationSource.External && o.At == receipt.At))
            return;
        bed.Observe(new Observation(
            receipt.At, receipt.Data.Species ?? 0, stage, ObservationSource.External));
    }
}
