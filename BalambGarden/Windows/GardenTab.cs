using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using BalambGarden.Chains;
using BalambGarden.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace BalambGarden.Windows;

/// <summary>
/// The Garden tab: one card per planted estate, most urgent first, each leading with its water
/// deadline. Everything on it comes from <see cref="GardenBoard"/>; this file only draws - the
/// order, the deadlines, the counts, the groups, the card's thirst, every container's look and
/// what each container's menu offers are the board's. The card of the estate the player stands at
/// (the here card, ruling 18) carries the verbs: Tend All and Replant ripe on it, and a click on
/// a patch or a container in its picture opens a small menu (ruling 19: not modal, closed by a
/// press or a click elsewhere) whose presses go through <see cref="Chains.GardenVerbs"/>; every
/// other card carries Teleport, fired only by its click. Every colour value comes from
/// <see cref="Palette"/>; plain ImGui text keeps the theme's colour.
/// </summary>
internal static class GardenTab
{
    private const float CardPad = 8f;
    private const float CellGap = 3f;
    private const float PictureGap = 14f;
    private const float HeadlineScale = 1.35f;

    /// <summary>The here card's picture is this much larger (ruling 18: it grows).</summary>
    private const float HereScale = 1.5f;

    /// <summary>What the game puts in front of the player at the here card, read once a frame:
    /// the patches outside, the pots inside, and whether Balamb acts here at all.</summary>
    private sealed record HereObjects(EstateRecord Estate, List<PatchGroup> Patches, List<PotObject> Pots,
        bool Covered);

    /// <summary>A press's refusal, said on the here card for a few seconds (a refusal starts no run,
    /// so it has no run log line to live in).</summary>
    private static string notice = "";
    private static DateTime noticeUntil;

    public static void Draw(Plugin plugin, EstateKey? here, DateTimeOffset now)
    {
        using var tab = ImRaii.TabItem("Garden###garden");
        if (!tab.Success)
            return;

        var cards = GardenBoard.Cards(
            Plugin.Garden.Ledger.Estates, Plugin.Garden.Census.LedgerBeds, RosterSensor.Current(),
            Plugin.Tables, now, CensusPump.StandingAt(here), CensusPump.Looks.Holds);

        ImGui.Spacing();
        if (cards.Count == 0)
        {
            ImGui.TextDisabled("Nothing planted anywhere on your teleport list that Balamb has seen.");
            return;
        }

        foreach (var card in cards)
        {
            DrawCard(plugin, card, card.Estate.Key == here ? ObjectsAt(card.Estate) : null, now);
            ImGui.Spacing();
        }

        DrawLanes(cards, now);
    }

    private static HereObjects ObjectsAt(EstateRecord estate)
    {
        var inside = EstateSensor.IsInside();
        return new HereObjects(estate,
            !inside && !estate.Key.IsIndoorOnly ? ObjectSensor.Patches() : [],
            inside ? ObjectSensor.AllPots() : [],
            CensusPump.CoveredHere);
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

    /// <summary>The thin bar from the watering (or sowing) of the bed or pot that sets the card's
    /// deadline to the drop at that deadline, coloured by the card's thirst like the headline. No
    /// clock, no bar.</summary>
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
            ImGui.SetTooltip(WindowFormat.DropHead(by.ToLocalTime(), card.DeadlineSeenFine, now.ToLocalTime())
                             + "\n" + WindowFormat.WaterFrom(card.FirstToWilt, card.Lane.FromSowing, from, now, card.Lane.SeenFine)
                             + WorstLine(card));
    }

    /// <summary>The card's worst look and what wears it, for the headline and drop hovers, on a line
    /// of its own; empty while nothing here asks for water.</summary>
    private static string WorstLine(VisitCard card)
        => WindowFormat.Worst(card.Worst, card.Thirst, card.WorstByClock) is { } line ? $"\n{line}" : "";

