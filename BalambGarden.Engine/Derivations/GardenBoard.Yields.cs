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
/// found no sowing on record (sown before the history began, or a pot whose key moved); their
/// items and cross seeds still count (ruled 2026-11-11). <see cref="Gathered"/> is the items those harvests yielded, from the
/// <see cref="GatheredFrom"/> harvests with a yield recorded. Pots carry no <see cref="Cross"/>
/// and no <see cref="Seeds"/> (pots never cross).
///
/// <para>The splits (rethink slice 8, rulings 10 and 12): a crop whose paired harvests came from
/// more than one partner, or from more than one soil, carries <see cref="Splits"/>: one row per
/// partner, per soil, or per partner and soil when both split, each with its own counts, adding up
/// to the crop's. A harvest takes its sowing's partner and soil; a harvest with no sowing on record
/// reads not recorded. Until a crop splits, the partners (beds only) and the soils its plantings
/// recorded ride <see cref="Partners"/> and <see cref="Soils"/> for the hover; each list is empty
/// once the crop splits that way. A split row names its <see cref="Partner"/> (null when the crop
/// does not split by partner) and its <see cref="Soil"/> (null when it does not split by soil).</para></summary>
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
    IReadOnlyList<SeedShare> Seeds)
{
    public Partner? Partner { get; init; }

    public SowSoil? Soil { get; init; }

    public IReadOnlyList<YieldRow> Splits { get; init; } = [];

    public IReadOnlyList<PartnerCount> Partners { get; init; } = [];

    public IReadOnlyList<SoilCount> Soils { get; init; } = [];
}

/// <summary>How many of a crop's plantings were sown beside <see cref="Partner"/> (or with no partner).</summary>
public sealed record PartnerCount(Partner Partner, int Planted);

