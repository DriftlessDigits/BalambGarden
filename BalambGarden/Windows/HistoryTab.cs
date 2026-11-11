using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace BalambGarden.Windows;

/// <summary>
/// The History tab (rethink slice 9, spec ruling 13): every plant and harvest, newest first, under a
/// heading for each day, filtered by estate, crop, hand and kind. Everything on it is
/// <see cref="GardenBoard.History"/>'s; every sentence and cell word is <see cref="WindowFormat"/>'s
/// (the column headings are this file's); this file only places them. Brightness is the only colour
/// it uses: labels, day headings and a value not heard draw in <see cref="Palette.TextDim"/>.
/// </summary>
internal static class HistoryTab
{
    private static readonly string[] Columns = ["When", "What", "Crop", "Where", "Hand", "Yield", "Cross seed"];

    /// <summary>The Yield column's place: its numbers sit right-aligned.</summary>
    private static readonly int YieldColumn = Array.IndexOf(Columns, "Yield");

    private const float FilterWidth = 170f;

    private static HistoryFilter filter = new();

    /// <summary>The history grows forever, so the page is rebuilt only when what it shows changed: a
    /// row landed, a filter changed, or an estate was named (<see cref="EstateNames"/>).</summary>
    private static HistoryPage? page;
    private static HistoryFilter? pageFilter;
    private static int pageRows = -1;
    private static int pageNames;

    /// <summary>The page as the table draws it: each line's words, and each column's width over every
    /// line (measured once per rebuild, so the columns hold still while the list scrolls).</summary>
    private static List<Line> lines = [];
    private static float[] widths = [];

    /// <summary>A day heading (<see cref="Day"/>) or a row's cells, each with whether it draws dim.</summary>
    private sealed record Line(string? Day, (string Text, bool Dim)[] Cells);

    public static void Draw()
    {
        using var tab = ImRaii.TabItem("History###history");
        if (!tab.Success)
            return;

        var history = Plugin.Garden.History;
        var estates = Plugin.Garden.Ledger.Estates;
        var names = EstateNames(estates);
        if (page is null || pageFilter != filter || pageRows != history.Entries.Count || pageNames != names)
            Rebuild(history, estates, names);

        ImGui.Spacing();
        if (page!.Since is not { } since)
        {
            ImGui.TextColored(Palette.TextDim, WindowFormat.NoHistory);
            return;
        }

        Filters(page);
        ImGui.Spacing();
        ImGui.TextColored(Palette.TextDim, WindowFormat.HistoryCount(page.Shown, page.Total, since.ToLocalTime(), page.Filtered));
        ImGui.Spacing();

        if (page.Shown == 0)
        {
            ImGui.TextColored(Palette.TextDim, WindowFormat.NoHistoryMatch);
            return;
        }

        Table();
    }

    /// <summary>A fingerprint of the estates' names, so a renamed estate rebuilds the page. A handful
    /// of records, read each frame.</summary>
    private static int EstateNames(List<EstateRecord> estates)
    {
        var hash = new HashCode();
        foreach (var estate in estates)
        {
            hash.Add(estate.Key);
            hash.Add(estate.Nickname);
        }
        return hash.ToHashCode();
    }

    private static void Rebuild(GardenHistory history, List<EstateRecord> estates, int names)
    {
        page = GardenBoard.History(history, filter, estates, Plugin.Tables, TimeZoneInfo.Local);
        pageFilter = filter;
        pageRows = history.Entries.Count;
        pageNames = names;

        lines = page.Lines.Select(ToLine).ToList();
        widths = Columns.Select(c => ImGui.CalcTextSize(c).X).ToArray();
        foreach (var line in lines)
        {
            if (line.Day is { } day)
                widths[0] = MathF.Max(widths[0], ImGui.CalcTextSize(day).X);
            else
                for (var c = 0; c < Columns.Length; c++)
                    widths[c] = MathF.Max(widths[c], ImGui.CalcTextSize(line.Cells[c].Text).X);
        }
    }

