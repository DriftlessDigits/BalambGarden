using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Sync;

/// <summary>One foreign receipt waiting for a local entity to exist (its estate not yet
/// censused under this phrase, or the entity genuinely orphaned). ParkedAt is LOCAL arrival
/// time and exists only for the prune - the receipt's own At stays the fact's time.</summary>
public sealed record ParkedForeign(SyncReceipt Receipt, DateTimeOffset ParkedAt);

/// <summary>What one page did: everything in the page lands in exactly one of the four
/// buckets. <see cref="Contradicted"/> counts older receipts that disagreed with the ledger
/// and so changed nothing (replant evidence worth a log line). <see cref="Rebases"/> and
/// <see cref="HistoryToRecord"/> are what the page did to the record and what it owes the
/// history; <see cref="CursorAdvanced"/> says whether <see cref="ForeignApply.AbsorbPage"/>
/// moved the inbox position (only after the history save succeeded).</summary>
public sealed record ForeignOutcome(
    int Applied, int OwnSkipped, int Parked, int Dropped, int Contradicted,
    IReadOnlyList<RebaseEvent> Rebases, IReadOnlyList<HistoryEntry> HistoryToRecord,
    string PageCursor, bool CursorAdvanced);

/// <summary>Applies household receipts to the local ledger (piece 4, ruled 2026-08-29;
/// rewritten for the tenancy record 2026-10-14).
///
/// <para>THE LAWS. A live map read still beats a wire fact everywhere the game speaks
/// (ruling 5). Derivations never branch on source; External is display vocabulary, not a
/// confidence tier (ruling 10). Own receipts skip by user id - they were applied when they
/// happened. THE RULE (2026-10-14): an older receipt never overwrites newer evidence - it adds
/// its evidence when it agrees and goes to history only when it contradicts; the fence refuses
/// anything at or before the last reset. Apply is IDEMPOTENT by construction, because a ledger
/// restore rewinds the cursor and deliberately replays the window (ruling 9): Plant at the same
/// instant is a no-op, Tend takes max(), Absorb folds min/max and never resets on older input,
/// Rebase refuses cuts older than the fence, history dedups by id.</para>
///
/// <para>HISTORY BEFORE CURSOR. A page's Plant and Harvest rows are owed to the permanent
/// history (ruled 2026-10-14, both hands). The inbox cursor advances only after that history
/// is saved, so a failed save means the same page is fetched again and re-recorded by id. No
/// access, no history: a receipt for an entity this install never censused parks and expires
/// without ever reaching history (review finding 8, ruled).</para></summary>
public static class ForeignApply
{
    /// <summary>Longer than the relay retains (21d), so nothing prunes while it could still
    /// re-arrive; short enough that orphans do not accumulate. Past it a receipt anchors
    /// nothing anyway - the longest grow is ~10 days (ruling 9's own cut).</summary>
    public static readonly TimeSpan ParkedLifetime = TimeSpan.FromDays(30);

    /// <summary>The whole inbox step, one call: apply the page, record what it owes the
    /// history, save the history, and only then advance the cursor. <paramref name="saveHistory"/>
    /// is the plugin's file write (true = on disk); it is asked only when the history is dirty.
    /// On a failed save the cursor holds and the ledger changes stand - they are idempotent on
    /// the re-delivery the held cursor guarantees.</summary>
    public static ForeignOutcome AbsorbPage(
        LedgerStore ledger, RelayPage page, string ownUser, DateTimeOffset now,
        GardenHistory history, Func<bool> saveHistory)
    {
        var outcome = ApplyPage(ledger, page.Receipts, ownUser, now);
        foreach (var entry in outcome.HistoryToRecord)
            history.Record(entry);

        var advanced = !history.Dirty || saveHistory();
        if (advanced)
            ledger.RelayCursor = page.Cursor;
        return outcome with { PageCursor = page.Cursor, CursorAdvanced = advanced };
    }

    /// <summary>Applies a page to the ledger only. Touches neither the cursor nor the history
    /// file - see <see cref="AbsorbPage"/> for the contract that does.</summary>
    public static ForeignOutcome ApplyPage(
        LedgerStore ledger, IReadOnlyList<SyncReceipt> page, string ownUser, DateTimeOffset now)
    {
        int applied = 0, ownSkipped = 0, parked = 0, dropped = 0, contradicted = 0;
        var rebases = new List<RebaseEvent>();
        var history = new List<HistoryEntry>();
        var byEntity = IndexByEntity(ledger);

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

            // Own receipts were applied when they happened. The literal "own" slug is refused
            // too: it is the local word for this install's hand, and a sender using it would
            // land its facts under our name in the history.
            if (user == ownUser || user == Hand.OwnSlug)
            {
                ownSkipped++;
                continue;
            }

            if (TryApply(byEntity, receipt, new Hand(user), rebases, history, ref contradicted))
            {
                applied++;
                continue;
            }

            parked++;
            if (heldIds.Add(receipt.Id))
                ledger.ForeignHold.Add(new ParkedForeign(receipt, now));
        }

