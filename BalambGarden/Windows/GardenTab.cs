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
/// the board's. A card carries no verbs: they live on the estate tabs.
/// </summary>
internal static class GardenTab
{
    private static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Amber = new(1f, 0.78f, 0.35f, 1f);
    private static readonly Vector4 Dim = new(0.55f, 0.55f, 0.55f, 1f);

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
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.35f, 0.35f, 1f)), 4f);
    }

    /// <summary>The big number: the water deadline and its countdown, coloured by the card's
    /// thirst. A card with no water clock says so plainly - pots are watched, not clocked.</summary>
    private static void DrawHeadline(VisitCard card, DateTimeOffset now)
    {
        ImGui.SetWindowFontScale(HeadlineScale);
        if (card.WaterBy is { } by)
        {
            var text = WindowFormat.WaterBy(by.ToLocalTime(), now.ToLocalTime());
            switch (card.Thirst)
            {
                case ContainerLook.PastDeadline: ImGui.TextColored(Red, text); break;
                case ContainerLook.WaterSoon: ImGui.TextColored(Amber, text); break;
                default: ImGui.Text(text); break;
            }
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

    private static void DrawNotes(VisitCard card)
    {
        if (card.TendUnheard > 0)
        {
            ImGui.TextColored(Amber, card.TendUnheard == 1
                ? "1 bed with no watering heard"
                : $"{card.TendUnheard} beds with no watering heard");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("their deadline counts from the sowing; a bed with no sowing heard either has no deadline at all");
        }

        if (card.PotsWilting > 0)
            ImGui.TextColored(Red, card.PotsWilting == 1 ? "pot wilting - water now" : $"{card.PotsWilting} pots wilting - water now");
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
            lit ? new Vector4(0.22f, 0.30f, 0.40f, 1f) : new Vector4(0.16f, 0.16f, 0.16f, 1f)), 6f);
        draw.AddText(pos + pad, ImGui.ColorConvertFloat4ToU32(lit ? Vector4.One : Dim), text);
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
            DashedRect(draw, min, max, ImGui.ColorConvertFloat4ToU32(Amber));
        else
            draw.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(cell.Look == ContainerLook.Ripe
                ? new Vector4(0.52f, 0.72f, 1f, 1f)
                : new Vector4(0.40f, 0.40f, 0.40f, 1f)), 3f);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(CellTooltip(patch, cell, now));
    }

    /// <summary>State colours: green ramp by stage while growing, amber water soon, red past the
    /// deadline or wilting, blue ripe (the 2026-08-18 ruling: ripe is a payoff, kept off the
    /// health gradient), earth for a known-empty container, grey for one never read, near-black
    /// for an unclaimed slot.</summary>
    private static Vector4 Fill(ContainerCell cell) => cell.Look switch
    {
        ContainerLook.Unclaimed => new Vector4(0.12f, 0.12f, 0.12f, 1f),
        ContainerLook.Unread => new Vector4(0.30f, 0.30f, 0.30f, 1f),
        ContainerLook.Empty => new Vector4(0.24f, 0.19f, 0.13f, 1f),
        ContainerLook.WaterSoon => new Vector4(0.62f, 0.46f, 0.16f, 1f),
        ContainerLook.PastDeadline or ContainerLook.Wilting => new Vector4(0.62f, 0.20f, 0.20f, 1f),
        ContainerLook.Ripe => new Vector4(0.22f, 0.45f, 0.80f, 1f),
        _ => Grow(cell.Stage),
    };

    private static Vector4 Grow(byte stage)
    {
        var t = Math.Clamp(stage / 3f, 0f, 1f);
        return new Vector4(0.13f + 0.10f * t, 0.28f + 0.44f * t, 0.16f + 0.12f * t, 1f);
    }

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
