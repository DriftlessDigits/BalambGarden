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

        Garden = GardenService.Load(PluginInterface.GetPluginConfigDirectory());

        TendChain = new Chains.TendChain();
        CycleChain = new Chains.CycleChain();
        PotChain = new Chains.PotChain();

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
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        Svc.Framework.Update -= OnFrameworkUpdate;
#if DEBUG
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
#endif
        Game.ObtainWatch.Stop();
#if DEBUG
        // A watcher left registered would outlive the plugin its callback belongs to.
        Chains.PlantFlow.StopWatching();
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

    /// <summary>Nothing sensed before the player exists: no character, no estate,
    /// no census. The pump throttles itself past this gate.</summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!PlayerState.IsLoaded)
            return;
        Game.CensusPump.Tick();
#if DEBUG
        AutoArmWatcher();
#endif
    }

#if DEBUG
    /// <summary>How close to a pot counts as "about to garden" (Sam's spec, 08-15).
    /// Internal so the recon readout can quote the real number, not a copy of it.</summary>
    internal const float WatcherArmRangeY = 4.6f;

    private DateTime nextWatcherCheckUtc = DateTime.MinValue;

    /// <summary>Scoped recording (Sam's ruling 08-15): with the config flag on, the
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
