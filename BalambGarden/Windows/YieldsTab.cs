using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BalambGarden.Engine.Derivations;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace BalambGarden.Windows;

/// <summary>
/// The Yields tab (rethink slice 7, rulings 6-9): per crop, beds and pots in their own sections,
/// what was planted, harvested, still growing and lost, what was gathered, and for beds how often
/// the cross took and which seed it gave. Everything on it is <see cref="GardenBoard.Yields"/>'s;
/// every sentence and cell word is <see cref="WindowFormat"/>'s (the headings are this file's);
/// this file only places them. Besides brightness (labels, a zero count and a rate resting on too few
/// harvests draw in <see cref="Palette.TextDim"/>) it uses one colour, the onion ink (rethink slice
/// 12, spec ruling 14): the trophy line over the tables, and the onion row's name and gathered count
/// once it has gathered something (<see cref="YieldRow.OnionMark"/>).
/// </summary>
internal static class YieldsTab
{
    private static readonly string[] BedColumns = ["Crop", "Planted", "Harvested", "Growing", "Lost", "Gathered", "Crossed", "Cross seeds"];

    /// <summary>Pots never cross: their table stops before the cross columns.</summary>
    private static readonly int PotColumns = Array.IndexOf(BedColumns, "Crossed");

    /// <summary>The history grows forever, so the book is not rebuilt every frame: once a second,
    /// or at once when a row lands. A ledger change (a plant seen dead) shows within the second.</summary>
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(1);

    private static YieldBook? book;
    private static int bookRows = -1;
    private static DateTime bookAt;

    /// <summary>The trophy line's words, built with the book (never per frame); null = no line.</summary>
    private static string? trophy;

    public static void Draw()
    {
        using var tab = ImRaii.TabItem("Yields###yields");
        if (!tab.Success)
            return;

        var history = Plugin.Garden.History;
        if (book is null || bookRows != history.Entries.Count || DateTime.UtcNow - bookAt > Refresh)
        {
            book = GardenBoard.Yields(history, Plugin.Garden.Census.LedgerBeds, Plugin.Tables);
            bookRows = history.Entries.Count;
            bookAt = DateTime.UtcNow;
            trophy = WindowFormat.OnionTrophy(book.Trophy);
        }

        ImGui.Spacing();
        if (book.Since is not { } since)
        {
            ImGui.TextColored(Palette.TextDim, WindowFormat.NoYields);
            return;
        }

        if (trophy is not null)
            Trophy(trophy);

        var local = since.ToLocalTime();
        ImGui.TextColored(Palette.TextDim, WindowFormat.YieldsSince(local));
        ImGui.Spacing();

        // One crop column width for both sections, so the numbers line up from beds to pots; a
        // split row's name sits indented under its crop.
        var rows = book.Beds.Concat(book.Pots).ToList();
        var cropWidth = rows
            .Select(r => ImGui.CalcTextSize(Plugin.Tables.SpeciesName(r.Species)).X)
            .Concat(rows.SelectMany(r => r.Splits)
                .Select(s => SplitIndent + ImGui.CalcTextSize(SplitLabel(s)).X))
            .Append(ImGui.CalcTextSize(BedColumns[0]).X)
            .Max();

        if (book.Beds.Count > 0)
            Section("BEDS", "##yields-beds", book.Beds, BedColumns.Length, cropWidth, local);
        if (book.Pots.Count > 0)
        {
            if (book.Beds.Count > 0)
                ImGui.Spacing();
            Section("POTS", "##yields-pots", book.Pots, PotColumns, cropWidth, local);
        }
    }

    private static void Section(string title, string id, IReadOnlyList<YieldRow> rows, int columns, float cropWidth,
        DateTimeOffset since)
    {
        ImGui.TextUnformatted(title);

        using var pad = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(10f, 2f));
        using var table = ImRaii.Table(id, columns, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoHostExtendX);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn(BedColumns[0], ImGuiTableColumnFlags.WidthFixed, cropWidth);
        for (var c = 1; c < columns; c++)
            ImGui.TableSetupColumn(BedColumns[c], ImGuiTableColumnFlags.WidthFixed,
                c == BedColumns.Length - 1 ? 0f : ImGui.CalcTextSize(BedColumns[c]).X);

