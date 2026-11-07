using System;
using BalambGarden.Chains;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sync;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BalambGarden.Game;

/// <summary>
/// Hears the game's sow question for sows with no chain behind them (v0.4.1, Sow Capture design
/// ruling 4) - "Prepare the bed with a bag of grade 3 thanalan topsoil and a bag of krakka root
/// seeds?" is the only place the game names a sow's soil, and the sow line after it never does.
/// When the question appears its soil goes into the <see cref="SoilNote"/>; when it goes away the
/// note starts running out; the next sow line takes it (<see cref="CensusPump"/>).
///
/// <para>A READER, NEVER A CLICKER. It reads the yes/no prompt through the same reader and parser
/// the chains use (<see cref="PlantFlow.ReadStringValue"/>, <see cref="SowPrompt.Parse"/>) and never
/// answers, prevents or touches it. Every other yes/no prompt in the game fails the parse and is
/// ignored outright. Not debug-gated: the soil it hears is the product. The debug PlantFlow watcher
/// stays debug-only and separate.</para>
///
/// <para>While a chain drives, the chain owns the question: it checks the prompt itself and banks
/// the soil on its own receipt, so the listener stands aside and the note keeps hand sows only.</para>
/// </summary>
internal static unsafe class SowQuestion
{
    private const string Addon = "SelectYesno";

    private static readonly SoilNote note = new();

    /// <summary>The prompt now on screen is a sow question we noted - so its closing is ours to
    /// mark. A different yes/no closing must not start a sow note's clock.</summary>
    private static bool sowQuestionOpen;

    // Held as single delegate instances so the unregister removes the same listener the
    // register added (the PlantFlow watcher's lesson: a method group converted twice is two objects).
    private static readonly IAddonLifecycle.AddonEventDelegate SetupListener = OnSetup;
    private static readonly IAddonLifecycle.AddonEventDelegate FinalizeListener = OnFinalize;

    internal static void Start()
    {
        // The soil-name list walks the whole Item sheet once; at load, not inside the game's
        // own prompt setup the first time a hand sow asks.
        PlantFlow.SoilItemId("potting soil");
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, Addon, SetupListener);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, Addon, FinalizeListener);
    }

    internal static void Stop()
    {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, Addon, SetupListener);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, Addon, FinalizeListener);
    }

    /// <summary>The soil for a sow line at <paramref name="lineAt"/>, or null when no question
    /// covers it. Taking clears the note.</summary>
    internal static uint? TakeSoil(DateTimeOffset lineAt)
    {
        var soil = note.Take(lineAt);
        // The field-fire's instrument for the note's window: the only capture measured the line
        // from the question APPEARING (2.39s, Yes included); this measures it from the close.
        if (soil is not null && lastClosedAt is { } closed)
            Plugin.Log.Information(
                $"[SowQuestion] soil {soil} taken by the sow line {(lineAt - closed).TotalSeconds:0.00}s after the question closed");
        return soil;
    }

    private static DateTimeOffset? lastClosedAt;

    private static void OnSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (Plugin.AnyChainDriving)
                return;

            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null)
                return;

            var prompt = PlantFlow.ReadStringValue(addon, 0);
            if (SowPrompt.Parse(prompt) is not { } parts)
                return;

            sowQuestionOpen = true;
            lastClosedAt = null;
            var soil = PlantFlow.SoilItemId(parts.Soil);
            note.Offer(soil, DateTimeOffset.UtcNow);
            if (soil is null)
                Plugin.Log.Warning($"[SowQuestion] sow question names a soil with no item id: '{parts.Soil}'");
            else
                Plugin.Log.Debug($"[SowQuestion] sow question heard: soil '{parts.Soil}' = item {soil}");
        }
        catch (Exception ex)
        {
            // The game is building its own prompt here; a throw would land inside its UI setup.
            Plugin.Log.Warning($"[SowQuestion] sow question read failed: {ex.Message}");
        }
    }

    private static void OnFinalize(AddonEvent type, AddonArgs args)
    {
        if (!sowQuestionOpen)
            return;
        sowQuestionOpen = false;
        lastClosedAt = DateTimeOffset.UtcNow;
        note.Closed(lastClosedAt.Value);
    }
}
