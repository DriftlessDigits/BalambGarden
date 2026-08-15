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

    public readonly WindowSystem WindowSystem = new("BalambGarden");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    internal RunLogWindow RunLogWindow { get; init; }

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Tables = BalambGarden.Engine.Domain.DomainTables.Load();
        Log.Information($"[Engine] domain tables loaded: sunflower check = {Tables.SpeciesName(103)}");

        Garden = GardenService.Load(PluginInterface.GetPluginConfigDirectory());

        TendChain = new Chains.TendChain();

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

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        TendChain.Dispose();

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
    }

    private void OnCommand(string command, string args)
    {
        // In response to the slash command, toggle the display status of our main ui
        MainWindow.Toggle();
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