        ImGui.TableNextRow();
        for (var c = 0; c < columns; c++)
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(Palette.TextDim, BedColumns[c]);
        }

        foreach (var row in rows)
        {
            Row(row, since);
            foreach (var split in row.Splits)
                Row(split, since, SplitLabel(split));
        }
    }

    /// <summary>How much larger the trophy line draws than the tab's text.</summary>
    private const float TrophyScale = 1.4f;

    /// <summary>The shimmer's band, soft-edged: three clipped copies of the line in the shine ink,
    /// each a share of the band's width and an alpha; the narrower ones sit over the wider. The inks
    /// are worked out once, not per frame.</summary>
    private static readonly (float Width, uint Ink)[] ShineBands =
    [
        (1f, ImGui.ColorConvertFloat4ToU32(Palette.OnionShine with { W = 0.30f })),
        (0.6f, ImGui.ColorConvertFloat4ToU32(Palette.OnionShine with { W = 0.35f })),
        (0.3f, ImGui.ColorConvertFloat4ToU32(Palette.OnionShine with { W = 0.45f })),
    ];

    // The trophy line's sparkles, in line heights: the text sits LeadIn in from the line's start;
    // a big sparkle Big from the text's edge, radius BigRadius; a small one Small out, radius
    // SmallRadius. The space kept for the line reaches the small sparkles' outer tips.
    private const float LeadIn = 1.1f, Big = 0.6f, BigRadius = 0.42f, Small = 0.95f, SmallRadius = 0.18f;

    /// <summary>The trophy line (rethink slice 12): the onion tally over the tables, in the onion
    /// ink, larger than a row, a gold sparkle either side, and a slow shimmer crossing it
    /// (<see cref="TrophyShimmer"/>, off ImGui's own clock, so opening the tab starts nothing). It
    /// lives on this tab only; nothing announces it. Per frame it adds the line's text run, up to three
    /// clipped copies of it while the band crosses, and four sparkle shapes; it builds no string and
    /// keeps nothing between frames (the words are built with the book).</summary>
    private static void Trophy(string line)
    {
        ImGui.SetWindowFontScale(TrophyScale);
        var draw = ImGui.GetWindowDrawList();
        var size = ImGui.CalcTextSize(line);
        var h = size.Y;
        var start = ImGui.GetCursorScreenPos();
        var at = start + new Vector2(h * LeadIn, 0f);
        var gold = ImGui.ColorConvertFloat4ToU32(Palette.OnionInk);

        draw.AddText(at, gold, line);
        if (TrophyShimmer.Centre(ImGui.GetTime()) is { } centre)
        {
            var x = at.X + centre * size.X;
            var half = TrophyShimmer.HalfWidth * size.X;
            foreach (var (width, ink) in ShineBands)
            {
                draw.PushClipRect(new Vector2(x - half * width, at.Y), new Vector2(x + half * width, at.Y + h), true);
                draw.AddText(at, ink, line);
                draw.PopClipRect();
            }
        }

        Sparkle(draw, at + new Vector2(-h * Big, h * 0.5f), h * BigRadius, gold);
        Sparkle(draw, at + new Vector2(-h * Small, h * 0.15f), h * SmallRadius, gold);
        Sparkle(draw, at + new Vector2(size.X + h * Big, h * 0.5f), h * BigRadius, gold);
        Sparkle(draw, at + new Vector2(size.X + h * Small, h * 0.85f), h * SmallRadius, gold);

        ImGui.Dummy(new Vector2(size.X + h * (LeadIn + Small + SmallRadius), h));
        ImGui.SetWindowFontScale(1f);
        ImGui.Spacing();
    }

    /// <summary>A four-point sparkle centred on <paramref name="centre"/>: two thin diamonds crossed.</summary>
    private static void Sparkle(ImDrawListPtr draw, Vector2 centre, float radius, uint colour)
    {
        var waist = radius * 0.28f;
        draw.AddQuadFilled(centre + new Vector2(0f, -radius), centre + new Vector2(waist, 0f),
            centre + new Vector2(0f, radius), centre + new Vector2(-waist, 0f), colour);
        draw.AddQuadFilled(centre + new Vector2(-radius, 0f), centre + new Vector2(0f, -waist),
            centre + new Vector2(radius, 0f), centre + new Vector2(0f, waist), colour);
    }

    /// <summary>How far a split row's name sits in from its crop's.</summary>
    private const float SplitIndent = 14f;

    private static string SplitLabel(YieldRow split) => WindowFormat.PartnerLabel(split.Partner!.Value, Plugin.Tables.SpeciesName);

    /// <summary>A crop's row, or with <paramref name="split"/> one of its split rows (rethink slice 8):
    /// its name dim and indented under the crop, the same columns after it.</summary>
    private static void Row(YieldRow row, DateTimeOffset since, string? split = null)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        var onion = row.OnionMark;
        if (split is null)
        {
            WindowText.Text(Plugin.Tables.SpeciesName(row.Species), dim: false, onion);
            if (ImGui.IsItemHovered() && WindowFormat.PartnerHover(row, Plugin.Tables.SpeciesName) is { } hover)
                ImGui.SetTooltip(hover);
        }
        else
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + SplitIndent);
            ImGui.TextColored(Palette.TextDim, split);
        }

        Count(row.Planted);
        Count(row.Harvested);
        if (row.Unmatched > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(WindowFormat.UnmatchedHover(row.Unmatched, since, row.IsPot));
        Count(row.Growing);
        Count(row.Lost);
        Count(row.Gathered, onion);
        if (row.GatheredFrom > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(WindowFormat.GatheredHover(row.GatheredFrom));

        if (row.IsPot)
            return;

        ImGui.TableNextColumn();
        if (row.Cross is { } rate)
        {
            WindowText.Text(WindowFormat.Percent(rate.Percent), dim: !rate.Firm);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(WindowFormat.CrossRateHover(rate));
        }
        else
        {
            WindowText.Text(WindowFormat.NoCrossHeard, dim: true);
        }

        ImGui.TableNextColumn();
        if (row.Seeds.Count > 0)
        {
            ImGui.TextUnformatted(WindowFormat.SeedShares(row.Seeds, WindowText.SeedName));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(WindowFormat.SeedCounts(row.Seeds, WindowText.SeedName));
        }
    }

    /// <summary>A count in the next cell, dim at zero; with <paramref name="onion"/>, gold above zero
    /// (the onion row's gathered count, rethink slice 12).</summary>
    private static void Count(int n, bool onion = false)
    {
        ImGui.TableNextColumn();
        WindowText.Text(n.ToString(), dim: n == 0, onion: onion && n > 0);
    }
}
