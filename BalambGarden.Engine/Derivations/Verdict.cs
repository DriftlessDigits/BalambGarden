using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>One sentence about the whole garden, plus the window it quoted (null when it
/// quoted none). The window rides alongside rather than baked into the text so the surface
/// can print its provenance marker - a quoted window without its marker is a forecast
/// dressed as a fact.</summary>
public sealed record GardenVerdict(string Text, EtaWindow? Window = null);

/// <summary>
/// The line above the tabs: across every estate the ledger knows, what is the worst thing
/// that wants a human, and where. It reads the ledger only - no live sensing - so it says
/// the same thing standing in the yard as it does standing in Limsa.
///
/// <para>Priority is by consequence, not by count: thirst can kill a plant, ripeness only
/// waits. When nothing needs anyone it says so and names the next window instead of
/// inventing an errand.</para>
/// </summary>
public static class Verdicts
{
    public static GardenVerdict ForGarden(
        IReadOnlyList<EstateRecord> estates,
        IReadOnlyList<ClaimedBed> beds,
        DomainTables tables,
        IWiltSource wilt,
        DateTimeOffset now,
        Func<EtaWindow, string>? formatWindow = null)
    {
        // The Engine has no business knowing where the player lives, so local-time shaping
        // is the caller's; the default keeps the string honest (UTC) for tests and logs.
        formatWindow ??= w => WindowFormat.Range(w.Earliest, w.Latest);

        var rows = estates
            .Select(e => (Estate: e, Rollups: Rollups.ForEstate(e.Key, beds, tables, wilt, now)))
            .Where(r => r.Rollups.Count > 0)
            .ToList();

        if (rows.Count == 0)
            return new GardenVerdict("Nothing claimed yet - tend a bed and it joins the roster.");

        var patches = rows
            .SelectMany(r => r.Rollups.Select(p => (Name: r.Estate.DisplayName, Patch: p)))
            .ToList();

        if (Thirst(patches) is { } thirst)
            return thirst;

        if (Ripe(rows) is { } ripe)
            return ripe;

        var next = patches
            .Select(p => p.Patch.NextRipe)
            .Where(w => w is not null)
            .OrderBy(w => w!.Earliest)
            .FirstOrDefault();

        return next is null
            ? new GardenVerdict("Nothing needs you right now.")
            : new GardenVerdict($"Nothing to do but wait - next window ~{formatWindow(next)}", next);
    }

    /// <summary>Thirst is a patch-level errand: you walk to a patch and water it, so the
    /// line names the patch. Danger outranks plain thirst, and the count of everything
    /// else still thirsty rides along so the worst thing never hides the rest.</summary>
    private static GardenVerdict? Thirst(List<(string Name, PatchRollup Patch)> patches)
    {
        var thirsty = patches
            .Select(p => (p.Name, p.Patch, Count: p.Patch.Due + p.Patch.Overdue + p.Patch.Danger))
            .Where(p => p.Count > 0)
            .OrderByDescending(p => p.Patch.Danger)
            .ThenByDescending(p => p.Count)
            .ToList();

        if (thirsty.Count == 0)
            return null;

        var worst = thirsty[0];
        var text = $"{Where(worst.Name, worst.Patch)}: {Beds(worst.Count)} thirsty";
        if (worst.Patch.Danger > 0)
            text += $" ({worst.Patch.Danger} critical)";

        var elsewhere = thirsty.Skip(1).Sum(p => p.Count);
        if (elsewhere > 0)
            text += $" · {elsewhere} more thirsty elsewhere";

        return new GardenVerdict(text);
    }

    /// <summary>Ripeness is an estate-level errand - you harvest a whole visit's worth at
    /// once - so it counts by estate rather than by patch.</summary>
    private static GardenVerdict? Ripe(
        List<(EstateRecord Estate, IReadOnlyList<PatchRollup> Rollups)> rows)
    {
        var ripe = rows
            .Select(r => (r.Estate.DisplayName, Count: r.Rollups.Sum(p => p.Ripe)))
            .Where(r => r.Count > 0)
            .OrderByDescending(r => r.Count)
            .ToList();

        if (ripe.Count == 0)
            return null;

        var text = $"{Beds(ripe[0].Count)} ripe at {ripe[0].DisplayName}";
        var elsewhere = ripe.Skip(1).Sum(r => r.Count);
        if (elsewhere > 0)
            text += $" · {elsewhere} more ripe elsewhere";

        return new GardenVerdict(text);
    }

    /// <summary>Ordinals are stored raw 0-based; +1 happens only in display strings.</summary>
    private static string Where(string estate, PatchRollup patch)
        => patch.IsPots ? $"Pots at {estate}" : $"Patch {patch.PatchOrdinal + 1} at {estate}";

    private static string Beds(int count) => count == 1 ? "1 bed" : $"{count} beds";
}