        Prune(ledger, now);
        return new ForeignOutcome(applied, ownSkipped, parked, dropped, contradicted,
            rebases, history, ledger.RelayCursor, CursorAdvanced: false);
    }

    /// <summary>Re-runs the parking lot - after census mints new entity ids, and on every poll
    /// while anything is held (a row held back by a failed history save must not wait days for
    /// the next mint). A landed receipt leaves the hold ONLY after its history is saved;
    /// otherwise it stays parked and re-lands next time (TryApply is idempotent). Returns how
    /// many landed.</summary>
    public static int RetryParked(
        LedgerStore ledger, DateTimeOffset now, GardenHistory history, Func<bool> saveHistory,
        ICollection<RebaseEvent>? rebases = null)
    {
        if (ledger.ForeignHold.Count == 0)
            return 0;

        var byEntity = IndexByEntity(ledger);
        var landed = new HashSet<string>();
        var events = new List<RebaseEvent>();
        var entries = new List<HistoryEntry>();
        var contradicted = 0;

        foreach (var parked in ledger.ForeignHold)
        {
            if (!SyncId.TryParse(parked.Receipt.Id, out var user, out _, out _))
                continue;   // cannot happen - unparseable ids never park - but never throw here
            if (TryApply(byEntity, parked.Receipt, new Hand(user), events, entries, ref contradicted))
                landed.Add(parked.Receipt.Id);
        }

        foreach (var entry in entries)
            history.Record(entry);
        if (!history.Dirty || saveHistory())
            ledger.ForeignHold.RemoveAll(p => landed.Contains(p.Receipt.Id));

        if (rebases is not null)
            foreach (var ev in events)
                rebases.Add(ev);

        Prune(ledger, now);
        return landed.Count;
    }

    private static void Prune(LedgerStore ledger, DateTimeOffset now)
        => ledger.ForeignHold.RemoveAll(p => now - p.ParkedAt > ParkedLifetime);

    private static Dictionary<string, ClaimedBed> IndexByEntity(LedgerStore ledger)
    {
        var index = new Dictionary<string, ClaimedBed>();
        foreach (var bed in ledger.Beds)
            if (bed.WireEntity is { } entity)
                index.TryAdd(entity, bed);
        return index;
    }

    /// <summary>One receipt against the record. False = no local entity yet (parks). The
    /// receipt's own At is the fact's time and is never altered (pass-3 must-fix #2) - skew
    /// between installs is absorbed by the fence rules on the bed, not by clamping.</summary>
    private static bool TryApply(
        Dictionary<string, ClaimedBed> byEntity, SyncReceipt r, Hand hand,
        List<RebaseEvent> rebases, List<HistoryEntry> history, ref int contradicted)
    {
        if (!byEntity.TryGetValue(r.Entity, out var bed))
            return false;

        var species = r.Data.Species ?? 0;
        switch (r.Kind)
        {
            case SyncKind.Harvest:
                // The emptying witness, exactly as a local harvest receipt (ruled 08-28). A
                // harvest older than the fence is refused inside Rebase; it is still history.
                Push(rebases, bed.Rebase(RebaseReason.HarvestWitnessed, cut: r.At, by: hand));
                history.Add(HistoryEntry.FromForeign(r, bed, hand)!);
                return true;

            case SyncKind.Plant:
                // Older and conflicting -> ledger untouched; still history (pass-2 #4).
                Push(rebases, bed.Plant(r.At, species, r.Data.Stage ?? 0, hand, out _));
                history.Add(HistoryEntry.FromForeign(r, bed, hand)!);
                return true;

            case SyncKind.Tend:
            case SyncKind.PotWater:
                bed.Tend(r.At, hand, species);
                return true;

            case SyncKind.Stage:
            case SyncKind.Ripe:
            {
                var stage = r.Kind == SyncKind.Ripe ? GardenStage.Ripe : r.Data.Stage ?? 0;
                var outcome = bed.Absorb(r.At, species, stage, hand, out var ev);
                Push(rebases, ev);
                if (outcome == AbsorbOutcome.Contradicted)
                    contradicted++;
                return true;
            }

            default:
                // Absorbed, honest no-op. Fertilize: a feed is not a reading locally either;
                // the fed-check gap waits on its own ruling and the game's refusal line is the
                // backstop. PotWilt: wilt is map-published locally and warnings always derive
                // locally - a foreign claim adds nothing a visit does not answer better. A
                // future kind we matched but do not understand lands here too.
                return true;
        }
    }

    private static void Push(List<RebaseEvent> sink, RebaseEvent? ev)
    {
        if (ev is not null)
            sink.Add(ev);
    }
}
