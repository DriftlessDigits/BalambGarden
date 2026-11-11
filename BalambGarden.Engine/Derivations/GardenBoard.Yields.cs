using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>The Yields tab's whole content (rethink slice 7, rulings 6-9): per crop, beds and pots
/// apart, all time from <see cref="Since"/>. Null <see cref="Since"/> = no history yet.</summary>
public sealed record YieldBook(DateTimeOffset? Since, IReadOnlyList<YieldRow> Beds, IReadOnlyList<YieldRow> Pots);

/// <summary>One crop in one section. <see cref="Planted"/> = <see cref="Harvested"/> +
/// <see cref="Growing"/> + <see cref="Lost"/>, every term a sowing on record: a harvest counts as
/// harvested only when it pairs with its sowing. <see cref="Unmatched"/> is the harvests that
/// found no sowing on record (sown before the history began, or a pot whose key moved);
/// <see cref="GardenBoard.UnmatchedHarvestsCount"/> says whether their items and cross seeds
/// count. <see cref="Gathered"/> is the items those harvests yielded, from the
/// <see cref="GatheredFrom"/> harvests with a yield recorded. Pots carry no <see cref="Cross"/>
/// and no <see cref="Seeds"/> (pots never cross).</summary>
public sealed record YieldRow(
    ushort Species,
    bool IsPot,
    int Planted,
    int Harvested,
    int Growing,
    int Lost,
    int Unmatched,
    int Gathered,
    int GatheredFrom,
    CrossRate? Cross,
    IReadOnlyList<SeedShare> Seeds);

/// <summary>The harvests that gave a cross seed (<see cref="Crossed"/>) over the harvests whose
/// cross was heard either way (<see cref="Heard"/>; "not heard" never counts as a miss).
/// <see cref="Firm"/> = enough harvests to lean on (<see cref="GardenBoard.FirmRateHarvests"/>);
/// below it the window draws the rate dim.</summary>
public sealed record CrossRate(int Crossed, int Heard)
{
    public int Percent => (int)Math.Round(100.0 * Crossed / Heard, MidpointRounding.AwayFromZero);

    public bool Firm => Heard >= GardenBoard.FirmRateHarvests;
}

/// <summary>One cross seed's share of a crop's crossing harvests: <see cref="Harvests"/> of them,
/// <see cref="Percent"/> of the whole (the shares of one crop always add to 100).</summary>
public sealed record SeedShare(uint SeedItem, int Harvests, int Percent);

public static partial class GardenBoard
{
    /// <summary>A cross rate resting on fewer harvests than this is drawn dim (ruling 8: "75%" from
    /// 3 of 4 must not read as solid). Starts at ten; tunable.</summary>
    public const int FirmRateHarvests = 10;

    /// <summary>Whether a harvest with no sowing on record still counts its items and cross seeds
    /// (it never counts as harvested). True = plan pick A (2026-11-11, the words await the maintainer's look);
    /// false = B, they count nowhere. The one switch between the two.</summary>
    public const bool UnmatchedHarvestsCount = true;