    /// <summary>A ripe mark: a tick for an anchored window, a band as wide as its window (striped when
    /// estimated), a dot at the now line for what is seen ripe; its crops and glyph beside it.
    /// <paramref name="leftFree"/> is where the row's previous label ended; returns where this one ends.</summary>
    private static float DrawMark(LaneMark mark, float y, float height, float leftFree, float right, Func<DateTimeOffset, float> x)
    {
        var draw = ImGui.GetWindowDrawList();
        var ripe = ImGui.ColorConvertFloat4ToU32(Palette.Ripe);
        var x0 = x(mark.From);
        var x1 = x(mark.To);
        // What is ripe now, seen and by the clock, both sit at the now line: the second dot goes
        // after the first one's label rather than under it.
        if (mark.Kind == LaneMarkKind.RipeByClock)
            x0 = MathF.Max(x0, leftFree + 6f + height / 2);
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
            case LaneMarkKind.RipeByClock:
                // Hollow: the clock's word alone, the game has not shown it (spec ruling 24).
                (min, max) = (new Vector2(x0 - height / 2, y), new Vector2(x0 + height / 2, y + height));
                draw.AddCircle((min + max) / 2, height / 2 - 1f, ripe, 0, 2f);
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
        var label = mark.Group is { } shown ? $"{who} {WindowFormat.Mark(shown.Window.Provenance)}"
            : WindowFormat.LaneRipeLabel(who, mark.Kind == LaneMarkKind.RipeByClock);
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
                : WindowFormat.LaneRipeHover(crops, mark.Kind == LaneMarkKind.RipeByClock));
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

