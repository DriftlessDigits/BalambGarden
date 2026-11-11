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
/// <para>The partner split (rethink slice 8, ruling 12): a bed crop whose harvests paired with
/// sowings beside more than one partner carries <see cref="Splits"/>, one row per partner, each
/// naming its <see cref="Partner"/> and carrying its own counts, adding up to the crop's. A harvest
/// takes its sowing's partner; a harvest with no sowing on record reads not recorded. Until the
/// crop splits, the partners its sowings recorded ride <see cref="Partners"/> for the hover; once
/// it splits, that list is empty. Soil splits nothing (ruled 2026-11-11: "Off the table").</para></summary>
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

    public IReadOnlyList<YieldRow> Splits { get; init; } = [];

    public IReadOnlyList<PartnerCount> Partners { get; init; } = [];
}

/// <summary>How many of a crop's sowings went in beside <see cref="Partner"/> (or with no partner).</summary>
public sealed record PartnerCount(Partner Partner, int Planted);

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
    /// partner comes from its patch at sow through the crossbreed walk (<see cref="PartnerAtSow"/>);
    /// <see cref="YieldRow"/> says when a crop splits.</summary>
    public static YieldBook Yields(GardenHistory history, IReadOnlyList<ClaimedBed> ledger, DomainTables tables)
    {
        var crops = new Dictionary<(ushort Species, bool IsPot), CropTally>();
        CropTally CropOf(HistoryEntry e) => crops.TryGetValue((e.Species, e.IsPot), out var t)
            ? t
            : crops[(e.Species, e.IsPot)] = new CropTally();

        var open = new Dictionary<Container, (HistoryEntry Sowing, Partner Partner)>();
        var ordered = history.Entries.OrderBy(e => e.At).ToList();
        foreach (var entry in ordered)
        {
            var crop = CropOf(entry);
            var at = Container.Of(entry);
            if (entry.Kind == HistoryKind.Plant)
            {
                // Pots have no patch and never cross: their sowings' partner is never recorded, so
                // a pot crop never splits and lists no partner.
                var partner = entry.IsPot ? Partner.NotRecorded : PartnerAtSow(entry, tables);
                crop.Count(partner, t => t.Planted++);
                open[at] = (entry, partner);
                continue;
            }

            if (open.TryGetValue(at, out var sown) && sown.Sowing.Species == entry.Species)
            {
                open.Remove(at);
                crop.Count(sown.Partner, t =>
                {
                    t.Harvested++;
                    t.AddHarvest(entry);
                });
            }
            else
            {
                // A harvest with no sowing on record is never "harvested", but what it gave came out
                // of the ground, so it counts (ruled 2026-11-11); its partner is not on record.
                crop.Count(Partner.NotRecorded, t =>
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
                CropOf(sown.Sowing).Count(sown.Partner, t => t.Growing++);
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

    /// <summary>One crop's tally, whole and per partner, each partner remembering when it was first met.</summary>
    private sealed class CropTally
    {
        private readonly Tally whole = new();
        private readonly Dictionary<Partner, Tally> parts = [];

        /// <summary>Counts the same thing on the whole crop and on one partner's part.</summary>
        public void Count(Partner partner, Action<Tally> count)
        {
            if (!parts.TryGetValue(partner, out var tally))
                parts[partner] = tally = new Tally { FirstMet = parts.Count };
            count(whole);
            count(tally);
        }

        public YieldRow Row(ushort species, bool isPot)
        {
            // More than one partner among the harvests paired with their sowings: a comparison to
            // make, so the crop splits. "No partner" and "not recorded" are not partners.
            var splits = parts.Count(p => p.Key.Kind == PartnerKind.Beside && p.Value.Harvested > 0) > 1;

            return whole.Row(species, isPot) with
            {
                Splits = splits
                    ? Ordered(parts).Select(p => p.Value.Row(species, isPot) with { Partner = p.Key }).ToList()
                    : [],
                Partners = splits
                    ? []
                    : Ordered(parts.Where(p => p.Key.Kind != PartnerKind.NotRecorded && p.Value.Planted > 0))
                        .Select(p => new PartnerCount(p.Key, p.Value.Planted))
                        .ToList(),
            };
        }

        /// <summary>The partners first, most planted first, then in the order first met; "no partner"
        /// after them; "not recorded" last.</summary>
        private static IEnumerable<KeyValuePair<Partner, Tally>> Ordered(IEnumerable<KeyValuePair<Partner, Tally>> parts)
            => parts
                .OrderBy(p => p.Key.Kind)
                .ThenByDescending(p => p.Value.Planted)
                .ThenBy(p => p.Value.FirstMet);
    }

    private sealed class Tally
    {
        public int Planted, Harvested, Growing, Unmatched, Gathered, GatheredFrom, Crossed, Heard;

        /// <summary>The order its partner was first met in the history (the book's tie-break).</summary>
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
