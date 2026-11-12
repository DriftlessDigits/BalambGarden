using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

public sealed record SpeciesRipe(ushort SpeciesIndex, EtaWindow Window);

public sealed record PatchRollup(
    EstateKey Estate, int PatchOrdinal, bool IsPots,
    int Claimed, int Ripe, int Due, int Overdue, int Danger, int Unknown,
    EtaWindow? NextRipe, IReadOnlyList<SpeciesRipe> RipeBySpecies, int RipeByClock = 0);

public static class Rollups
{
    /// <summary>The ordinal every pot rollup carries: an estate's pots are ONE group
    /// (UI ruling 2026-08-15), whatever per-pot ordinals the ledger rows hold.</summary>
    public const int PotsOrdinal = -1;

    /// <summary>A planting the plugin saw sown counts by what the card believes of it (spec ruling
    /// 24, <see cref="GardenBoard.Believe"/>): ripe by the clock counts ripe (and in
    /// <see cref="PatchRollup.RipeByClock"/>) and asks for no water; dead by the clock asks for none
    /// either. <paramref name="held"/> as the board's.</summary>
    public static IReadOnlyList<PatchRollup> ForEstate(
        EstateKey estate, IReadOnlyList<ClaimedBed> beds, DomainTables tables,
        IWiltSource wilt, DateTimeOffset now, Func<ClaimedBed, bool>? held = null)
    {
        return beds
            .Where(b => b.Estate == estate)
            .GroupBy(b => (PatchOrdinal: b.IsPot ? PotsOrdinal : b.PatchOrdinal, IsPot: b.IsPot))
            .Select(g =>
            {
                int ripe = 0, due = 0, overdue = 0, danger = 0, unknown = 0, ripeByClock = 0;
                var ripeBySpecies = new Dictionary<ushort, EtaWindow>();
                foreach (var bed in g)
                {
                    var latest = bed.Latest;
                    var belief = GardenBoard.Believe(bed, tables, now, held?.Invoke(bed) ?? false);
                    var isRipe = belief is null ? latest?.Stage == 4 : belief.State == PlantState.Ripe;
                    if (isRipe) ripe++;
                    if (isRipe && belief is { ByClock: true }) ripeByClock++;
                    var noWaterClaim = belief?.State is PlantState.Ripe or PlantState.Dead || belief is { OutlivedClock: true };

                    var crop = latest is null ? null : tables.CropBySpeciesIndex(latest.SpeciesIndex);
                    // A pot's water state is deliberately out of the thirst counts: no pot
                    // has ever been SEEN to wilt, but the evidence base is flower seeds
                    // only - whether that is a pot mechanic or a flower oddity is exactly
                    // what the dry-vs-watered twins labs are running to decide (08-15).
                    // Until they report, NotApplicable asserts nothing either way.
                    var state = bed.IsPot || noWaterClaim ? WaterState.NotApplicable
                        : crop is null ? WaterState.Unknown
                        : wilt.StateFor(bed, crop, now);
                    switch (state)
                    {
                        case WaterState.Due: due++; break;
                        case WaterState.Overdue: overdue++; break;
                        case WaterState.Danger: danger++; break;
                        case WaterState.Unknown: unknown++; break;
                    }

                    // GrowHours rather than the crop row: flowerpot flowers have no crop
                    // row but DO have a clock (24h line-wide) - a "?" for an anchored
                    // flower planting would be the surface refusing a claim it can make.
                    if (!isRipe && latest is not null
                        && tables.GrowHours(latest.SpeciesIndex) is { } growHours
                        && StageModel.RipeWindow(bed, growHours)?.Window is { } window
                        && (!ripeBySpecies.TryGetValue(latest.SpeciesIndex, out var kept)
                            || window.Earliest < kept.Earliest))
                        ripeBySpecies[latest.SpeciesIndex] = window;
                }

                var speciesRipe = ripeBySpecies
                    .Select(kv => new SpeciesRipe(kv.Key, kv.Value))
                    .OrderBy(s => s.Window.Earliest)
                    .ToList();
                return new PatchRollup(estate, g.Key.PatchOrdinal, g.Key.IsPot,
                    g.Count(), ripe, due, overdue, danger, unknown,
                    speciesRipe.Count == 0 ? null : speciesRipe[0].Window, speciesRipe, ripeByClock);
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