    /// <summary>The yield book: every sowing and harvest in <paramref name="history"/>, per crop,
    /// beds and pots apart. A harvest pairs with the newest unpaired sowing of the same crop in the
    /// same container before it, never across containers; a newer sowing in a container ends the
    /// older one unharvested. A sowing still open at the end is growing while the
    /// <paramref name="ledger"/> record of its container still holds that crop alive (its newest
    /// contents name it, not dead), else lost. Rows run most planted first.</summary>
    public static YieldBook Yields(GardenHistory history, IReadOnlyList<ClaimedBed> ledger, DomainTables tables)
    {
        var tallies = new Dictionary<(ushort Species, bool IsPot), Tally>();
        Tally TallyOf(HistoryEntry e) => tallies.TryGetValue((e.Species, e.IsPot), out var t)
            ? t
            : tallies[(e.Species, e.IsPot)] = new Tally();

        var open = new Dictionary<Container, HistoryEntry>();
        var ordered = history.Entries.OrderBy(e => e.At).ToList();
        foreach (var entry in ordered)
        {
            var tally = TallyOf(entry);
            var at = Container.Of(entry);
            if (entry.Kind == HistoryKind.Plant)
            {
                tally.Planted++;
                open[at] = entry;
                continue;
            }

            var matched = open.TryGetValue(at, out var sowing) && sowing.Species == entry.Species;
            if (matched)
            {
                tally.Harvested++;
                open.Remove(at);
            }
            else
            {
                tally.Unmatched++;
            }

            if (matched || UnmatchedHarvestsCount)
                tally.Count(entry);
        }

        var records = ledger.ToDictionary(Container.Of);
        foreach (var (at, sowing) in open)
        {
            if (records.TryGetValue(at, out var record) && HoldsAlive(record, sowing.Species))
                TallyOf(sowing).Growing++;
        }

        IReadOnlyList<YieldRow> Section(bool pots) => tallies
            .Where(t => t.Key.IsPot == pots)
            .Select(t => t.Value.Row(t.Key.Species, pots))
            .OrderByDescending(r => r.Planted)
            .ThenByDescending(r => r.Harvested)
            .ThenBy(r => tables.SpeciesName(r.Species), StringComparer.CurrentCulture)
            .ToList();

        return new YieldBook(ordered.Count > 0 ? ordered[0].At : null, Section(pots: false), Section(pots: true));
    }

    /// <summary>The record still holds this crop alive: its newest contents (a read, or the sowing
    /// itself, which writes one) name it, and not dead. An emptied record holds nothing.</summary>
    private static bool HoldsAlive(ClaimedBed record, ushort species)
        => record.Latest is { } latest && latest.SpeciesIndex == species && !record.ReadsDead;

    /// <summary>A bed or pot as both the history and the ledger name it (<see cref="HistoryQuery.LastAt"/>'s match).</summary>
    private readonly record struct Container(EstateKey Estate, bool IsPot, int Ordinal, int Slot)
    {
        public static Container Of(HistoryEntry e) => new(e.Estate, e.IsPot, e.PatchOrdinal, e.BedSlot);

        public static Container Of(ClaimedBed b) => new(b.Estate, b.IsPot, b.PatchOrdinal, b.BedSlot);
    }

    private sealed class Tally
    {
        public int Planted, Harvested, Growing, Unmatched, Gathered, GatheredFrom, Crossed, Heard;
        private readonly Dictionary<uint, int> seeds = [];

        /// <summary>A harvest whose items and cross seed count.</summary>
        public void Count(HistoryEntry harvest)
        {
            if (harvest.Yields is { Count: > 0 })
            {
                Gathered += harvest.YieldQty;
                GatheredFrom++;
            }

            if (harvest.SeedQty is not { } qty)
                return;
            Heard++;
            if (qty > 0 && harvest.SeedItem is { } item)
            {
                Crossed++;
                seeds[item] = seeds.GetValueOrDefault(item) + 1;
            }
        }

        public YieldRow Row(ushort species, bool isPot) => new(
            species, isPot, Planted, Harvested, Growing,
            Lost: Planted - Harvested - Growing,
            Unmatched, Gathered, GatheredFrom,
            Cross: isPot || Heard == 0 ? null : new CrossRate(Crossed, Heard),
            Seeds: isPot ? [] : Shares());

        /// <summary>Each seed's share of the crossing harvests, largest first, rounded so the
        /// shares add to 100: each takes its whole percent, then the points left over go one each
        /// to the largest remainders (ties to the seed listed first).</summary>
        private IReadOnlyList<SeedShare> Shares()
        {
            if (Crossed == 0)
                return [];
            var ranked = seeds.OrderByDescending(s => s.Value).ThenBy(s => s.Key).ToList();
            var whole = ranked.Select(s => 100 * s.Value / Crossed).ToArray();
            var left = 100 - whole.Sum();
            foreach (var i in Enumerable.Range(0, ranked.Count)
                         .OrderByDescending(i => 100 * ranked[i].Value % Crossed)
                         .ThenBy(i => i)
                         .Take(left))
                whole[i]++;
            return ranked.Select((s, i) => new SeedShare(s.Key, s.Value, whole[i])).ToList();
        }
    }
}
