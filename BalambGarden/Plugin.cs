using System;
using Dalamud.Game.Command;
using ECommons;
using ECommons.DalamudServices;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using BalambGarden.Windows;

namespace BalambGarden;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/garden";

    public static Configuration Configuration { get; private set; } = null!;

    public static BalambGarden.Engine.Domain.DomainTables Tables { get; private set; } = null!;

    /// <summary>The v2 spine (ledger file + census brain + trail), loaded at startup.</summary>
    public static GardenService Garden { get; private set; } = null!;

    internal Chains.TendChain TendChain { get; init; }
    internal Chains.CycleChain CycleChain { get; init; }
    internal Chains.PotChain PotChain { get; init; }

    /// <summary>One chain at a time. They all drive the same menus through the same
    /// character - two running at once would interleave clicks into a conversation
    /// neither of them is having.</summary>
    internal bool AnyChainBusy => TendChain.Busy || CycleChain.Busy || PotChain.Busy;

    /// <summary>The one live plugin, for the static receipt paths that need to ask a question
    /// only the instance can answer. Nulled on dispose so a hot-reload never leaves a stale
    /// object answering for chains that no longer exist.</summary>
    private static Plugin? instance;

    /// <summary>Whether a chain is driving the character RIGHT NOW, reachable from the static
    /// chat handler (ruling 5, 2026-08-23: the manual branch never runs while a chain is driving
    /// - one character, one pair of hands, so a line that arrives mid-run is ours whether or not
    /// a pending happened to match it).
    ///
    /// <para>A pass-through rather than a cached flag on purpose: a mirrored bool refreshed on
    /// the framework tick would answer for the frame before the one the chat line landed in, and
    /// the whole point of this gate is the boundary. No instance means no chains, which honestly
    /// reads as nothing driving.</para></summary>
    internal static bool AnyChainDriving => instance?.AnyChainBusy ?? false;

    /// <summary>Whichever chain the run log should be showing: the one that is running,
    /// or the last one launched once it finishes (its outcome is the thing to read).</summary>
    internal Chains.ChainBase ActiveChain
        => TendChain.Busy ? TendChain
            : CycleChain.Busy ? CycleChain
            : PotChain.Busy ? PotChain
            : lastLaunched ?? TendChain;

    private Chains.ChainBase? lastLaunched;

    /// <summary>Call at every launch site, so the run log follows the player's last press.</summary>
    internal void Launched(Chains.ChainBase chain)
    {
        lastLaunched = chain;
        RunLogWindow.IsOpen = true;
    }

    public readonly WindowSystem WindowSystem = new("BalambGarden");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    internal RunLogWindow RunLogWindow { get; init; }

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // The Engine formats time; the app owns the preference. Set once here, and the
        // config window re-sets it on change.
        BalambGarden.Engine.Derivations.WindowFormat.TwelveHourClock = Configuration.TwelveHourClock;

        Tables = BalambGarden.Engine.Domain.DomainTables.Load();
        Log.Information($"[Engine] domain tables loaded: sunflower check = {Tables.SpeciesName(103)}");

        Log.Information($"build {BuildStamp.Line}");

        Garden = GardenService.Load(PluginInterface.GetPluginConfigDirectory());

        // Wired here rather than inside Load because the identity lives half in config and
        // half in the receipt log, and this is the first point where both exist.
        WireSyncIdentity();

        TendChain = new Chains.TendChain();
        CycleChain = new Chains.CycleChain();
        PotChain = new Chains.PotChain();

        // Only now: the chains the accessor answers about have to exist before anything can be
        // told they are idle.
        instance = this;

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        RunLogWindow = new RunLogWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(RunLogWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Balamb Garden window"
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        // The census heartbeat rides the framework tick (self-throttled inside).
        Svc.Framework.Update += OnFrameworkUpdate;

        // Harvest has no closing dialogue - the chat obtain line is its only completion
        // signal (capture 2026-08-15 F4), so the listener lives as long as the plugin does.
        Game.ObtainWatch.Start();

        // The chat line is the game's own confirmation of a gardening act: it closes a chain's
        // pending receipt, and it IS the whole event when a human acted by hand (receipt
        // lifecycle, ratified 2026-08-23). Either way the listener lives as long as the plugin
        // does. Not debug-gated: the receipt it writes is the product.
        Game.ChatReceipts.Start();

        // A zone line pulls the next relay poll close (piece 3's zone-in cadence) - the
        // moment you arrive somewhere is the moment a foreign receipt would matter.
        Svc.ClientState.TerritoryChanged += OnZoneLine;

#if DEBUG
        // Scoped recording: the persisted flag means "arm near pots", and a rezone ends
        // the scene a recording was scoped to. The proximity check in OnFrameworkUpdate
        // does the arming, so a hot-load next to a pot re-arms within seconds.
        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
#endif

        // Add a simple message to the log with level set to information
        // Use /xllog to open the log window in-game
        // Example Output: 00:57:54.959 | INF | [BalambGarden] ===A cool log message from Balamb Garden===
        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
    }

    public void Dispose()
    {
        // FIRST, before a single subscriber comes off: the chat handler is still live for the
        // rest of this method, and it must not be asking a half-torn-down plugin about chains.
        instance = null;

        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.ClientState.TerritoryChanged -= OnZoneLine;
#if DEBUG
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
#endif
        Game.ObtainWatch.Stop();
        Game.ChatReceipts.Stop();
#if DEBUG
        // A watcher left registered would outlive the plugin its callback belongs to.
        Chains.PlantFlow.StopWatching();
        Game.ChatRecon.StopWatching();
#endif

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        TendChain.Dispose();
        CycleChain.Dispose();
        PotChain.Dispose();

        Garden.Save();
        ECommonsMain.Dispose();
    }

    /// <summary>Opens the id-space for this install (identity amendment, ruled 2026-08-23).
    /// Two jobs, both once per launch: mint the user slug if this install has never had one,
    /// and rule on whether the receipt log's era may keep counting.
    ///
    /// <para>ERAS ARE CHEAP, COLLISIONS ARE NOT. A spurious mint costs one slug and a counter
    /// restart; a missed mint puts two different facts on the relay under one id, which cannot
    /// be undone. EraGuard fails toward minting for that reason, and the verdict's reason is
    /// logged either way so a field report says WHICH trigger fired rather than leaving it to
    /// be guessed at from a changed slug.</para></summary>
    private static void WireSyncIdentity()
    {
        if (Configuration.SyncUserId.Length == 0)
        {
            Configuration.SyncUserId = BalambGarden.Engine.Sync.SyncId.NewSlug();
            Configuration.Save();
            Log.Information("[Garden] minted this install's sync user id");
        }

        // An empty remembered era is NO MEMORY, not a memory of nothing - the guard's own
        // "no memory" branch mints, which is the right answer for a config that was wiped.
        var remembered = Configuration.SyncEraRemembered.Length == 0
            ? null
            : new BalambGarden.Engine.Sync.EraMemory(
                Configuration.SyncEraRemembered,
                Configuration.SyncCounterRemembered,
                Configuration.SyncMachineHash);

        // The v2 upgrade is one of EraGuard's own mint reasons (QA pass 2026-08-29 - every
        // way a counter's life ends lives in that one tested rule); this side only stamps
        // the version once the guard has ruled, so the reason fires exactly once.
        var (verdict, reason) = BalambGarden.Engine.Sync.EraGuard.Check(
            remembered, Garden.Receipts.Era, Garden.Receipts.Counter, Sync.MachineStamp.Hash(),
            Garden.Receipts.Version);
        Log.Information($"[Garden] era check: {reason}");

        if (verdict == BalambGarden.Engine.Sync.EraVerdict.MintFresh)
        {
            var era = BalambGarden.Engine.Sync.SyncId.NewSlug();
            Garden.Receipts.MintEra(era);
            if (Garden.Receipts.Version < 2)
            {
                Garden.Receipts.Version = 2;
                Log.Information(
                    "[Garden] receipt log upgraded to v2 - pre-upgrade entries stay local "
                    + "history and never sync (they carry no house id)");
            }
            Log.Information($"[Garden] minted era '{era}' - the counter restarts under it");
            Garden.SaveReceipts();   // writes the log, then refreshes the memory with it
        }
        else
        {
            // Nothing changed in the log, but the memory still gets stamped: the machine hash
            // and counter must describe THIS load, or the next one compares against a stale
            // clean state.
            Garden.RememberEra();
        }
    }

    private static void OnZoneLine(uint territory) => Sync.RelayPoller.NoteZoneIn();

    /// <summary>Nothing sensed before the player exists: no character, no estate,
    /// no census. The pump throttles itself past this gate.</summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!PlayerState.IsLoaded)
            return;
        Game.CensusPump.Tick();
        Sync.RelaySender.Tick();
        Sync.RelayPoller.Tick();
