using System;
using System.IO;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;

namespace BalambGarden;

/// <summary>The v2 spine: one ledger file, one census brain, one debug trail, one receipt log.
/// The POC ledger (Configuration.Ledger) is never read - fresh start by spec.</summary>
public sealed class GardenService
{
    private readonly string ledgerPath;
    private readonly string receiptsPath;

    public LedgerStore Ledger { get; }
    public CensusEngine Census { get; }
    public DebugTrail Trail { get; }
    public IWiltSource Wilt { get; } = new ClockWiltSource();

    /// <summary>The append-only history (schema ratified 2026-08-23). The ledger answers
    /// "what is in this bed now"; this answers "what happened", and it is the outbox sync
    /// sends from. Its own file, beside the ledger, because the two have different lifetimes:
    /// a parked ledger must not take the history with it, and vice versa.</summary>
    public ReceiptLog Receipts { get; }

    private GardenService(
        string ledgerPath, LedgerStore ledger, string trailPath,
        string receiptsPath, ReceiptLog receipts)
    {
        this.ledgerPath = ledgerPath;
        this.receiptsPath = receiptsPath;
        Ledger = ledger;
        Census = new CensusEngine(ledger);
        Trail = new DebugTrail(trailPath);
        Receipts = receipts;
    }

    public static GardenService Load(string configDirectory)
    {
        Directory.CreateDirectory(configDirectory);
        var ledgerPath = Path.Combine(configDirectory, "ledger-v2.json");
        var trailPath = Path.Combine(configDirectory, "trail.jsonl");
        var receiptsPath = Path.Combine(configDirectory, "receipts.json");

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

        // Same law for the history, and it matters more here: a parked receipt log is a WIPE,
        // and the era guard is built to see one (an empty era mints a fresh id-space rather
        // than counting on from numbers the relay may already hold).
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

        var service = new GardenService(ledgerPath, ledger, trailPath, receiptsPath, receipts);

        // Ledgers written before 08-15 hold one physical plot as two records (the house
        // interior had its own territory id). Idempotent: a clean file reports nothing.
        var report = LedgerMigration.NormalizeEstates(ledger);
        foreach (var note in report.Notes)
            Plugin.Log.Information($"[Garden] ledger migration: {note}");
        foreach (var warning in report.Warnings)
            Plugin.Log.Warning($"[Garden] ledger migration COULD NOT MERGE - {warning}");
        if (report.Changed)
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

    /// <summary>Writes the history, then refreshes config's memory of it. The two are one
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
