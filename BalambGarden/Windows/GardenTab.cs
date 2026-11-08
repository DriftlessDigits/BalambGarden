using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace BalambGarden.Windows;

/// <summary>
/// The Garden tab: one card per planted estate, most urgent first, each leading with its water
/// deadline. Everything on it comes from <see cref="GardenBoard"/>; this file only draws - the
/// order, the deadlines, the counts, the groups, the card's thirst and every container's look are
/// the board's. A card carries no verbs: they live on the estate tabs. Every colour is a
/// <see cref="Palette"/> meaning.
/// </summary>
internal static class GardenTab
{
    private const float CardPad = 8f;
    private const float CellGap = 3f;
    private const float PictureGap = 14f;
    private const float HeadlineScale = 1.35f;

    public static void Draw(EstateKey? here, DateTimeOffset now)
    {
        using var tab = ImRaii.TabItem("Garden###garden");
        if (!tab.Success)
            return;

        var standing = here is { } at ? new Standing(at, EstateSensor.IsInside(), CensusPump.LastIndoor) : null;
        var cards = GardenBoard.Cards(
            Plugin.Garden.Ledger.Estates, Plugin.Garden.Census.LedgerBeds, RosterSensor.Current(),
            Plugin.Tables, now, standing);

        ImGui.Spacing();
        if (cards.Count == 0)
        {
            ImGui.TextDisabled("Nothing planted anywhere on your teleport list that Balamb has seen.");
            return;
        }

        foreach (var card in cards)
        {
            DrawCard(card, now);
            ImGui.Spacing();
        }

        DrawLanes(cards, now);
    }

    // ------------------------------------------------------------------ the timeline lanes

    private const float LaneLabelWidth = 120f;

    /// <summary>"The next six days" under the cards (ruling 17): one lane per card in card order,
    /// the water bar from <see cref="TimelineLane.WaterFrom"/> to the card's deadline drop, the ripe
    /// marks on a beds row and a pots row, a line per local midnight and one for now. The span and
    /// every mark are the board's; this only places them.</summary>
    private static void DrawLanes(IReadOnlyList<VisitCard> cards, DateTimeOffset now)
    {
        ImGui.Spacing();
        ImGui.TextDisabled("THE NEXT SIX DAYS");

        var span = GardenBoard.Span(now);
        var line = ImGui.GetTextLineHeight();
        var laneHeight = line * 3.2f;
        var left = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var trackX = left.X + LaneLabelWidth;
        var trackWidth = MathF.Max(width - LaneLabelWidth, 1f);
        float X(DateTimeOffset t) => trackX + trackWidth
            * (float)Math.Clamp((t - span.From).TotalSeconds / (span.To - span.From).TotalSeconds, 0d, 1d);

        var draw = ImGui.GetWindowDrawList();
        var midnights = span.Midnights(TimeZoneInfo.Local);

        // Day heads.
        foreach (var midnight in midnights)
            draw.AddText(new Vector2(X(midnight) + 4f, left.Y), ImGui.ColorConvertFloat4ToU32(Palette.TextDim),
                WindowFormat.DayHead(midnight));
        var lanesTop = left.Y + line;
        var lanesBottom = lanesTop + laneHeight * cards.Count;

        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            using var id = ImRaii.PushId(card.Estate.Key.BindingKey(0));
            var top = lanesTop + laneHeight * i;
            draw.AddLine(new Vector2(left.X, top + laneHeight), new Vector2(left.X + width, top + laneHeight),
                ImGui.ColorConvertFloat4ToU32(Palette.DayLine));
            draw.PushClipRect(new Vector2(left.X, top), new Vector2(trackX - 4f, top + laneHeight), true);
            draw.AddText(new Vector2(left.X + 4f, top + line * 0.2f), ImGui.GetColorU32(ImGuiCol.Text), card.Estate.DisplayName);
            draw.PopClipRect();

            foreach (var midnight in midnights)
                draw.AddLine(new Vector2(X(midnight), top), new Vector2(X(midnight), top + laneHeight),
                    ImGui.ColorConvertFloat4ToU32(Palette.DayLine));

            DrawWaterBar(card, top, line, X, now);

            var labelEnds = new Dictionary<LaneRow, float>();
            foreach (var mark in card.Lane.Marks.OrderBy(m => m.From))
                labelEnds[mark.Row] = DrawMark(mark, top + line * (mark.Row == LaneRow.Beds ? 1.15f : 2.15f), line * 0.7f,
                    labelEnds.GetValueOrDefault(mark.Row, trackX), trackX + trackWidth, X);
        }