    private static void DrawCard(Plugin plugin, VisitCard card, HereObjects? here, DateTimeOffset now)
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
            if (here is not null)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("· you are here");
            }
            else
                DrawTeleport(plugin, card.Estate);
            DrawHeadline(card, now);
            DrawNotes(card, now);
            DrawChips(card);
            DrawRipe(card, now);
            if (here is not null)
                DrawHereButtons(plugin, card, here);
            ImGui.Dummy(new Vector2(0, 2));
            DrawPictures(plugin, card, here, now);
        }
        ImGui.Unindent(CardPad);
        ImGui.Dummy(new Vector2(0, CardPad / 2));

        var bottom = ImGui.GetItemRectMax().Y;
        draw.AddRect(top, new Vector2(top.X + width, bottom),
            ImGui.ColorConvertFloat4ToU32(here is null ? Palette.Frame : Palette.HereFrame), 4f, ImDrawFlags.None,
            here is null ? 1f : 2f);
    }

    // ------------------------------------------------------------------ the here card's verbs

    /// <summary>Teleport, on every card but the here card (ruling 18), firing only on its click
    /// (<see cref="EstatePort"/>'s law). It exists only when the game's own teleport list uniquely
    /// names the estate; the cast gate is the game's own answer.</summary>
    private static void DrawTeleport(Plugin plugin, EstateRecord estate)
    {
        if (EstatePort.FindFor(estate.Key) is not { } dest)
            return;

        var ready = EstatePort.Castable(out var why);
        ImGui.SameLine(0f, 10f);
        using (ImRaii.Disabled(!ready || plugin.AnyChainBusy))
        {
            if (ImGui.SmallButton("Teleport"))
                EstatePort.Fire(dest, estate.DisplayName);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(plugin.AnyChainBusy
                ? "a chain is running - teleporting would strand it"
                : ready
                    ? $"Teleport to {estate.DisplayName} ({dest.GilCost:N0} gil)"
                    : why);
    }

    /// <summary>The here card's two buttons: outside, Tend All over the patches in reach and a
    /// Replant ripe per patch in reach holding something ripe (the cycle runs one patch at a time);
    /// inside, the pots' Water All and Replant ripe. Each claims what the press will act on, as the
    /// estate tabs' buttons do, and greys while a run goes.</summary>
    private static void DrawHereButtons(Plugin plugin, VisitCard card, HereObjects here)
    {
        using (ImRaii.Disabled(plugin.AnyChainBusy || !here.Covered))
        {
            if (here.Patches.Count > 0)
                DrawPatchButtons(plugin, card, here);
            else if (here.Pots.Count > 0)
                DrawPotButtons(plugin, card, here);
        }

        if (notice.Length > 0 && DateTime.UtcNow < noticeUntil)
            ImGui.Text(notice);
    }

    private static void DrawPatchButtons(Plugin plugin, VisitCard card, HereObjects here)
    {
        var estate = here.Estate.Key;
        var inReach = here.Patches.Where(p => p.InReach).OrderBy(p => p.Ordinal).ToList();
        if (inReach.Count == 0)
        {
            ImGui.TextDisabled("No beds in reach - walk to a patch.");
            return;
        }

        var sweep = inReach.Select(p => (Patch: p, Beds: GardenVerbs.SweepBeds(estate, p))).ToList();
        var totalBeds = sweep.Sum(s => s.Beds.Count);
        var drewOne = false;
        if (totalBeds == 0)
            ImGui.TextDisabled("Every bed in reach is ripe or empty - nothing to water.");
        else
        {
            var names = string.Join(", ", sweep.Select(s => s.Patch.Ordinal + 1));
            var word = inReach.Count > 1 ? "Patches" : "Patch";
            if (ImGui.Button($"Tend All ({word} {names} · {totalBeds} beds)"))
                plugin.Verbs.TendMany(sweep.SelectMany(s => s.Beds).ToList());
            Tip(plugin, here, "Waters every growing bed of every patch in reach:\n" + string.Join("\n",
                here.Patches.OrderBy(p => p.Ordinal).Select(p =>
                {
                    if (!p.InReach)
                        return $"Patch {p.Ordinal + 1} - out of reach ({p.Distance:F1}y)";
                    var visiting = sweep.FirstOrDefault(s => s.Patch == p).Beds?.Count ?? 0;
                    var left = p.Beds.Count - visiting;
                    return $"Patch {p.Ordinal + 1} - in reach ({visiting} beds"
                        + (left > 0 ? $"; {left} ripe/empty skipped)" : ")");
                })));
            drewOne = true;
        }

        foreach (var patch in inReach.Where(p => GardenVerbs.AnyRipeOnCard(card, p.Ordinal)))
        {
            if (drewOne)
                ImGui.SameLine();
            drewOne = true;
            if (ImGui.Button($"Replant ripe (patch {patch.Ordinal + 1})##replant{patch.Ordinal}")
                && plugin.Verbs.ReplantPatch(estate, patch) is { } refusal)
                Notice(refusal);
            Tip(plugin, here, ReplantPatchTip);
        }
    }

    private static void DrawPotButtons(Plugin plugin, VisitCard card, HereObjects here)
    {
        var waterable = GardenVerbs.WaterablePots(here.Pots);
        var ripeInReach = GardenVerbs.AnyRipePotOnCardInReach(card, here.Pots);
        if (waterable.Count > 0)
        {
            var word = waterable.Count > 1 ? "pots" : "pot";
            if (ImGui.Button($"Water All ({waterable.Count} {word})"))
                plugin.Verbs.WaterPots(waterable);
            Tip(plugin, here, "Water every planted pot in reach, one run.");
        }

        if (!ripeInReach)
            return;
        if (waterable.Count > 0)
            ImGui.SameLine();
        if (ImGui.Button("Replant ripe##pots") && plugin.Verbs.ReplantPots(RecordsAt(here)) is { } refusal)
            Notice(refusal);
        Tip(plugin, here, "Harvest + replant every ripe pot with the same plant."
            + "\nSeed comes from its species, soil from your bags.");
    }

    private const string ReplantPatchTip = "Harvest + replant every ripe bed with the same crop."
        + "\nSeeds from the ledger, topsoil from your bags. Beds still"
        + "\ngrowing are left alone.";

    /// <summary>A button's hover: the run that greys it, the ground Balamb does not act on, else its own words.</summary>
    private static void Tip(Plugin plugin, HereObjects here, string text)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            return;
        ImGui.SetTooltip(plugin.AnyChainBusy ? "a run is going - one chain at a time"
            : !here.Covered ? "Balamb doesn't act here - not on your teleport list"
            : text);
    }

    private static void Notice(string text)
    {
        notice = text;
        noticeUntil = DateTime.UtcNow.AddSeconds(6);
    }

    private static List<ClaimedBed> RecordsAt(HereObjects here)
        => Plugin.Garden.Census.LedgerBeds.Where(b => b.Estate == here.Estate.Key).ToList();

    /// <summary>A patch's menu: Water Patch over its sweep and, with something ripe in it, its
    /// Replant ripe; out of reach, how far away it is.</summary>
    private static void DrawPatchMenu(Plugin plugin, VisitCard card, HereObjects here, int ordinal)
    {
        ImGui.TextDisabled($"Patch {ordinal + 1}");
        var patch = here.Patches.FirstOrDefault(p => p.Ordinal == ordinal);
        if (patch is not { InReach: true })
        {
            ImGui.TextDisabled(patch is null ? "walk closer" : $"{patch.Distance:F1}y away - walk closer");
            return;
        }

        using (ImRaii.Disabled(plugin.AnyChainBusy || !here.Covered))
        {
            var sweep = GardenVerbs.SweepBeds(here.Estate.Key, patch);
            if (sweep.Count == 0)
                ImGui.TextDisabled("ripe or empty - nothing to water");
            else if (ImGui.SmallButton("Water Patch"))
            {
                plugin.Verbs.TendMany(sweep);
                ImGui.CloseCurrentPopup();
            }

            if (GardenVerbs.AnyRipeOnCard(card, ordinal))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Replant ripe"))
                {
                    if (plugin.Verbs.ReplantPatch(here.Estate.Key, patch) is { } refusal)
                        Notice(refusal);
                    ImGui.CloseCurrentPopup();
                }
                Tip(plugin, here, ReplantPatchTip);
            }
        }
    }

    /// <summary>A bed's or pot's menu: the presses its state allows (<see cref="GardenBoard.VerbsFor"/>),
    /// on the object in front of the player; out of reach, how far away it is.</summary>
    private static void DrawCellMenu(Plugin plugin, PatchPicture picture, ContainerCell cell, HereObjects here)
    {
        ImGui.TextDisabled(MenuTitle(picture, cell));
        var verbs = GardenBoard.VerbsFor(cell);
        if (verbs.Count == 0)
        {
            if (cell.Look is ContainerLook.Empty or ContainerLook.ReadsEmpty)
                ImGui.TextDisabled(picture.IsPots
                    ? $"sow it with Plant... on the {here.Estate.DisplayName} tab"
                    : $"sow it with Cycle... on the {here.Estate.DisplayName} tab");
            return;
        }

        using var disabled = ImRaii.Disabled(plugin.AnyChainBusy || !here.Covered);
        if (picture.IsPots)
            DrawPotMenu(plugin, cell, verbs, here);
        else
            DrawBedMenu(plugin, picture.Ordinal!.Value, cell, verbs, here);
    }

    private static void DrawBedMenu(Plugin plugin, int ordinal, ContainerCell cell, IReadOnlyList<CellVerb> verbs,
        HereObjects here)
    {
        var patch = here.Patches.FirstOrDefault(p => p.Ordinal == ordinal);
        var bed = patch?.Beds.FirstOrDefault(b => b.Gimmick.BedIndex == cell.Position);
        if (patch is null || bed is not { InReach: true } target)
        {
            ImGui.TextDisabled(bed is { } far ? $"{far.Distance:F1}y away - walk closer"
                : patch is not null ? $"{patch.Distance:F1}y away - walk closer" : "walk closer");
            return;
        }

        VerbButtons(verbs, "Tend", verb => verb switch
        {
            CellVerb.Tend => Started(() => plugin.Verbs.TendOne(target)),
            CellVerb.Harvest => plugin.Verbs.HarvestBed(patch, cell.Position),
            _ => plugin.Verbs.ReplantBed(here.Estate.Key, patch, cell.Position),
        });
    }

    /// <summary>A menu's presses in one row; a press runs its verb, closes the menu, and shows the
    /// verb's refusal on the card if it had one. <paramref name="tendWord"/> is a pot's "Water".</summary>
    private static void VerbButtons(IReadOnlyList<CellVerb> verbs, string tendWord, Func<CellVerb, string?> press)
    {
        for (var i = 0; i < verbs.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine();
            var word = verbs[i] switch { CellVerb.Tend => tendWord, CellVerb.Harvest => "Harvest", _ => "Replant" };
            if (!ImGui.SmallButton(word))
                continue;
            if (press(verbs[i]) is { } refusal)
                Notice(refusal);
            ImGui.CloseCurrentPopup();
        }
    }

    /// <summary>A verb that always starts its run: no refusal to show.</summary>
    private static string? Started(Action start)
    {
        start();
        return null;
    }

    private static void DrawPotMenu(Plugin plugin, ContainerCell cell, IReadOnlyList<CellVerb> verbs, HereObjects here)
    {
        var found = here.Pots.FindIndex(p => p.MapKey == cell.Position);
        if (found < 0 || !here.Pots[found].InReach)
        {
            ImGui.TextDisabled(found >= 0 ? $"{here.Pots[found].Distance:F1}y away - walk closer" : "walk closer");
            return;
        }

        var pot = here.Pots[found];
        VerbButtons(verbs, "Water", verb => verb switch
        {
            CellVerb.Tend => Started(() => plugin.Verbs.WaterPot(pot)),
            CellVerb.Harvest => Started(() => plugin.Verbs.HarvestPot(pot)),
            _ => cell.Bed is { } record ? plugin.Verbs.ReplantPot(record, pot) : null,
        });
    }

    /// <summary>A menu's first line: the container and what it holds ("Bed 1 · Krakka Root, ripe").</summary>
    private static string MenuTitle(PatchPicture picture, ContainerCell cell)
    {
        var name = picture.IsPots ? $"Pot {cell.Position}" : $"Bed {cell.Position + 1}";
        var crop = cell.Species != 0 ? $"{Plugin.Tables.SpeciesName(cell.Species)}, " : "";
        return cell.Look switch
        {
            ContainerLook.Empty => $"{name} · empty",
            ContainerLook.ReadsEmpty => $"{name} · reads empty now",
            ContainerLook.Unread or ContainerLook.Unclaimed => name,
            ContainerLook.Dead => $"{name} · {crop}dead",
            ContainerLook.Ripe => $"{name} · {crop}ripe",
            _ => $"{name} · {crop}stage {cell.Stage}",
        };
    }

    /// <summary>The big number: the water deadline and its countdown, coloured by the card's
    /// thirst; its hover names what wilts first, by kind and place (spec ruling 23). A card with no
    /// water clock says so plainly.</summary>
    private static void DrawHeadline(VisitCard card, DateTimeOffset now)
    {
        ImGui.SetWindowFontScale(HeadlineScale);
        if (card.WaterBy is { } by)
        {
            var (text, aside) = WindowFormat.Headline(by.ToLocalTime(), card.DeadlineSeenFine, now.ToLocalTime());
            if (ThirstColour(card.Thirst) is { } colour)
                ImGui.TextColored(colour, text);
            else
                ImGui.Text(text);
            ImGui.SetWindowFontScale(1f);
            ImGui.SameLine();
            ImGui.TextDisabled(aside);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(WindowFormat.HeadlineHover(card.FirstToWilt, by.ToLocalTime(), card.DeadlineSeenFine) + WorstLine(card));
            return;
        }

        ImGui.TextDisabled("no water clock here");
        ImGui.SetWindowFontScale(1f);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(WindowFormat.NoDeadline);
    }

    /// <summary>The card's thirst as a colour, shared by the headline and the lane's water bar so
    /// the two cannot disagree: red dying, purple wilting, amber water soon, null for plain.</summary>
    private static Vector4? ThirstColour(ContainerLook thirst) => thirst switch
    {
        ContainerLook.Dying => Palette.DyingInk,
        ContainerLook.Wilting => Palette.WiltingInk,
        ContainerLook.WaterSoon => Palette.WaterSoonInk,
        _ => null,
    };

    /// <summary>The card's notes: the beds and pots with no watering recorded, in plain words (a
    /// caution is a word, not a colour), and the pots the game showed wilting, purple: the live
    /// map's where the player stands, the last sighting's elsewhere, with its age.</summary>
    private static void DrawNotes(VisitCard card, DateTimeOffset now)
    {
        if (card.TendUnheard.Total > 0)
        {
            ImGui.Text(WindowFormat.NoWatering(card.TendUnheard));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("clock counts from the sowing");
        }

        if (card.PotsWilting > 0)
            ImGui.TextColored(Palette.WiltingInk, WindowFormat.PotsWilting(card.PotsWilting, card.PotsWiltingSeenAt, now));
    }

    private static void DrawChips(VisitCard card)
    {
        var span = $"{GardenBoard.ChipSpan.TotalHours:0}h";
        Chip("water", card.Water.Total, WindowFormat.WaterDue(card.Water, span));
        ImGui.SameLine();
        Chip("harvest", card.Harvest, WindowFormat.HarvestDue(card.RipeNow, card.RipeByClock, span));
        ImGui.SameLine();
        Chip("replant", card.Replant, WindowFormat.ReplantDue(card.Replant, card.Dead, card.DeadByClock));
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
    /// side by side, then the pots in map-key order. A dashed outline marks a bed or pot with no
    /// watering recorded, or one that reads empty now. On the here card the picture is larger, each
    /// patch says whether it is in reach (one out of reach is dimmed), and a click on a patch (its
    /// label or the middle of its ring) or on a container opens its menu.</summary>
    private static void DrawPictures(Plugin plugin, VisitCard card, HereObjects? here, DateTimeOffset now)
    {
        var side = ImGui.GetTextLineHeight() * 1.2f * (here is null ? 1f : HereScale);
        var first = true;
        foreach (var patch in card.Patches.Where(p => !p.IsPots))
        {
            if (!first)
                ImGui.SameLine(0f, PictureGap);
            first = false;

            var sensed = here?.Patches.FirstOrDefault(p => p.Ordinal == patch.Ordinal);
            var alpha = here is not null && sensed is not { InReach: true } ? Palette.OutOfReachAlpha : 1f;
            using (ImRaii.Group())
            {
                var menu = $"patch{patch.Ordinal}";
                if (here is null)
                    ImGui.TextDisabled($"patch {patch.Ordinal + 1}");
                else
                {
                    var label = sensed is null ? $"patch {patch.Ordinal + 1}"
                        : sensed.InReach ? $"patch {patch.Ordinal + 1} · in reach"
                        : $"patch {patch.Ordinal + 1} · {sensed.Distance:F1}y";
                    using (ImRaii.PushColor(ImGuiCol.Text, Palette.TextDim))
                        if (ImGui.Selectable(label, ImGui.IsPopupOpen(menu), ImGuiSelectableFlags.None,
                                new Vector2(3 * (side + CellGap) - CellGap, 0)))
                            ImGui.OpenPopup(menu);
                }

                var origin = ImGui.GetCursorScreenPos();
                foreach (var cell in patch.Cells)
                {
                    var (column, row) = GardenBoard.RingPosition(cell.Position);
                    ImGui.SetCursorScreenPos(origin + new Vector2(column, row) * (side + CellGap));
                    DrawContainer(plugin, patch, cell, side, alpha, here, now);
                }

                if (here is not null)
                {
                    // The empty middle of the ring is the patch itself.
                    ImGui.SetCursorScreenPos(origin + new Vector2(side + CellGap));
                    if (ImGui.InvisibleButton($"middle{patch.Ordinal}", new Vector2(side, side)))
                        ImGui.OpenPopup(menu);
                    using var popup = ImRaii.Popup(menu);
                    if (popup.Success)
                        DrawPatchMenu(plugin, card, here, patch.Ordinal!.Value);
                }

                ImGui.SetCursorScreenPos(origin + new Vector2(0, 3 * (side + CellGap)));
                ImGui.Dummy(new Vector2(3 * (side + CellGap), 0));
            }
        }

        if (card.Patches.FirstOrDefault(p => p.IsPots) is not { } pots)
            return;
        if (!first)
            ImGui.SameLine(0f, PictureGap);
        // Standing outside, the pots are in the house: dimmed like a patch out of reach.
        var potsAlpha = here is not null && here.Pots.Count == 0 ? Palette.OutOfReachAlpha : 1f;
        using (ImRaii.Group())
        {
            ImGui.TextDisabled("pots");
            for (var i = 0; i < pots.Cells.Count; i++)
            {
                if (i > 0)
                    ImGui.SameLine(0f, CellGap);
                DrawContainer(plugin, pots, pots.Cells[i], side, potsAlpha, here, now);
            }
        }
    }

    private static uint U32(Vector4 colour, float alpha) => ImGui.ColorConvertFloat4ToU32(colour with { W = colour.W * alpha });

    private static void DrawContainer(Plugin plugin, PatchPicture patch, ContainerCell cell, float side, float alpha,
        HereObjects? here, DateTimeOffset now)
    {
        var draw = ImGui.GetWindowDrawList();
        var id = $"c{patch.Ordinal?.ToString() ?? "p"}-{cell.Position}";
        var clicked = ImGui.InvisibleButton(id, new Vector2(side, side));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        if (cell.ByClock)
        {
            // Hollow: the state's colour as a ring, the clock's word alone (spec ruling 24, the
            // plan picture's B). Filled is what the game showed.
            draw.AddRectFilled(min, max, U32(Palette.Hollow, alpha), 3f);
            var ring = cell.Look == ContainerLook.Dead ? Palette.DeadRing : Fill(cell);
            draw.AddRect(min + new Vector2(1.5f), max - new Vector2(1.5f), U32(ring, alpha), 3f, ImDrawFlags.None, 3f);
            // No watering recorded keeps its dashed outline, outside the ring.
            if (cell.TendUnheard)
                DashedRect(draw, min, max, U32(Palette.Dashed, alpha));
        }
        else
        {
            draw.AddRectFilled(min, max, U32(Fill(cell), alpha), 3f);
            // Dashed: no watering recorded, or empty on the live map but not confirmed (ruling 22).
            if (cell.TendUnheard || cell.Look == ContainerLook.ReadsEmpty)
                DashedRect(draw, min, max, U32(Palette.Dashed, alpha));
            else
                draw.AddRect(min, max, U32(cell.Look == ContainerLook.Ripe
                    ? Palette.RipeInk
                    : Palette.Outline, alpha), 3f);
        }

        // A dead plant wears a cross over its fill: the shape says dead, the earth says sow here.
        if (cell.Look == ContainerLook.Dead)
        {
            var inset = new Vector2(side * 0.22f);
            var ink = U32(Palette.DeadMark, alpha);
            draw.AddLine(min + inset, max - inset, ink, 2f);
            draw.AddLine(new Vector2(max.X - inset.X, min.Y + inset.Y), new Vector2(min.X + inset.X, max.Y - inset.Y), ink, 2f);
        }

        // A pot whose clock rests on the seen-fine floor alone wears a "?": no watering recorded
        // under its clock (ruled 2026-11-10 at slice 21's plan call; an uncoloured mark).
        if (cell.SeenFineAt is not null)
        {
            var size = ImGui.CalcTextSize("?");
            draw.AddText((min + max - size) * 0.5f, U32(Palette.Text, alpha), "?");
        }

        if (hovered)
            ImGui.SetTooltip(CellTooltip(patch, cell, now));

        // The here card: a click opens the container's menu; it closes on a press or a click elsewhere.
        if (here is null || cell.Look == ContainerLook.Unclaimed)
            return;
        var menu = $"menu-{id}";
        if (clicked)
            ImGui.OpenPopup(menu);
        if (ImGui.IsPopupOpen(menu))
            draw.AddRect(min - new Vector2(2f), max + new Vector2(2f), U32(Palette.MenuOpen, 1f), 4f, ImDrawFlags.None, 2f);
        using var popup = ImRaii.Popup(menu);
        if (popup.Success)
            DrawCellMenu(plugin, patch, cell, here);
    }

    /// <summary>State colours (the colour map): green ramp by stage while growing, amber water soon,
    /// purple wilting, red dying, blue ripe (the 2026-08-18 ruling: ripe is a payoff, kept off the
    /// health gradient), earth for a known-empty container, earth under a cross for a dead plant,
    /// grey for one never read, near-black for an unclaimed slot.</summary>
    private static Vector4 Fill(ContainerCell cell) => cell.Look switch
    {
        ContainerLook.Unclaimed => Palette.Unclaimed,
        ContainerLook.Unread => Palette.Unread,
        ContainerLook.Empty or ContainerLook.ReadsEmpty => Palette.Empty,
        ContainerLook.Dead => Palette.Dead,
        ContainerLook.WaterSoon => Palette.WaterSoon,
        ContainerLook.Wilting => Palette.Wilting,
        ContainerLook.Dying => Palette.Dying,
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
            case ContainerLook.ReadsEmpty:
                return $"{name}: reads empty now - harvested?";
            case ContainerLook.Dead when cell.ByClock && cell.ClockSince is { } died:
                return WindowFormat.DeadByClockHover(name, Plugin.Tables.SpeciesName(cell.Species),
                    died.ToLocalTime(), cell.Bed?.LastTended?.ToLocalTime());
            case ContainerLook.Dead:
                return WindowFormat.DeadHover(name, Plugin.Tables.SpeciesName(cell.Species), cell.SeenDeadAt, now);
        }

        var lines = new List<string> { $"{name}: {Plugin.Tables.SpeciesName(cell.Species)}, stage {cell.Stage}" };
        if (cell.Look == ContainerLook.Ripe && cell.ByClock && cell is { ClockSince: { } since, Clock: { } clock, Bed.Latest: { } read })
            lines.Add(WindowFormat.RipeByClock(since.ToLocalTime(), clock.SownAt.ToLocalTime(), clock.Crop.GrowHours,
                cell.Stage, read.At.ToLocalTime(), now.ToLocalTime()));
        else if (cell.Look == ContainerLook.Ripe)
            lines.Add(cell.SeenRipeAt is { } seen ? WindowFormat.SeenRipe(seen.ToLocalTime()) : "ripe now");
        else if (cell.Ripe is { } ripe)
            lines.Add($"ripe ~{WindowFormat.Range(ripe.Earliest.ToLocalTime(), ripe.Latest.ToLocalTime())} {WindowFormat.Mark(ripe.Provenance)}");

        // Beds and pots alike run the clock (ruling 23); a pot whose wilt the game showed says so.
        var shown = cell.WiltShown ? WindowFormat.ShownWilt(cell.WiltSeenAt, now) : null;
        if (shown is not null && cell.Look == ContainerLook.Wilting)
            lines.Add($"{shown} - water now");
        else if (cell.SeenFineAt is { } seenFine)
        {
            // The seen-fine floor (slice 21): the deadline is the look itself, so no deadline line.
            lines.Add(WindowFormat.SeenFineThirstLine(cell.Look));
            lines.Add(WindowFormat.SeenFineClock(seenFine, cell.Bed?.LastTended, now));
            return string.Join("\n", lines);
        }
        else
        {
            if (WindowFormat.ThirstLine(cell.Look, cell.DiesAt?.ToLocalTime(), now.ToLocalTime(), cell.ByClock) is { } thirst)
                lines.Add(thirst);
            if (cell is { ByClock: true, Look: ContainerLook.Wilting, Clock: { } wiltClock })
                lines.Add(WindowFormat.DyingFrom(wiltClock.DyingAt.ToLocalTime(), now.ToLocalTime()));
            if (shown is not null)
                lines.Add(shown);
        }

        if (cell.WaterBy is { } by)
        {
            lines.Add($"{WindowFormat.WaterBy(by.ToLocalTime(), now.ToLocalTime())} ({WindowFormat.Countdown(by, now)})");
            lines.Add(cell.Bed?.LastTended is { } tended
                ? $"watered {WindowFormat.Ago(tended, now)}"
                : "no watering recorded - counting from the sowing");
        }
        else if (cell.OutlivedClock)
            lines.Add(WindowFormat.OutlivedClock);
        else if (cell.NeverWilts)
            lines.Add("never wilts - no deadline");
        else if (cell.Look != ContainerLook.Ripe)
            lines.Add("no watering or sowing recorded - no deadline");

        return string.Join("\n", lines);
    }
}
