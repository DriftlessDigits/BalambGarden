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
/// heading for each day, filtered by estate, crop, hand and kind. Each day folds to its heading and
/// count (rethink slice 24, ruling 13 amended): the tab opens on the newest day open, the rest folded;
/// the days the player opens stay open until the tab is left, and a landing row never moves the line
/// at the top of the view (<see cref="GardenBoard.HistoryLineIndex"/>). A hand reads its alias (rethink
/// slice 11, spec ruling 15), else "household", and the player's own reads "you". Everything on it is
/// <see cref="GardenBoard.History"/>'s; every sentence and cell word is <see cref="WindowFormat"/>'s
/// (the column headings are this file's); this file only places them. Besides brightness (labels, day
/// headings, a value not heard and a heard harvest that gave no cross seed - "no cross" - draw in
/// <see cref="Palette.TextDim"/>) it uses one colour: an onion harvest's crop and heard yield in
/// <see cref="Palette.OnionInk"/> (rethink slice 12, spec ruling 14; <see cref="GardenBoard.IsGoalCropHarvest"/>).
/// </summary>
internal static class HistoryTab
{
    private static readonly string[] Columns = ["When", "What", "Crop", "Where", "Hand", "Yield", "Cross seed"];

    /// <summary>The Yield column's place: its numbers sit right-aligned.</summary>
    private static readonly int YieldColumn = Array.IndexOf(Columns, "Yield");

    private const float FilterWidth = 170f;

    /// <summary>The table's cell padding; a row is the font's height plus this above and below, every
    /// row alike (headings too), which the list clipper and the held view both rely on.</summary>
    private static readonly Vector2 CellPadding = new(10f, 2f);

    private static HistoryFilter filter = new();

    /// <summary>The hand filter's pick (null = All hands): by alias, so it sits beside the filter.</summary>
    private static HandPick? hand;

    /// <summary>The history grows forever, so the page is rebuilt only when what it shows changed: a
    /// row landed, a filter changed, or an estate or a hand was named (<see cref="NamesFingerprint"/>).</summary>
    private static HistoryPage? page;
    private static HistoryFilter? pageFilter;
    private static HandPick? pageHand;
    private static int pageRows = -1;
    private static int pageNames;

    /// <summary>The days open, by their midnight: the newest when the tab opens, then the player's.</summary>
    private static HashSet<DateTimeOffset> openDays = [];

    /// <summary>Whether this tab was the one shown when the window last drew. A draw that finds it
    /// unset is the tab being opened (from another tab, or the first time); a window closed, collapsed
    /// or hidden draws nothing and leaves it set, so its open days stay.</summary>
    private static bool tabShown;

    /// <summary>The page as the board folds it (<see cref="GardenBoard.HistoryLines"/>), the same as the
    /// table draws it (<see cref="lines"/>, built with it in <see cref="Refold"/>), and each column's
    /// width over every row and heading of the page, open or folded (measured once per page, so the
    /// columns hold still while the list scrolls and while days open and fold).</summary>
    private static IReadOnlyList<HistoryLine> boardLines = [];
    private static List<Line> lines = [];
    private static float[] widths = [];

    /// <summary>Each row's cells, built once per page, so opening a day builds nothing. Keyed by the
    /// board's own row (never an id a hand-edited file could repeat).</summary>
    private static Dictionary<HistoryRowLine, Line> rowLines = new(ReferenceEqualityComparer.Instance);

    /// <summary>The held view: the line at the top of the table's view at its last draw, where it stood
    /// and the scroll it stood at. After a refold the table finds that line again and scrolls by as many
    /// rows as it moved, before its window begins, so the move lands in the same frame; a tab just
    /// opened goes to the top instead.</summary>
    private static HistoryLine? topLine;
    private static int topLineIndex;
    private static float topLineScroll;
    private static bool refolded;
    private static bool scrollToTop;

    /// <summary>A day heading the player clicked this frame, opened or folded after the table is drawn.</summary>
    private static DateTimeOffset? toggled;

    /// <summary>A line as the table draws it: a day heading or a row's cells.</summary>
    private abstract record Line;

