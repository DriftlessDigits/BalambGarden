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
/// every sentence is <see cref="WindowFormat"/>'s; this file only places them. Brightness is the
/// only colour it uses: a zero count, and a rate resting on too few harvests, draw in
/// <see cref="Palette.TextDim"/>.
/// </summary>
internal static class YieldsTab
{
    private static readonly string[] BedColumns = ["Crop", "Planted", "Harvested", "Growing", "Lost", "Gathered", "Crossed", "Cross seeds"];
    private const int PotColumns = 6;   // the bed columns up to Gathered: pots never cross

    public static void Draw()
    {
        using var tab = ImRaii.TabItem("Yields###yields");
        if (!tab.Success)
            return;

        var book = GardenBoard.Yields(Plugin.Garden.History, Plugin.Garden.Census.LedgerBeds, Plugin.Tables);

        ImGui.Spacing();
        if (book.Since is not { } since)
        {
            ImGui.TextDisabled(WindowFormat.NoYields);
            return;
        }

        var local = since.ToLocalTime();
        ImGui.TextDisabled(WindowFormat.YieldsSince(local));
        ImGui.Spacing();

        // One crop column width for both sections, so the numbers line up from beds to pots.
        var cropWidth = book.Beds.Concat(book.Pots)
            .Select(r => ImGui.CalcTextSize(Plugin.Tables.SpeciesName(r.Species)).X)
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
            ImGui.TextDisabled(BedColumns[c]);
        }

        foreach (var row in rows)
            Row(row, since);
    }

    private static void Row(YieldRow row, DateTimeOffset since)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Plugin.Tables.SpeciesName(row.Species));

        Count(row.Planted);
        Count(row.Harvested);
        if (row.Unmatched > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(WindowFormat.UnmatchedHover(row.Unmatched, since));
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
            Text(WindowFormat.Percent(rate.Percent), dim: !rate.Firm);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(WindowFormat.CrossRateHover(rate));
        }
        else
        {
            Text("-", dim: true);
        }

        ImGui.TableNextColumn();
        if (row.Seeds.Count > 0)
        {
            ImGui.TextUnformatted(WindowFormat.SeedShares(row.Seeds, SeedName));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(WindowFormat.SeedCounts(row.Seeds, SeedName));
        }
    }

    private static string SeedName(uint seed) => Plugin.Tables.CropBySeedId(seed)?.Name ?? $"seed {seed}";

    /// <summary>A count in the next cell, dim at zero.</summary>
    private static void Count(int n)
    {
        ImGui.TableNextColumn();
        Text(n.ToString(), dim: n == 0);
    }

    private static void Text(string text, bool dim)
    {
        if (dim)
            ImGui.TextColored(Palette.TextDim, text);
        else
            ImGui.TextUnformatted(text);
    }
}