/// <summary>How many of a crop's plantings were sown in <see cref="Soil"/> (an item id).</summary>
public sealed record SoilCount(uint Soil, int Planted);

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

    /// <summary>The yield book: every sowing and harvest in <paramref name="history"/>, per crop,
    /// beds and pots apart. A harvest pairs with the newest unpaired sowing of the same crop in the
    /// same container before it, never across containers; a newer sowing in a container ends the
    /// older one unharvested. A sowing still open at the end is growing while the
    /// <paramref name="ledger"/> record of its container still holds that crop alive (its newest
    /// contents name it, not dead), else lost. Rows run most planted first. Each bed sowing's
    /// partner comes from its patch at sow through the crossbreed walk (<see cref="PartnerAtSow"/>),
    /// its soil from its row; <see cref="YieldRow"/> says when a crop splits.</summary>
    public static YieldBook Yields(GardenHistory history, IReadOnlyList<ClaimedBed> ledger, DomainTables tables)
    {
        var crops = new Dictionary<(ushort Species, bool IsPot), CropTally>();
        CropTally CropOf(HistoryEntry e) => crops.TryGetValue((e.Species, e.IsPot), out var t)
            ? t
            : crops[(e.Species, e.IsPot)] = new CropTally();

        var open = new Dictionary<Container, (HistoryEntry Sowing, Part Part)>();
        var ordered = history.Entries.OrderBy(e => e.At).ToList();
        foreach (var entry in ordered)
        {
            var crop = CropOf(entry);
            var at = Container.Of(entry);
            if (entry.Kind == HistoryKind.Plant)
            {
                var part = new Part(
                    entry.IsPot ? Partner.NotRecorded : PartnerAtSow(entry, tables),
                    new SowSoil(entry.Soil));
                crop.Count(part, t => t.Planted++);
                open[at] = (entry, part);
                continue;
            }

            if (open.TryGetValue(at, out var sown) && sown.Sowing.Species == entry.Species)
            {
                open.Remove(at);
                crop.Count(sown.Part, t =>
                {
                    t.Harvested++;
                    t.AddHarvest(entry);
                });
            }
            else
            {
                // A harvest with no sowing on record is never "harvested", but what it gave came out
                // of the ground, so it counts (ruled 2026-11-11); its partner and soil are not on record.
                crop.Count(Part.NotRecorded, t =>
                {
                    t.Unmatched++;
                    t.AddHarvest(entry);
                });
            }
        }

        var records = ledger.ToLookup(Container.Of);
        foreach (var (at, sown) in open)
        {
            if (records[at].Any(record => HoldsAlive(record, sown.Sowing.Species)))
                CropOf(sown.Sowing).Count(sown.Part, t => t.Growing++);
        }

        IReadOnlyList<YieldRow> Section(bool pots) => crops
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

    /// <summary>A bed or pot as both the history and the ledger name it (<see cref="HistoryQuery.LastAt"/>'s match).
    /// The ledger may hold more than one record for a container; the book never assumes one.</summary>
    private readonly record struct Container(EstateKey Estate, bool IsPot, int Ordinal, int Slot)
    {
        public static Container Of(HistoryEntry e) => new(e.Estate, e.IsPot, e.PatchOrdinal, e.BedSlot);

        public static Container Of(ClaimedBed b) => new(b.Estate, b.IsPot, b.PatchOrdinal, b.BedSlot);
    }

    /// <summary>A sowing's partner and soil: the finest split the book keeps.</summary>
    private readonly record struct Part(Partner Partner, SowSoil Soil)
    {
        public static Part NotRecorded => new(Partner.NotRecorded, new SowSoil(null));
    }

    /// <summary>One crop's tally, whole and per part (partner and soil), each part remembering when
    /// it was first met.</summary>
    private sealed class CropTally
    {
        private readonly Tally whole = new();
        private readonly Dictionary<Part, Tally> parts = [];

        /// <summary>Counts the same thing on the whole crop and on one part.</summary>
        public void Count(Part part, Action<Tally> count)
        {
            if (!parts.TryGetValue(part, out var tally))
                parts[part] = tally = new Tally { FirstMet = parts.Count };
            count(whole);
            count(tally);
        }

        public YieldRow Row(ushort species, bool isPot)
        {
            // A pot sowing's partner is always not recorded (pots have no patch and never cross),
            // so a pot crop never splits by partner and lists none.
            var byPartner = Compared(p => p.Partner.Kind == PartnerKind.Beside, p => p.Partner);
            var bySoil = Compared(p => p.Soil.Item is not null, p => p.Soil);

            // Recorded rows first, most planted first; "no partner" after the partners; a not-recorded
            // partner or soil last.
            var splits = byPartner || bySoil
                ? parts
                    .GroupBy(p => (Partner: byPartner ? p.Key.Partner : (Partner?)null, Soil: bySoil ? p.Key.Soil : (SowSoil?)null))
                    .Select(g => (g.Key, Tally: Tally.Sum(g.Select(p => p.Value))))
                    .OrderBy(s => (s.Key.Partner?.Kind == PartnerKind.NotRecorded ? 1 : 0) + (s.Key.Soil is { Item: null } ? 1 : 0))
                    .ThenBy(s => s.Key.Partner?.Kind == PartnerKind.NoPartner)
                    .ThenByDescending(s => s.Tally.Planted)
                    .ThenBy(s => s.Tally.FirstMet)
                    .Select(s => s.Tally.Row(species, isPot) with { Partner = s.Key.Partner, Soil = s.Key.Soil })
                    .ToList()
                : [];

            return whole.Row(species, isPot) with
            {
                Splits = splits,
                Partners = byPartner
                    ? []
                    : PlantedBy(p => p.Partner.Kind != PartnerKind.NotRecorded, p => p.Partner)
                        .OrderBy(c => c.Key.Kind == PartnerKind.NoPartner)
                        .Select(c => new PartnerCount(c.Key, c.Planted))
                        .ToList(),
                Soils = bySoil
                    ? []
                    : PlantedBy(p => p.Soil.Item is not null, p => p.Soil)
                        .Select(c => new SoilCount(c.Key.Item!.Value, c.Planted))
                        .ToList(),
            };
        }

        /// <summary>More than one recorded value among the parts holding a harvest paired with its
        /// sowing: a comparison to make, so the crop splits that way.</summary>
        private bool Compared<T>(Func<Part, bool> recorded, Func<Part, T> key)
            => parts.Where(p => p.Value.Harvested > 0 && recorded(p.Key)).Select(p => key(p.Key)).Distinct().Count() > 1;

        /// <summary>Plantings per recorded value, most first, then in the order first met.</summary>
        private IEnumerable<(T Key, int Planted)> PlantedBy<T>(Func<Part, bool> recorded, Func<Part, T> key)
            => parts.Where(p => p.Value.Planted > 0 && recorded(p.Key))
                .GroupBy(p => key(p.Key))
                .Select(g => (g.Key, Planted: g.Sum(p => p.Value.Planted), FirstMet: g.Min(p => p.Value.FirstMet)))
                .OrderByDescending(g => g.Planted)
                .ThenBy(g => g.FirstMet)
                .Select(g => (g.Key, g.Planted));
    }

    private sealed class Tally
    {
        public int Planted, Harvested, Growing, Unmatched, Gathered, GatheredFrom, Crossed, Heard;

        /// <summary>The order its part was first met in the history (the book's tie-break).</summary>
        public int FirstMet;

        private readonly Dictionary<uint, int> seeds = [];

        /// <summary>Adds a harvest's items and its cross seed (heard or not).</summary>
        public void AddHarvest(HistoryEntry harvest)
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

        /// <summary>Several parts' tallies as one; it was first met when the first of them was.</summary>
        public static Tally Sum(IEnumerable<Tally> tallies)
        {
            var sum = new Tally { FirstMet = int.MaxValue };
            foreach (var t in tallies)
            {
                sum.Planted += t.Planted;
                sum.Harvested += t.Harvested;
                sum.Growing += t.Growing;
                sum.Unmatched += t.Unmatched;
                sum.Gathered += t.Gathered;
                sum.GatheredFrom += t.GatheredFrom;
                sum.Crossed += t.Crossed;
                sum.Heard += t.Heard;
                sum.FirstMet = Math.Min(sum.FirstMet, t.FirstMet);
                foreach (var (item, n) in t.seeds)
                    sum.seeds[item] = sum.seeds.GetValueOrDefault(item) + n;
            }

            return sum;
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