    /// <summary>A day heading: its words (<see cref="WindowFormat.HistoryDay"/>), its midnight, and
    /// whether it is open.</summary>
    private sealed record DayLine(string Text, DateTimeOffset Day, bool Open) : Line;

    /// <summary>A row: one cell per column.</summary>
    private sealed record RowCells(Cell[] Cells) : Line;

    /// <summary>A cell's words and how they draw: dim, or in the onion mark's gold (an onion
    /// harvest's crop, and its yield when heard), else full.</summary>
    private sealed record Cell(string Text, bool Dim = false, bool Onion = false);

    public static void Draw()
    {
        using var tab = ImRaii.TabItem("History###history");
        if (!tab.Success)
        {
            tabShown = false;
            return;
        }
        var arriving = !tabShown;
        tabShown = true;

        var history = Plugin.Garden.History;
        var estates = Plugin.Garden.Ledger.Estates;
        var aliases = Plugin.Configuration.HandAliases;
        var names = NamesFingerprint(estates, aliases);
        if (page is null || pageFilter != filter || pageHand != hand || pageRows != history.Entries.Count || pageNames != names)
            Rebuild(history, estates, aliases, names);
        if (arriving)
        {
            openDays = [.. GardenBoard.HistoryOpenOnArrival(page!)];
            Refold();
            scrollToTop = true;
        }

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

        if (toggled is { } day)
        {
            toggled = null;
            if (!openDays.Remove(day))
                openDays.Add(day);
            Refold();
        }
    }

    /// <summary>A fingerprint of the estates' names and the hands' aliases, so a renamed estate or a
    /// hand named in the settings rebuilds the page. A handful of entries, read each frame.</summary>
    private static int NamesFingerprint(List<EstateRecord> estates, Dictionary<string, string> aliases)
    {
        var hash = new HashCode();
        foreach (var estate in estates)
        {
            hash.Add(estate.Key);
            hash.Add(estate.Nickname);
        }
        foreach (var (slug, alias) in aliases.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            hash.Add(slug);
            hash.Add(alias);
        }
        return hash.ToHashCode();
    }

    private static void Rebuild(GardenHistory history, List<EstateRecord> estates, Dictionary<string, string> aliases, int names)
    {
        page = GardenBoard.History(history, filter, hand, aliases, estates, Plugin.Tables, TimeZoneInfo.Local);
        hand = page.Hand; // the board drops a pick whose line is gone
        pageFilter = filter;
        pageHand = hand;
        pageRows = history.Entries.Count;
        pageNames = names;

        rowLines = new(ReferenceEqualityComparer.Instance);
        foreach (var row in page.Days.SelectMany(d => d.Rows))
            rowLines[row] = RowLine(row, aliases);
        Refold();

        // Every day has its heading whether open or folded, so the headings in the fold are all of them.
        widths = Columns.Select(c => ImGui.CalcTextSize(c).X).ToArray();
        foreach (var heading in lines.OfType<DayLine>())
            widths[0] = MathF.Max(widths[0], ImGui.CalcTextSize(heading.Text).X + ImGui.GetTreeNodeToLabelSpacing());
        foreach (var row in rowLines.Values.Cast<RowCells>())
            for (var c = 0; c < Columns.Length; c++)
                widths[c] = MathF.Max(widths[c], ImGui.CalcTextSize(row.Cells[c].Text).X);
    }

    /// <summary>Folds the page by the open days; the table then holds the reader's line still
    /// (<see cref="topLine"/>): a row landing above it, a new day arriving, a day opened or folded above
    /// it, or a filter change that keeps it moves nothing the reader sees; a line gone (filtered away)
    /// leaves the scroll where it was.</summary>
    private static void Refold()
    {
        boardLines = GardenBoard.HistoryLines(page!, openDays);
        lines = boardLines.Select(ToLine).ToList();
        refolded = true;
    }

    private static Line ToLine(HistoryLine line) => line switch
    {
        HistoryDayLine day => new DayLine(WindowFormat.HistoryDay(day.Day, day.Count), day.Day, day.Open),
        HistoryRowLine row => rowLines[row],
        _ => throw new ArgumentOutOfRangeException(nameof(line)),
    };

