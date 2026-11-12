using System;
using System.Numerics;

namespace BalambGarden.Windows;

/// <summary>
/// The plugin's colours, each named by the one thing it means (the colour map ruling: one colour,
/// one meaning, on every surface). A meaning has a fill (cells, bands, bars) and, where text or a
/// thin mark carries it, a brighter ink. The Garden tab and its lanes take every colour value from
/// here, and each draws only its one meaning; the estate tabs still hold their own values. What
/// colour does not carry is said by line style, glyphs and words: the no-watering-recorded line
/// is plain text, the outline of a bed or pot with no watering recorded is <see cref="Dashed"/>, a
/// lit chip is a structure grey.
/// </summary>
internal static class Palette
{
    // ------------------------------------------------------------------ what a colour means

    /// <summary>Growing, by stage: a green ramp from a fresh sowing (stage 0) to stage 3.</summary>
    public static Vector4 Growing(byte stage)
    {
        var t = Math.Clamp(stage / 3f, 0f, 1f);
        return new Vector4(0.13f + 0.10f * t, 0.28f + 0.44f * t, 0.16f + 0.12f * t, 1f);
    }

    /// <summary>Water soon: amber, past three quarters of the crop's wilt hours.</summary>
    public static readonly Vector4 WaterSoon = new(0.62f, 0.46f, 0.16f, 1f);
    public static readonly Vector4 WaterSoonInk = new(1f, 0.78f, 0.35f, 1f);

    /// <summary>Wilting: purple, the game's own haze - a bed or pot past its crop's wilt hours, or a
    /// pot the game shows wilting.</summary>
    public static readonly Vector4 Wilting = new(0.48f, 0.26f, 0.66f, 1f);
    public static readonly Vector4 WiltingInk = new(0.80f, 0.56f, 1f, 1f);

    /// <summary>Dying: red, past halfway from wilting to withering - the plant is about to be lost.</summary>
    public static readonly Vector4 Dying = new(0.62f, 0.20f, 0.20f, 1f);
    public static readonly Vector4 DyingInk = new(1f, 0.4f, 0.4f, 1f);

    /// <summary>Ripe: blue, a harvest waiting. The stripe marks a ripe window that is only estimated.</summary>
    public static readonly Vector4 Ripe = new(0.22f, 0.45f, 0.80f, 1f);
    public static readonly Vector4 RipeInk = new(0.52f, 0.72f, 1f, 1f);
    public static readonly Vector4 RipeStripe = new(0.12f, 0.26f, 0.50f, 1f);

    /// <summary>Onion: gold, the goal crop's harvest (rethink slice 12) - nothing else: an onion
    /// harvest's crop name and yield on the History tab, the onion row's name and gathered count on
    /// the Yields tab, and the trophy line over it with its two sparkles. A pure gold, more
    /// yellow and more saturated than <see cref="WaterSoonInk"/>'s amber, so the two read apart.</summary>
    public static readonly Vector4 OnionInk = new(1f, 0.84f, 0f, 1f);

    /// <summary>The onion shine: the pale light of the trophy line's shimmer band, laid over the gold
    /// at part strength (the band's soft edge sets the alpha). Onion only, like the gold.</summary>
    public static readonly Vector4 OnionShine = new(1f, 1f, 0.92f, 1f);

    /// <summary>Empty: earth, ready to replant.</summary>
    public static readonly Vector4 Empty = new(0.24f, 0.19f, 0.13f, 1f);

    /// <summary>Dead: the game reads the plant as lost ("beyond hope"). The earth of a container to
    /// sow again, under a cross (<see cref="DeadMark"/>) that says the plant is gone and must be
    /// cleared first: the shape carries "dead", never ripe and never alive. Provisional (rethink
    /// slice 23), held for the final UI check.</summary>
    public static readonly Vector4 Dead = Empty;

    /// <summary>The cross drawn over a dead plant: a pale ash ink, so the shape reads on the earth.</summary>
    public static readonly Vector4 DeadMark = new(0.70f, 0.66f, 0.60f, 1f);

    /// <summary>No claim, in greys: an unclaimed slot, nothing recorded (near-black); an unread
    /// record, contents never read (mid grey); a water clock that is fine and asks nothing.</summary>
    public static readonly Vector4 Unclaimed = new(0.12f, 0.12f, 0.12f, 1f);
    public static readonly Vector4 Unread = new(0.30f, 0.30f, 0.30f, 1f);
    public static readonly Vector4 ClockFine = new(0.42f, 0.42f, 0.45f, 1f);

    /// <summary>The inside of a hollow cell: a state the clock alone holds (spec ruling 24), its
    /// colour drawn as a ring around this. The window's own dark, so the ring carries the state.</summary>
    public static readonly Vector4 Hollow = new(0.12f, 0.12f, 0.12f, 1f);

    /// <summary>The ring of a plant dead by the clock: the earth of <see cref="Dead"/> lifted, so the
    /// ring reads on <see cref="Hollow"/> (the earth itself is too dark to see as a ring there).</summary>
    public static readonly Vector4 DeadRing = new(0.52f, 0.41f, 0.27f, 1f);

    // ------------------------------------------------------------------ text

    /// <summary>Plain text drawn by hand, full brightness: what matters now (ImGui's own text
    /// uses the theme's colour).</summary>
    public static readonly Vector4 Text = Vector4.One;

    /// <summary>Dim text: ages, counts at zero, labels.</summary>
    public static readonly Vector4 TextDim = new(0.55f, 0.55f, 0.55f, 1f);

    // ------------------------------------------------------------------ structure: frames, rules, chips

    /// <summary>A card's frame.</summary>
    public static readonly Vector4 Frame = new(0.35f, 0.35f, 0.35f, 1f);

    /// <summary>A container's outline.</summary>
    public static readonly Vector4 Outline = new(0.40f, 0.40f, 0.40f, 1f);

    /// <summary>A dashed outline: not heard or not confirmed. The dashes carry it, not the colour.</summary>
    public static readonly Vector4 Dashed = new(0.55f, 0.55f, 0.55f, 1f);

    /// <summary>A chip's fill with a count above zero, and at zero: neutral, brightness only.</summary>
    public static readonly Vector4 ChipLit = new(0.28f, 0.28f, 0.28f, 1f);
    public static readonly Vector4 ChipUnlit = new(0.16f, 0.16f, 0.16f, 1f);

    /// <summary>The lanes' day lines and lane rules, and the line at now.</summary>
    public static readonly Vector4 DayLine = new(0.24f, 0.24f, 0.27f, 1f);
    public static readonly Vector4 NowLine = new(0.85f, 0.85f, 0.95f, 0.9f);
}
