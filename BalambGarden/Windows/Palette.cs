using System;
using System.Numerics;

namespace BalambGarden.Windows;

/// <summary>
/// Every colour the Garden tab draws, named by the one thing it means: each colour means one
/// thing on every surface (the colour map ruling). A screen picks a meaning here and never holds
/// a colour value of its own. A meaning has a fill (cells, bands, bars) and, where text or a thin
/// mark carries it, a brighter ink. Shape, line style and words carry what colour does not: a
/// dashed outline is "not heard", provenance is a glyph, status and cautions are plain words.
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

    /// <summary>Wilting: purple, the game's own haze - past the crop's wilt hours, or a pot the
    /// live map shows wilting.</summary>
    public static readonly Vector4 Wilting = new(0.48f, 0.26f, 0.66f, 1f);
    public static readonly Vector4 WiltingInk = new(0.80f, 0.56f, 1f, 1f);

    /// <summary>Dying: red, past halfway from wilting to withering - the plant is about to be lost.</summary>
    public static readonly Vector4 Dying = new(0.62f, 0.20f, 0.20f, 1f);
    public static readonly Vector4 DyingInk = new(1f, 0.4f, 0.4f, 1f);

    /// <summary>Ripe: blue, a harvest waiting. The stripe marks a ripe window that is only estimated.</summary>
    public static readonly Vector4 Ripe = new(0.22f, 0.45f, 0.80f, 1f);
    public static readonly Vector4 RipeInk = new(0.52f, 0.72f, 1f, 1f);
    public static readonly Vector4 RipeStripe = new(0.12f, 0.26f, 0.50f, 1f);

    /// <summary>Empty: earth, ready to replant.</summary>
    public static readonly Vector4 Empty = new(0.24f, 0.19f, 0.13f, 1f);

    /// <summary>No claim, in greys: a slot with nothing recorded (near-black), a record never
    /// read (mid grey), a water clock that is fine and asks nothing.</summary>
    public static readonly Vector4 NothingRecorded = new(0.12f, 0.12f, 0.12f, 1f);
    public static readonly Vector4 NeverRead = new(0.30f, 0.30f, 0.30f, 1f);
    public static readonly Vector4 ClockFine = new(0.42f, 0.42f, 0.45f, 1f);

    // ------------------------------------------------------------------ text

    /// <summary>Plain text, full brightness: what matters now.</summary>
    public static readonly Vector4 Text = Vector4.One;

    /// <summary>Dim text: ages, counts at zero, labels.</summary>
    public static readonly Vector4 TextDim = new(0.55f, 0.55f, 0.55f, 1f);

    // ------------------------------------------------------------------ structure, meaning nothing

    /// <summary>A card's frame.</summary>
    public static readonly Vector4 Frame = new(0.35f, 0.35f, 0.35f, 1f);

    /// <summary>A container's outline.</summary>
    public static readonly Vector4 Outline = new(0.40f, 0.40f, 0.40f, 1f);

    /// <summary>A chip's fill with a count above zero, and at zero.</summary>
    public static readonly Vector4 ChipLit = new(0.22f, 0.30f, 0.40f, 1f);
    public static readonly Vector4 ChipUnlit = new(0.16f, 0.16f, 0.16f, 1f);

    /// <summary>The lanes' day lines and lane rules, and the line at now.</summary>
    public static readonly Vector4 DayLine = new(0.24f, 0.24f, 0.27f, 1f);
    public static readonly Vector4 NowLine = new(0.85f, 0.85f, 0.95f, 0.9f);
}
