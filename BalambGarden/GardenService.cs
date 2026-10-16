using System;
using System.Collections.Generic;
using System.IO;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;

namespace BalambGarden;

/// <summary>The v2 spine: one ledger file, one census brain, one debug trail, one receipt log,
/// and - since 2026-10-14 - one garden history. The POC ledger (Configuration.Ledger) is never
/// read - fresh start by spec.</summary>
public sealed class GardenService
{
    private readonly string ledgerPath;
    private readonly string receiptsPath;
    private readonly string historyPath;

    public LedgerStore Ledger { get; }
    public CensusEngine Census { get; }
    public DebugTrail Trail { get; }
    public IWiltSource Wilt { get; } = new ClockWiltSource();

    /// <summary>The append-only receipt log (schema ratified 2026-08-23). The ledger answers
    /// "what is in this bed now"; this answers "what did THIS install do", and it is the outbox
    /// sync sends from. Its own file, beside the ledger, because the two have different
    /// lifetimes: a parked ledger must not take the receipts with it, and vice versa.</summary>
    public ReceiptLog Receipts { get; }

    /// <summary>The garden history (ruled 2026-10-14): every plant and harvest, both hands,
    /// with yields, forever. Never the outbox, never pruned, born empty. Its own file for the
    /// same lifetime reason; written through <see cref="SaveHistory"/>, whose result gates the
    /// relay cursor (history before cursor - ForeignApply.AbsorbPage).</summary>
    public GardenHistory History { get; }

    private GardenService(
        string ledgerPath, LedgerStore ledger, string trailPath,
        string receiptsPath, ReceiptLog receipts, string historyPath, GardenHistory history)
    {
        this.ledgerPath = ledgerPath;
        this.receiptsPath = receiptsPath;
        this.historyPath = historyPath;
        Ledger = ledger;
        Census = new CensusEngine(ledger);
        Trail = new DebugTrail(trailPath);
        Receipts = receipts;
        History = history;
    }

