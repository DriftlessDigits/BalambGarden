using System.Globalization;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>
/// How a derived number says what kind of claim it is making. Pure string shaping, kept
/// in the Engine because the honest-surface rules (a provenance marker is TEXT, never
/// colour alone; an age never runs backwards; a pot's water column is "-", not "?") are
/// behaviour worth pinning, not layout.
/// </summary>
public static class WindowFormat
{
    /// <summary>Provenance markers are text on purpose (spec: glyphs, not colour alone) -
    /// a colour-blind reader and a screenshot both have to carry the claim.</summary>
    public static string Mark(Provenance provenance) => provenance switch
    {
        Provenance.Anchored => "[A]",
        Provenance.Bracketed => "[~]",
        _ => "[?]",
    };

    /// <summary>The hover line that says what the marker is claiming, in plain terms.</summary>
    public static string MarkMeaning(Provenance provenance) => provenance switch
    {
        Provenance.Anchored =>
            "anchored: this bed was planted under watch, so the clock starts at a real receipt",
        Provenance.Bracketed =>
            "bracketed: two sightings bound the stage flip - the window is as wide as the gap between visits",
        _ =>
            "estimated: one sighting only, so the window is the whole stage band",
    };

    /// <summary>How the reader tells time. The app sets this from its config at load and
    /// on change; the Engine only formats. False (24h) is the default so the choice is
    /// always an explicit act of the app, never a formatting surprise.</summary>
    public static bool TwelveHourClock;

    private static string Clock(DateTimeOffset t, bool withDay)
    {
        var day = withDay ? t.ToString("ddd ", CultureInfo.InvariantCulture) : "";
        return TwelveHourClock
            ? day + t.ToString("h:mm", CultureInfo.InvariantCulture) + (t.Hour < 12 ? "am" : "pm")
            : day + t.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>A window in the reader's own clock - the caller converts to local time
    /// first, because the Engine has no business knowing where the player lives. A
    /// zero-width window prints as the single time it actually is, never as a range
    /// pretending to have two ends.</summary>
    public static string Range(DateTimeOffset earliest, DateTimeOffset latest)
    {
        var lo = Clock(earliest, withDay: true);
        if (latest <= earliest)
            return lo;

        var hi = Clock(latest, withDay: earliest.Date != latest.Date);
        return $"{lo}-{hi}";
    }

    /// <summary>Data age, beside every number the dashboard shows. Clock skew clamps to
    /// "just now" - a negative age would be the surface claiming to have seen the future.</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var span = now - at;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours}h ago"
            : $"{(int)span.TotalDays}d ago";
    }

    /// <summary>The water state as a word. "-" means the state makes no claim for this row
    /// (pot wilt is unverified - the twins labs will say; until then pots assert nothing);
    /// "?" means we genuinely do not know. Two different silences, two different marks.</summary>
    public static string Water(WaterState state) => state switch
    {
        WaterState.Watered => "watered",
        WaterState.Due => "due",
        WaterState.Overdue => "overdue",
        WaterState.Danger => "danger",
        WaterState.NotApplicable => "-",
        _ => "?",
    };
}
