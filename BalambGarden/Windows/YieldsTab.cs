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
/// this file only places them. Brightness is the only colour it uses: labels, a zero count and a
/// rate resting on too few harvests draw in <see cref="Palette.TextDim"/>.
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
        }

        ImGui.Spacing();
        if (book.Since is not { } since)
        {
            ImGui.TextColored(Palette.TextDim, WindowFormat.NoYields);
            return;
        }

        var local = since.ToLocalTime();
        ImGui.TextColored(Palette.TextDim, WindowFormat.YieldsSince(local));
        ImGui.Spacing();

        // One crop column width for both sections, so the numbers line up from beds to pots; a
        // split row's name sits indented under its crop.
        var rows = book.Beds.Concat(book.Pots).ToList();
        var cropWidth = rows
            .Select(r => ImGui.CalcTextSize(WindowText.CropName(r.Species)).X)
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

    /// <summary>How far a split row's name sits in from its crop's.</summary>
    private const float SplitIndent = 14f;

    private static string SplitLabel(YieldRow split) => WindowFormat.PartnerLabel(split.Partner!.Value, WindowText.CropName);

    /// <summary>A crop's row, or with <paramref name="split"/> one of its split rows (rethink slice 8):
    /// its name dim and indented under the crop, the same columns after it.</summary>
    private static void Row(YieldRow row, DateTimeOffset since, string? split = null)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        if (split is null)
        {
            ImGui.TextUnformatted(WindowText.CropName(row.Species));
            if (ImGui.IsItemHovered() && WindowFormat.PartnerHover(row, WindowText.CropName) is { } hover)
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
        Count(row.Gathered);
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

    /// <summary>A count in the next cell, dim at zero.</summary>
    private static void Count(int n)
    {
        ImGui.TableNextColumn();
        WindowText.Text(n.ToString(), dim: n == 0);
    }
}