    private static Line ToLine(HistoryLine line) => line switch
    {
        HistoryDayLine day => new Line(WindowFormat.HistoryDay(day.Day), []),
        HistoryRowLine row => new Line(null,
        [
            (WindowFormat.HistoryTime(row.LocalAt), false),
            (WindowFormat.HistoryAct(row.Entry.Kind), false),
            (Plugin.Tables.SpeciesName(row.Entry.Species), false),
            (WindowFormat.HistoryWhere(row.EstateName, row.Entry), false),
            (WindowFormat.HistoryHand(row.Entry), false),
            WindowFormat.HistoryYield(row.Entry),
            WindowFormat.HistoryCross(row.Entry, WindowText.SeedName),
        ]),
        _ => throw new ArgumentOutOfRangeException(nameof(line)),
    };

    // ------------------------------------------------------------------ the four filters

    private static void Filters(HistoryPage current)
    {
        var estateName = filter.Estate is { } key
            ? current.Estates.FirstOrDefault(e => e.Key == key)?.Name ?? key.DisplayLabel()
            : WindowFormat.AllEstates;
        ImGui.SetNextItemWidth(FilterWidth);
        using (var combo = ImRaii.Combo("##history-estate", estateName))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable(WindowFormat.AllEstates, filter.Estate is null))
                    filter = filter with { Estate = null };
                foreach (var estate in current.Estates)
                    if (ImGui.Selectable(estate.Name, filter.Estate == estate.Key))
                        filter = filter with { Estate = estate.Key };
            }
        }

        ImGui.SameLine();
        var cropName = filter.Species is { } chosen ? Plugin.Tables.SpeciesName(chosen) : WindowFormat.AllCrops;
        ImGui.SetNextItemWidth(FilterWidth);
        using (var combo = ImRaii.Combo("##history-crop", cropName))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable(WindowFormat.AllCrops, filter.Species is null))
                    filter = filter with { Species = null };
                foreach (var species in current.Species)
                    if (ImGui.Selectable(Plugin.Tables.SpeciesName(species), filter.Species == species))
                        filter = filter with { Species = species };
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(FilterWidth);
        using (var combo = ImRaii.Combo("##history-hand", WindowFormat.HandChoice(filter.Own)))
        {
            if (combo.Success)
                foreach (var own in new bool?[] { null, true, false })
                    if (ImGui.Selectable(WindowFormat.HandChoice(own), filter.Own == own))
                        filter = filter with { Own = own };
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(FilterWidth);
        using (var combo = ImRaii.Combo("##history-kind", WindowFormat.KindChoice(filter.Kind)))
        {
            if (combo.Success)
                foreach (var kind in new HistoryKind?[] { null, HistoryKind.Plant, HistoryKind.Harvest })
                    if (ImGui.Selectable(WindowFormat.KindChoice(kind), filter.Kind == kind))
                        filter = filter with { Kind = kind };
        }
    }

    // ------------------------------------------------------------------ the rows

    private static void Table()
    {
        using var pad = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(10f, 2f));
        using var table = ImRaii.Table("##history-rows", Columns.Length, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY);
        if (!table.Success)
            return;

        for (var c = 0; c < Columns.Length; c++)
            ImGui.TableSetupColumn(Columns[c], ImGuiTableColumnFlags.WidthFixed, widths[c]);
        ImGui.TableSetupScrollFreeze(0, 1);

        ImGui.TableNextRow();
        for (var c = 0; c < Columns.Length; c++)
        {
            ImGui.TableNextColumn();
            if (c == YieldColumn)
                RightAlign(Columns[c], c);
            ImGui.TextColored(Palette.TextDim, Columns[c]);
        }

        // Only the rows in view are drawn: the history grows forever. The clipper is native memory,
        // so it is released even if a row throws.
        var clipper = ImGui.ImGuiListClipper();
        try
        {
            clipper.Begin(lines.Count);
            while (clipper.Step())
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    Row(lines[i]);
            clipper.End();
        }
        finally
        {
            clipper.Destroy();
        }
    }

    private static void Row(Line line)
    {
        ImGui.TableNextRow();
        if (line.Day is { } day)
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(Palette.TextDim, day);
            return;
        }

        for (var c = 0; c < Columns.Length; c++)
        {
            ImGui.TableNextColumn();
            var (text, dim) = line.Cells[c];
            if (text.Length == 0)
                continue;
            if (c == YieldColumn)
                RightAlign(text, c);
            WindowText.Text(text, dim);
        }
    }

    /// <summary>Moves the cursor so <paramref name="text"/> ends at its column's right edge.</summary>
    private static void RightAlign(string text, int column)
        => ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(widths[column] - ImGui.CalcTextSize(text).X, 0f));
}