#if DEBUG
        AutoArmWatcher();
#endif
    }

#if DEBUG
    /// <summary>How close to a pot counts as "about to garden" (Drift's spec, 08-15).
    /// Internal so the recon readout can quote the real number, not a copy of it.</summary>
    internal const float WatcherArmRangeY = 4.6f;

    private DateTime nextWatcherCheckUtc = DateTime.MinValue;

    /// <summary>Scoped recording (Drift's ruling 08-15): with the config flag on, the
    /// plant-flow watcher arms itself when a pot is within reach and disarms on every
    /// rezone/teleport - so captures hold gardening, not a night of quest dialogue.
    /// Debug builds only; Release has no watcher at all.</summary>
    private void AutoArmWatcher()
    {
        if (!Configuration.WatchPlantFlow || Chains.PlantFlow.Watching)
            return;
        if (DateTime.UtcNow < nextWatcherCheckUtc)
            return;
        nextWatcherCheckUtc = DateTime.UtcNow.AddSeconds(2);

        foreach (var pot in Game.ObjectSensor.NearbyPots())
        {
            if (pot.Distance > WatcherArmRangeY)
                continue;
            Chains.PlantFlow.StartWatching();
            Log.Information($"[PlantRecon] auto-armed - {pot.Name} at {pot.Distance:F1}y");
            return;
        }
    }

    private void OnTerritoryChanged(uint territory)
    {
        // A rezone ends the scene the recording was scoped to; the proximity check
        // re-arms it at the next pot. Manual watching (flag off) is untouched.
        if (Configuration.WatchPlantFlow && Chains.PlantFlow.Watching)
        {
            Chains.PlantFlow.StopWatching();
            Log.Information("[PlantRecon] auto-disarmed - rezone");
        }
    }
#endif

    private void OnCommand(string command, string args)
    {
        // In response to the slash command, toggle the display status of our main ui
        MainWindow.Toggle();
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
