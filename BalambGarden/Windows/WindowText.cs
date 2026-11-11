using System;
using System.Collections.Generic;
using BalambGarden.Chains;
using BalambGarden.Engine.Derivations;
using Dalamud.Bindings.ImGui;

namespace BalambGarden.Windows;

/// <summary>What the tabs say the same way: a ripe window (the day-part phrase on the surface and
/// the exact range behind it on hover, in the reader's local time), a cross seed's name, and text
/// at full or dim brightness.</summary>
internal static class WindowText
{
    /// <summary>The hover behind every day-part window: the exact range the model holds
    /// (precision ruling 2026-08-16 - the surface speaks in day-parts, the hover keeps
    /// the hours) plus what kind of claim the marker is making.</summary>
    public static string WindowTooltip(EtaWindow window)
        => WindowFormat.Range(window.Earliest.ToLocalTime(), window.Latest.ToLocalTime())
           + $"\n{WindowFormat.MarkMeaning(window.Provenance)}";

    /// <summary>The spoken day-part window, clamped at now - and the "~" approx marker
    /// only while there is still a future range to approximate ("~any time now" would be
    /// hedging a hedge). The hover keeps the raw math either way.</summary>
    public static string SpokenWindow(EtaWindow w, DateTimeOffset now, bool approxMark = false)
    {
        var local = now.ToLocalTime();
        var phrase = WindowFormat.Coarse(w.Earliest.ToLocalTime(), w.Latest.ToLocalTime(), local);
        return approxMark && local < w.Earliest.ToLocalTime() ? "~" + phrase : phrase;
    }

    /// <summary>A cross seed by its crop's name, or by its id when the crop table does not know it.</summary>
    public static string SeedName(uint seed) => WindowFormat.SeedName(Plugin.Tables.CropBySeedId(seed)?.Name, seed);

    /// <summary>A crop by its species number.</summary>
    public static string CropName(ushort species) => Plugin.Tables.SpeciesName(species);

    /// <summary>A soil by name: the topsoil table's, else the game's item sheet (Potting Soil is in
    /// no table), kept once read since the Yields tab asks every frame.</summary>
    public static string SoilName(uint soil)
    {
        if (!SoilNames.TryGetValue(soil, out var name))
            SoilNames[soil] = name = WindowFormat.SoilName(Plugin.Tables.SoilByItemId(soil)?.Name ?? PlantFlow.ItemName(soil), soil);
        return name;
    }

    private static readonly Dictionary<uint, string> SoilNames = [];

    /// <summary>Text at full brightness, or in <see cref="Palette.TextDim"/>.</summary>
    public static void Text(string text, bool dim)
    {
        if (dim)
            ImGui.TextColored(Palette.TextDim, text);
        else
            ImGui.TextUnformatted(text);
    }
}