    private static float RowHeight() => ImGui.GetFontSize() + CellPadding.Y * 2f;

    /// <summary>A row's cells: an onion harvest's crop, and its yield when heard, wear the onion mark.</summary>
    private static Line RowLine(HistoryRowLine row, Dictionary<string, string> aliases)
    {
        var entry = row.Entry;
        var onion = GardenBoard.IsGoalCropHarvest(entry);
        var (yield, yieldDim) = WindowFormat.HistoryYield(entry);
        var (cross, crossDim) = WindowFormat.HistoryCross(entry, WindowText.SeedName);
        return new RowCells(
        [
            new Cell(WindowFormat.HistoryTime(row.LocalAt)),
            new Cell(WindowFormat.HistoryAct(entry.Kind)),
            new Cell(Plugin.Tables.SpeciesName(entry.Species), Onion: onion),
            new Cell(WindowFormat.HistoryWhere(row.EstateName, entry)),
            new Cell(WindowFormat.HistoryHand(entry, aliases)),
            new Cell(yield, yieldDim, Onion: onion && entry.Yields is { Count: > 0 }),
            new Cell(cross, crossDim),
        ]);
    }

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
        using (var combo = ImRaii.Combo("##history-hand", WindowFormat.HandChoice(hand)))
        {
            if (combo.Success)
            {
                // Each line carries its place as its id: an alias may read like another line ("You").
                var line = 0;
                foreach (var pick in current.Hands.Prepend(null))
                    if (ImGui.Selectable($"{WindowFormat.HandChoice(pick)}##hand-{line++}", hand == pick))
                        hand = pick;
            }
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
        using var pad = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, CellPadding);

        // Hold the view, set before the table's own window begins so it lands this frame, not the next:
        // the tab just opened starts at the top; after a refold the reader's line is found again and the
        // scroll moves by as many rows as it did.
        float? hold = null;
        if (scrollToTop)
            hold = 0f;
        else if (refolded && topLine is not null && GardenBoard.HistoryLineIndex(boardLines, topLine) is var now and >= 0 && now != topLineIndex)
            hold = topLineScroll + (now - topLineIndex) * RowHeight();
        if (hold is { } start)
            ImGuiP.SetNextWindowScroll(new Vector2(-1f, start));
        scrollToTop = false;
        refolded = false;

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

        // The window's scroll is held to last frame's height, so a hold past it (rows landed while the
        // reader sat at the very bottom) lands the rest on the next frame.
        if (hold is { } wanted && ImGui.GetScrollY() + 0.5f < wanted)
            ImGui.SetScrollY(wanted);
        topLineScroll = ImGui.GetScrollY();
        topLineIndex = (int)(topLineScroll / RowHeight());
        topLine = topLineIndex < boardLines.Count ? boardLines[topLineIndex] : null;

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
        if (line is DayLine heading)
        {
            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, Palette.TextDim))
            {
                // ### keeps the id to the day alone, so a count that changes mid-click keeps the click.
                ImGui.SetNextItemOpen(heading.Open, ImGuiCond.Always);
                ImGui.TreeNodeEx($"{heading.Text}###day-{heading.Day.UtcTicks}", ImGuiTreeNodeFlags.NoTreePushOnOpen);
            }
            if (ImGui.IsItemToggledOpen())
                toggled = heading.Day;
            return;
        }

        var cells = ((RowCells)line).Cells;

        for (var c = 0; c < Columns.Length; c++)
        {
            ImGui.TableNextColumn();
            var cell = cells[c];
            if (cell.Text.Length == 0)
                continue;
            if (c == YieldColumn)
                RightAlign(cell.Text, c);
            WindowText.Text(cell.Text, cell.Dim, cell.Onion);
        }
    }

    /// <summary>Moves the cursor so <paramref name="text"/> ends at its column's right edge.</summary>
    private static void RightAlign(string text, int column)
        => ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(widths[column] - ImGui.CalcTextSize(text).X, 0f));
}
