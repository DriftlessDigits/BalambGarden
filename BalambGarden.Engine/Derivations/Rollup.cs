using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

public sealed record PatchRollup(
    EstateKey Estate, int PatchOrdinal, bool IsPots,
    int Claimed, int Ripe, int Due, int Overdue, int Danger, int Unknown,
    EtaWindow? NextRipe);

public static class Rollups
{
    public static IReadOnlyList<PatchRollup> ForEstate(
        EstateKey estate, IReadOnlyList<ClaimedBed> beds, DomainTables tables,
        IWiltSource wilt, DateTimeOffset now)
    {
        return beds
            .Where(b => b.Estate == estate)
            .GroupBy(b => (b.PatchOrdinal, b.IsPot))
            .Select(g =>
            {
                int ripe = 0, due = 0, overdue = 0, danger = 0, unknown = 0;
                EtaWindow? nextRipe = null;
                foreach (var bed in g)
                {
                    var latest = bed.Latest;
                    var isRipe = latest?.Stage == 4;
                    if (isRipe) ripe++;

                    var crop = latest is null ? null : tables.CropBySpeciesIndex(latest.SpeciesIndex);
                    // A pot's water state never depends on its crop: flowerpots cannot wilt
                    // (see ClockWiltSource), so an unidentified pot flower is Not Applicable
                    // rather than Unknown. NotApplicable falls through every case below - it
                    // counts toward nothing, so a stale pot can never make the estate look
                    // thirsty. Ripe still counts: pots do ripen, they just never die.
                    var state = bed.IsPot ? WaterState.NotApplicable
                        : crop is null ? WaterState.Unknown
                        : wilt.StateFor(bed, crop, now);
                    switch (state)
                    {
                        case WaterState.Due: due++; break;
                        case WaterState.Overdue: overdue++; break;
                        case WaterState.Danger: danger++; break;
                        case WaterState.Unknown: unknown++; break;
                    }

                    if (!isRipe && crop is not null
                        && StageModel.RipeWindow(bed.Ring, crop.GrowHours) is { } window
                        && (nextRipe is null || window.Earliest < nextRipe.Earliest))
                        nextRipe = window;
                }
                return new PatchRollup(estate, g.Key.PatchOrdinal, g.Key.IsPot,
                    g.Count(), ripe, due, overdue, danger, unknown, nextRipe);
            })
            .OrderBy(r => r.IsPots).ThenBy(r => r.PatchOrdinal)
            .ToList();
    }

    /// <summary>The one line the plugin ever says unprompted. Null = stay silent. The
    /// prefix belongs to the caller: it is the name the plugin announces itself under in
    /// the player's own chat log, which is a setting, not a derivation constant. Blank
    /// prefix = the line speaks with no name at all.</summary>
    public static string? ArrivalNudge(
        EstateKey estate, IReadOnlyList<PatchRollup> rollups, string label = "Balamb")
    {
        var thirsty = rollups.Sum(r => r.Due + r.Overdue + r.Danger);
        var ripe = rollups.Sum(r => r.Ripe);
        if (thirsty == 0 && ripe == 0)
            return null;

        var parts = new List<string>();
        if (thirsty > 0) parts.Add($"{thirsty} bed{(thirsty == 1 ? "" : "s")} thirsty here");
        if (ripe > 0) parts.Add($"{ripe} ripe");
        var line = string.Join(", ", parts);
        return label.Length > 0 ? $"{label}: {line}" : line;
    }
}