    public static GardenService Load(string configDirectory)
    {
        Directory.CreateDirectory(configDirectory);
        var ledgerPath = Path.Combine(configDirectory, "ledger-v2.json");
        var trailPath = Path.Combine(configDirectory, "trail.jsonl");
        var receiptsPath = Path.Combine(configDirectory, "receipts.json");
        var historyPath = Path.Combine(configDirectory, "history.json");

        var ledger = new LedgerStore();
        if (File.Exists(ledgerPath))
        {
            try
            {
                ledger = LedgerStore.FromJson(File.ReadAllText(ledgerPath));
            }
            catch (Exception ex)
            {
                // Fail closed: never overwrite a file we could not read. Park it and start fresh.
                var parked = ledgerPath + $".unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                File.Move(ledgerPath, parked);
                Plugin.Log.Error($"[Garden] ledger unreadable ({ex.Message}) - parked at {parked}, starting fresh");
            }
        }

        // Same law for the receipt log, and it matters more here: a parked receipt log is a
        // WIPE, and the era guard is built to see one (an empty era mints a fresh id-space
        // rather than counting on from numbers the relay may already hold).
        var receipts = new ReceiptLog();
        if (File.Exists(receiptsPath))
        {
            try
            {
                receipts = ReceiptLog.FromJson(File.ReadAllText(receiptsPath));
            }
            catch (Exception ex)
            {
                var parked = receiptsPath + $".unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                File.Move(receiptsPath, parked);
                Plugin.Log.Error($"[Garden] receipt log unreadable ({ex.Message}) - parked at {parked}, starting fresh");
            }
        }

        // And for the history: park, never overwrite. Born empty on a fresh install and on the
        // first v3 load (Q4, ruled 2026-10-14: no back-fill from the receipt log).
        var history = new GardenHistory();
        if (File.Exists(historyPath))
        {
            try
            {
                history = GardenHistory.FromJson(File.ReadAllText(historyPath));
            }
            catch (Exception ex)
            {
                var parked = historyPath + $".unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                File.Move(historyPath, parked);
                Plugin.Log.Error($"[Garden] history unreadable ({ex.Message}) - parked at {parked}, starting fresh");
            }
        }

        var service = new GardenService(
            ledgerPath, ledger, trailPath, receiptsPath, receipts, historyPath, history);

        // v2 -> v3 (2026-10-14): the per-bed observation ring becomes the tenancy record. The
        // v2 file is copied aside ONCE before its first v3 write, because a v0.3.0 rollback
        // reading a v3 file would see empty rows and overwrite them on its next save.
        var migrated = false;
        if (ledger.Version < LedgerStore.CurrentVersion)
        {
            var backup = ledgerPath + ".pre-v3.bak";
            if (File.Exists(ledgerPath) && !File.Exists(backup))
            {
                File.Copy(ledgerPath, backup);
                Plugin.Log.Information($"[Garden] ledger v{ledger.Version} backed up to {backup} before the v3 fold");
            }
            var folded = LedgerMigration.FoldLegacyRings(ledger);
            Plugin.Log.Information(
                $"[Garden] ledger migration: folded {folded} observation ring(s) into tenancy records (v{LedgerStore.CurrentVersion})");
            migrated = true;
        }

        // Ledgers written before 08-15 hold one physical plot as two records (the house
        // interior had its own territory id). Idempotent: a clean file reports nothing. Runs
        // AFTER the ring fold so it merges records, not rings.
        var report = LedgerMigration.NormalizeEstates(ledger);
        foreach (var note in report.Notes)
            Plugin.Log.Information($"[Garden] ledger migration: {note}");
        foreach (var warning in report.Warnings)
            Plugin.Log.Warning($"[Garden] ledger migration COULD NOT MERGE - {warning}");
        if (report.Changed || migrated)
            service.Save();

        return service;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(ledgerPath, Ledger.ToJson());
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Garden] ledger save failed: {ex.Message}");
        }
    }

    /// <summary>Writes the receipt log, then refreshes config's memory of it. The two are one
    /// operation on purpose: the memory is the rewind/wipe/clone detector, and it only detects
    /// anything if it is CURRENT - a memory that lagged a session behind would read a healthy
    /// counter as a rewind and mint an era for nothing. Doing it here means no append path can
    /// forget.</summary>
    public void SaveReceipts()
    {
        try
        {
            File.WriteAllText(receiptsPath, Receipts.ToJson());
        }
        catch (Exception ex)
        {
            // Memory that outran the file would be a lie about a clean state that was never
            // written, so a failed write leaves the old memory standing.
            Plugin.Log.Error($"[Garden] receipt log save failed: {ex.Message}");
            return;
        }

        RememberEra();
    }

    /// <summary>Writes the history. Returns whether it is on disk - the relay cursor advances
    /// only on true (ForeignApply.AbsorbPage), so a failed write means the same page is fetched
    /// again and re-recorded by id. Never swallows silently: the caller needs the answer.</summary>
    public bool SaveHistory()
    {
        try
        {
            File.WriteAllText(historyPath, History.ToJson());
            History.MarkSaved();
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Garden] history save failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The ONE own-receipt seam: appends to the receipt log (the outbox) and, when the
    /// act is a plant or a harvest, to the garden history as well - the same fact, both files,
    /// so no append path can write one and forget the other. Throws exactly as
    /// <see cref="ReceiptLog.Append"/> does (no era yet); callers keep their own guard.</summary>
    public LoggedReceipt AppendOwn(
        SyncKind kind, SyncSource source, DateTimeOffset at, EstateKey estate, bool isPot,
        int patchOrdinal, int bedSlot, ushort patchId, int potKey, SyncData? data = null, ulong houseId = 0)
    {
        var entry = Receipts.Append(
            kind, source, at, estate, isPot,
            patchOrdinal: patchOrdinal, bedSlot: bedSlot, patchId: patchId, potKey: potKey,
            data: data, houseId: houseId);

        var ownUser = Plugin.Configuration.SyncUserId;
        if (ownUser.Length == 0)
        {
            // Cannot happen in practice - the user slug mints at load, before any era exists -
            // but a history id needs the real slug and never a placeholder.
            Plugin.Log.Warning($"[Garden] {kind} not written to history: no user slug minted yet");
            return entry;
        }

        if (History.Record(HistoryEntry.FromOwn(entry, ownUser)))
            SaveHistory();
        return entry;
    }

    /// <summary>Stamps the log's current era, counter and this machine into config - what
    /// "the last clean state" means to <see cref="EraGuard"/> at the next load. Separate files,
    /// restored separately, which is the whole reason the disagreement is ever visible.</summary>
    internal void RememberEra()
    {
        var config = Plugin.Configuration;
        config.SyncEraRemembered = Receipts.Era;
        config.SyncCounterRemembered = Receipts.Counter;
        config.SyncMachineHash = Sync.MachineStamp.Hash();
        config.Save();
    }
}
