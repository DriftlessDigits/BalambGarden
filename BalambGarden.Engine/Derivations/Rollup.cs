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
                    switch (crop is null ? WaterState.Unknown : wilt.StateFor(bed, crop, now))
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

    /// <summary>The one line the plugin ever says unprompted. Null = stay silent.</summary>
    public static string? ArrivalNudge(EstateKey estate, IReadOnlyList<PatchRollup> rollups)
    {
        var thirsty = rollups.Sum(r => r.Due + r.Overdue + r.Danger);
        var ripe = rollups.Sum(r => r.Ripe);
        if (thirsty == 0 && ripe == 0)
            return null;

        var parts = new List<string>();
        if (thirsty > 0) parts.Add($"{thirsty} bed{(thirsty == 1 ? "" : "s")} thirsty here");
        if (ripe > 0) parts.Add($"{ripe} ripe");
        return $"Balamb: {string.Join(", ", parts)}";
    }
}
