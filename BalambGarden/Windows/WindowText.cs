using System;
using BalambGarden.Engine.Derivations;

namespace BalambGarden.Windows;

/// <summary>How a window speaks a ripe window, shared by every tab that shows one: the day-part
/// phrase on the surface and the exact range behind it on hover, in the reader's local time.</summary>
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
}
