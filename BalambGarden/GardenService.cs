using System;
using System.IO;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;

namespace BalambGarden;

/// <summary>The v2 spine: one ledger file, one census brain, one debug trail.
/// The POC ledger (Configuration.Ledger) is never read - fresh start by spec.</summary>
public sealed class GardenService
{
    private readonly string ledgerPath;

    public LedgerStore Ledger { get; }
    public CensusEngine Census { get; }
    public DebugTrail Trail { get; }
    public IWiltSource Wilt { get; } = new ClockWiltSource();

    private GardenService(string ledgerPath, LedgerStore ledger, string trailPath)
    {
        this.ledgerPath = ledgerPath;
        Ledger = ledger;
        Census = new CensusEngine(ledger) { ClaimOnAction = Plugin.Configuration.ClaimOnAction };
        Trail = new DebugTrail(trailPath);
    }

    public static GardenService Load(string configDirectory)
    {
        Directory.CreateDirectory(configDirectory);
        var ledgerPath = Path.Combine(configDirectory, "ledger-v2.json");
        var trailPath = Path.Combine(configDirectory, "trail.jsonl");

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

        return new GardenService(ledgerPath, ledger, trailPath);
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
}