        draw.AddLine(new Vector2(X(now), lanesTop), new Vector2(X(now), lanesBottom),
            ImGui.ColorConvertFloat4ToU32(Palette.NowLine), 2f);

        // The hovers placed the cursor where they sat; one item spanning the lanes tells the
        // window how tall they are.
        ImGui.SetCursorScreenPos(left);
        ImGui.Dummy(new Vector2(width, lanesBottom - left.Y));
    }

    /// <summary>The thin bar from the deadline bed's watering (or sowing) to the drop at the card's
    /// deadline, coloured by the card's thirst like the headline. No clock, no bar.</summary>
    private static void DrawWaterBar(VisitCard card, float top, float line, Func<DateTimeOffset, float> x, DateTimeOffset now)
    {
        if (card.WaterBy is not { } by || card.Lane.WaterFrom is not { } from)
            return;

        var draw = ImGui.GetWindowDrawList();
        var colour = ImGui.ColorConvertFloat4ToU32(ThirstColour(card.Thirst) ?? Palette.ClockFine);
        var y = top + line * 0.5f;
        draw.AddRectFilled(new Vector2(x(from), y - 2.5f), new Vector2(x(by), y + 2.5f), colour, 2.5f);

        // The drop: a round bottom with a point on top, at the deadline.
        var drop = new Vector2(x(by), y);
        var r = line * 0.28f;
        draw.AddCircleFilled(drop + new Vector2(0, r * 0.4f), r, colour);
        draw.AddTriangleFilled(drop + new Vector2(-r * 0.9f, 0), drop + new Vector2(r * 0.9f, 0), drop + new Vector2(0, -r * 1.6f), colour);

        ImGui.SetCursorScreenPos(drop - new Vector2(r, r * 1.6f));
        ImGui.InvisibleButton("drop", new Vector2(r * 2, r * 3));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{WindowFormat.WaterBy(by.ToLocalTime(), now.ToLocalTime())} ({WindowFormat.Countdown(by, now)})\n"
                             + "the bar is the first bed here to reach its crop's wilt hours, from its last watering\n"
                             + $"(or its sowing, when no watering was heard) {WindowFormat.Ago(from, now)}");
    }

    /// <summary>A ripe mark: a tick for an anchored window, a band as wide as its window (striped when
    /// estimated), a dot at the now line for what is seen ripe; its crops and glyph beside it.
    /// <paramref name="leftFree"/> is where the row's previous label ended; returns where this one ends.</summary>
    private static float DrawMark(LaneMark mark, float y, float height, float leftFree, float right, Func<DateTimeOffset, float> x)
    {
        var draw = ImGui.GetWindowDrawList();
        var ripe = ImGui.ColorConvertFloat4ToU32(Palette.Ripe);
        var x0 = x(mark.From);
        var x1 = x(mark.To);
        Vector2 min, max;
        switch (mark.Kind)
        {
            case LaneMarkKind.Tick:
                (min, max) = (new Vector2(x0 - 2f, y), new Vector2(x0 + 2f, y + height));
                draw.AddRectFilled(min, max, ripe, 1f);
                break;
            case LaneMarkKind.RipeNow:
                (min, max) = (new Vector2(x0 - height / 2, y), new Vector2(x0 + height / 2, y + height));
                draw.AddCircleFilled((min + max) / 2, height / 2, ripe);
                break;
            default:
                (min, max) = (new Vector2(x0, y), new Vector2(MathF.Max(x1, x0 + 4f), y + height));
                draw.AddRectFilled(min, max, ripe, height / 2);
                if (mark.Kind == LaneMarkKind.EstimatedBand)
                    Stripes(draw, min, max, ImGui.ColorConvertFloat4ToU32(Palette.RipeStripe));
                break;
        }

        var crops = string.Join(" + ", mark.Crops.Select(c => Plugin.Tables.SpeciesName(c)));
        var who = mark.Row == LaneRow.Pots ? $"pots ({crops})" : crops;
        var label = mark.Group is { } shown ? $"{who} {WindowFormat.Mark(shown.Window.Provenance)}" : $"{who} ripe now";
        // Beside the mark; before it when it would run off the lane's end and the row's previous
        // label leaves room; otherwise beside it, cut at the end (the hover has it whole).
        var labelWidth = ImGui.CalcTextSize(label).X;
        var labelX = max.X + 5f + labelWidth <= right || min.X - 5f - labelWidth < leftFree + 5f
            ? max.X + 5f
            : min.X - 5f - labelWidth;
        var labelPos = new Vector2(labelX, y + (height - ImGui.GetTextLineHeight()) / 2);
        draw.PushClipRect(new Vector2(leftFree, y - height), new Vector2(right, y + height * 2), true);
        draw.AddText(labelPos, ImGui.ColorConvertFloat4ToU32(Palette.RipeInk), label);
        draw.PopClipRect();
        var labelEnd = MathF.Max(labelX + labelWidth, max.X);

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"m{(int)mark.Row}-{mark.From.ToUnixTimeSeconds()}", Vector2.Max(max - min, new Vector2(4f, 4f)));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(mark.Group is { } group
                ? string.Join("\n\n", group.Crops.Select(c => $"{Plugin.Tables.SpeciesName(c.Species)}: {WindowText.WindowTooltip(c.Window)}"))
                : $"{crops}: seen ripe - harvest any time");
        return labelEnd;
    }

    /// <summary>Diagonal stripes across a band, clipped to it: estimated, not bracketed.</summary>
    private static void Stripes(ImDrawListPtr draw, Vector2 min, Vector2 max, uint colour)
    {
        draw.PushClipRect(min, max, true);
        var h = max.Y - min.Y;
        for (var sx = min.X - h; sx < max.X; sx += 6f)
            draw.AddLine(new Vector2(sx, max.Y), new Vector2(sx + h, min.Y), colour, 2f);
        draw.PopClipRect();
    }

    // ------------------------------------------------------------------ the card

    private static void DrawCard(VisitCard card, DateTimeOffset now)
    {
        using var id = ImRaii.PushId(card.Estate.Key.BindingKey(0));
        var draw = ImGui.GetWindowDrawList();
        var width = ImGui.GetContentRegionAvail().X;
        var top = ImGui.GetCursorScreenPos();

        ImGui.Dummy(new Vector2(0, CardPad / 2));
        ImGui.Indent(CardPad);
        using (ImRaii.Group())
        {
            ImGui.Text(card.Estate.DisplayName);
            DrawHeadline(card, now);
            DrawNotes(card);
            DrawChips(card);
            DrawRipe(card, now);
            ImGui.Dummy(new Vector2(0, 2));
            DrawPictures(card, now);
        }
        ImGui.Unindent(CardPad);
        ImGui.Dummy(new Vector2(0, CardPad / 2));

        var bottom = ImGui.GetItemRectMax().Y;
        draw.AddRect(top, new Vector2(top.X + width, bottom),
            ImGui.ColorConvertFloat4ToU32(Palette.Frame), 4f);
    }

    /// <summary>The big number: the water deadline and its countdown, coloured by the card's
    /// thirst. A card with no water clock says so plainly - pots are watched, not clocked.</summary>
    private static void DrawHeadline(VisitCard card, DateTimeOffset now)
    {
        ImGui.SetWindowFontScale(HeadlineScale);
        if (card.WaterBy is { } by)
        {
            var text = WindowFormat.WaterBy(by.ToLocalTime(), now.ToLocalTime());
            if (ThirstColour(card.Thirst) is { } colour)
                ImGui.TextColored(colour, text);
            else
                ImGui.Text(text);
            ImGui.SetWindowFontScale(1f);
            ImGui.SameLine();
            ImGui.TextDisabled(WindowFormat.Countdown(by, now));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"the first bed here reaches its crop's wilt hours at {WindowFormat.Exact(by.ToLocalTime())}");
            return;
        }

        ImGui.TextDisabled("no water clock here");
        ImGui.SetWindowFontScale(1f);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("no bed here carries a clock: pots are watched, not clocked - Balamb only knows\n"
                             + "a pot is wilting while you stand in its room");
    }

    /// <summary>The card's thirst as a colour, shared by the headline and the lane's water bar so
    /// the two cannot disagree: red past the deadline, amber water soon, null for plain.</summary>
    private static Vector4? ThirstColour(ContainerLook thirst) => thirst switch
    {
        ContainerLook.PastDeadline => Palette.DyingInk,
        ContainerLook.WaterSoon => Palette.WaterSoonInk,
        _ => null,
    };

    private static void DrawNotes(VisitCard card)
    {
        if (card.TendUnheard > 0)
        {
            ImGui.TextColored(Palette.WaterSoonInk, card.TendUnheard == 1
                ? "1 bed with no watering heard"
                : $"{card.TendUnheard} beds with no watering heard");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("their deadline counts from the sowing; a bed with no sowing heard either has no deadline at all");
        }

        if (card.PotsWilting > 0)
            ImGui.TextColored(Palette.DyingInk, card.PotsWilting == 1 ? "pot wilting - water now" : $"{card.PotsWilting} pots wilting - water now");
    }

    private static void DrawChips(VisitCard card)
    {
        var span = $"{GardenBoard.ChipSpan.TotalHours:0}h";
        Chip("water", card.Water, $"beds due within {span} of this card's deadline");
        ImGui.SameLine();
        Chip("harvest", card.Harvest, card.RipeNow > 0
            ? $"{card.RipeNow} seen ripe; the rest may ripen within {span}"
            : $"may ripen within {span}");
        ImGui.SameLine();
        Chip("replant", card.Replant, "emptied by a harvest or an empty read, nothing sown since");
    }

    /// <summary>A count with its task word, framed, dimmed at zero so the chips that matter stand out.</summary>
    private static void Chip(string label, int count, string tooltip)
    {
        var text = $"{label} {count}";
        var pad = new Vector2(6f, 2f);
        var size = ImGui.CalcTextSize(text) + pad * 2;
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        var lit = count > 0;
        draw.AddRectFilled(pos, pos + size, ImGui.ColorConvertFloat4ToU32(
            lit ? Palette.ChipLit : Palette.ChipUnlit), 6f);
        draw.AddText(pos + pad, ImGui.ColorConvertFloat4ToU32(lit ? Palette.Text : Palette.TextDim), text);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private static void DrawRipe(VisitCard card, DateTimeOffset now)
    {
        if (card.Ripe.Count == 0)
            return;

        ImGui.TextDisabled("ripe");
        for (var i = 0; i < card.Ripe.Count; i++)
        {
            var group = card.Ripe[i];
            ImGui.SameLine();
            var crops = string.Join(" + ", group.Crops.Select(c => Plugin.Tables.SpeciesName(c.Species)));
            var who = group.IsPots ? $"pots ({crops})" : crops;
            ImGui.Text($"{who} {WindowText.SpokenWindow(group.Window, now, approxMark: true)} {WindowFormat.Mark(group.Window.Provenance)}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n\n", group.Crops.Select(c =>
                    $"{Plugin.Tables.SpeciesName(c.Species)}: {WindowText.WindowTooltip(c.Window)}")));
            if (i < card.Ripe.Count - 1)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("·");
            }
        }
    }

    // ------------------------------------------------------------------ the garden picture

    /// <summary>Each patch as the game lays its beds out (<see cref="GardenBoard.RingPosition"/>),
    /// side by side, then the pots in map-key order. A dashed amber outline marks a bed whose tend
    /// was never heard.</summary>
    private static void DrawPictures(VisitCard card, DateTimeOffset now)
    {
        var side = ImGui.GetTextLineHeight() * 1.2f;
        var first = true;
        foreach (var patch in card.Patches.Where(p => !p.IsPots))
        {
            if (!first)
                ImGui.SameLine(0f, PictureGap);
            first = false;

            using (ImRaii.Group())
            {
                ImGui.TextDisabled($"patch {patch.Ordinal + 1}");
                var origin = ImGui.GetCursorScreenPos();
                foreach (var cell in patch.Cells)
                {
                    var (column, row) = GardenBoard.RingPosition(cell.Position);
                    ImGui.SetCursorScreenPos(origin + new Vector2(column, row) * (side + CellGap));
                    DrawContainer(patch, cell, side, now);
                }
                ImGui.SetCursorScreenPos(origin + new Vector2(0, 3 * (side + CellGap)));
                ImGui.Dummy(new Vector2(3 * (side + CellGap), 0));
            }
        }

        if (card.Patches.FirstOrDefault(p => p.IsPots) is not { } pots)
            return;
        if (!first)
            ImGui.SameLine(0f, PictureGap);
        using (ImRaii.Group())
        {
            ImGui.TextDisabled("pots");
            for (var i = 0; i < pots.Cells.Count; i++)
            {
                if (i > 0)
                    ImGui.SameLine(0f, CellGap);
                DrawContainer(pots, pots.Cells[i], side, now);
            }
        }
    }

    private static void DrawContainer(PatchPicture patch, ContainerCell cell, float side, DateTimeOffset now)
    {
        var draw = ImGui.GetWindowDrawList();
        ImGui.InvisibleButton($"c{patch.Ordinal?.ToString() ?? "p"}-{cell.Position}", new Vector2(side, side));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        draw.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(Fill(cell)), 3f);
        if (cell.TendUnheard)
            DashedRect(draw, min, max, ImGui.ColorConvertFloat4ToU32(Palette.WaterSoonInk));
        else
            draw.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(cell.Look == ContainerLook.Ripe
                ? Palette.RipeInk
                : Palette.Outline), 3f);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(CellTooltip(patch, cell, now));
    }

    /// <summary>State colours: green ramp by stage while growing, amber water soon, red past the
    /// deadline or wilting, blue ripe (the 2026-08-18 ruling: ripe is a payoff, kept off the
    /// health gradient), earth for a known-empty container, grey for one never read, near-black
    /// for an unclaimed slot.</summary>
    private static Vector4 Fill(ContainerCell cell) => cell.Look switch
    {
        ContainerLook.Unclaimed => Palette.NothingRecorded,
        ContainerLook.Unread => Palette.NeverRead,
        ContainerLook.Empty => Palette.Empty,
        ContainerLook.WaterSoon => Palette.WaterSoon,
        ContainerLook.PastDeadline or ContainerLook.Wilting => Palette.Dying,
        ContainerLook.Ripe => Palette.Ripe,
        _ => Palette.Growing(cell.Stage),
    };

    private static void DashedRect(ImDrawListPtr draw, Vector2 min, Vector2 max, uint colour)
    {
        const float dash = 3f;
        void Line(Vector2 a, Vector2 b)
        {
            var length = Vector2.Distance(a, b);
            var dir = (b - a) / length;
            for (var d = 0f; d < length; d += dash * 2)
                draw.AddLine(a + dir * d, a + dir * MathF.Min(d + dash, length), colour);
        }
        Line(min, new Vector2(max.X, min.Y));
        Line(new Vector2(max.X, min.Y), max);
        Line(max, new Vector2(min.X, max.Y));
        Line(new Vector2(min.X, max.Y), min);
    }

    private static string CellTooltip(PatchPicture patch, ContainerCell cell, DateTimeOffset now)
    {
        var name = patch.IsPots ? $"Pot {cell.Position}" : $"Bed {cell.Position + 1}";
        switch (cell.Look)
        {
            case ContainerLook.Unclaimed:
                return $"{name}: nothing recorded here";
            case ContainerLook.Unread:
                return $"{name}: recorded, contents not read yet";
            case ContainerLook.Empty:
                return $"{name}: empty - harvested or read empty, nothing sown since";
        }

        var lines = new List<string> { $"{name}: {Plugin.Tables.SpeciesName(cell.Species)}, stage {cell.Stage}" };
        if (cell.Look == ContainerLook.Ripe)
            lines.Add("ripe now");
        else if (cell.Ripe is { } ripe)
            lines.Add($"ripe ~{WindowFormat.Range(ripe.Earliest.ToLocalTime(), ripe.Latest.ToLocalTime())} {WindowFormat.Mark(ripe.Provenance)}");

        if (patch.IsPots)
            lines.Add(cell.Look == ContainerLook.Wilting ? "wilting - water now" : "pots are watched, not clocked");
        else if (cell.WaterBy is { } by)
        {
            lines.Add($"{WindowFormat.WaterBy(by.ToLocalTime(), now.ToLocalTime())} ({WindowFormat.Countdown(by, now)})");
            lines.Add(cell.Bed?.LastTended is { } tended
                ? $"watered {WindowFormat.Ago(tended, now)}"
                : "no watering heard - counting from the sowing");
        }
        else if (cell.Look != ContainerLook.Ripe)
            lines.Add("no watering or sowing heard - no deadline");

        return string.Join("\n", lines);
    }
}
