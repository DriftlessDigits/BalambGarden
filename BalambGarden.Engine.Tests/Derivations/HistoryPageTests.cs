using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>The History tab through the garden board's seam (rethink slice 9, spec ruling 13): the
/// history, a filter and the ledger's estates go in; the tab's lines, its count and its filter
/// lists come out. Nothing here reaches inside the board.</summary>
public class HistoryPageTests
{
    private static readonly DomainTables T = DomainTables.Load();

    // Species indexes from Data/SpeciesIndex.json.
    private const ushort Mirror = 17, Krakka = 49, Onion = 50;

    private static readonly EstateKey Fc = new(340, 11, 56), Mama = new(340, 11, 31), Unnamed = new(641, 3, 50);

    /// <summary>Six hours behind UTC, no daylight saving: the household's November clock.</summary>
    private static readonly TimeZoneInfo Cst = TimeZoneInfo.CreateCustomTimeZone("test-cst", TimeSpan.FromHours(-6), "test-cst", "test-cst");

    private static readonly IReadOnlyList<EstateRecord> Estates =
    [
        new() { Key = Fc, Nickname = "FC Estate" },
        new() { Key = Mama, Nickname = "Mama's Place" },
        new() { Key = Unnamed },
    ];

    private static int seq;

    private static HistoryEntry Row(string at, HistoryKind kind, ushort species = Krakka, EstateKey? estate = null,
        string by = "jw9cdj", bool pot = false)
        => new($"{by}:eraaaa:{++seq}", DateTimeOffset.Parse(at), kind, species, estate ?? Fc, pot, 2, pot ? 0 : 6,
            by, SyncSource.Chain, null);

    private static GardenHistory History(params HistoryEntry[] rows)
    {
        var history = new GardenHistory();
        foreach (var row in rows)
            history.Record(row);
        return history;
    }

    private static HistoryPage Page(GardenHistory history, HistoryFilter? filter = null)
        => GardenBoard.History(history, filter ?? new HistoryFilter(), Estates, T, Cst);

    private static string[] Ids(HistoryPage page)
        => page.Lines.OfType<HistoryRowLine>().Select(l => l.Entry.Id).ToArray();

    [Fact] // an empty record: no lines, nothing to date it from
    public void AnEmptyHistoryHasNoLinesAndNoStart()
    {
        var page = Page(new GardenHistory());
        Assert.Null(page.Since);
        Assert.Equal(0, page.Total);
        Assert.Equal(0, page.Shown);
        Assert.Empty(page.Lines);
        Assert.Empty(page.Estates);
        Assert.Empty(page.Crops);
    }

    [Fact] // newest first, a day heading wherever the reader's own date changes
    public void RowsRunNewestFirstUnderTheReadersDays()
    {
        var oct16 = Row("2026-10-16T21:30:00Z", HistoryKind.Harvest);       // Oct 16, 3:30 pm local
        var nov7Plant = Row("2026-11-08T00:18:30Z", HistoryKind.Plant);     // Nov 7, 6:18 pm local
        var nov7Harvest = Row("2026-11-08T00:18:10Z", HistoryKind.Harvest); // Nov 7, 6:18 pm local
        var nov8 = Row("2026-11-08T06:30:00Z", HistoryKind.Plant);          // Nov 8, 12:30 am local
        var page = Page(History(oct16, nov7Harvest, nov7Plant, nov8));

        Assert.Equal(DateTimeOffset.Parse("2026-10-16T21:30:00Z"), page.Since);
        Assert.Equal(4, page.Total);
        Assert.Equal(4, page.Shown);

        var lines = page.Lines.Select(l => l switch
        {
            HistoryDayLine d => d.Day.ToString("yyyy-MM-dd zzz"),
            HistoryRowLine r => r.Entry.Id,
            _ => "?",
        }).ToArray();
        Assert.Equal(
        [
            "2026-11-08 -06:00", nov8.Id,
            "2026-11-07 -06:00", nov7Plant.Id, nov7Harvest.Id,
            "2026-10-16 -06:00", oct16.Id,
        ], lines);
    }

    [Fact] // each row carries its estate's name; an estate with no name set reads its label
    public void RowsNameTheirEstate()
    {
        var page = Page(History(
            Row("2026-11-07T10:00:00Z", HistoryKind.Plant, estate: Mama),
            Row("2026-11-07T11:00:00Z", HistoryKind.Plant, estate: Unnamed)));

        Assert.Equal(["Ward 4 Plot 51", "Mama's Place"], page.Lines.OfType<HistoryRowLine>().Select(l => l.Estate).ToArray());
    }

    [Fact] // the four filters through the existing history filter; the count is over the whole record
    public void FiltersNarrowTheRowsByEstateCropHandAndKind()
    {
        var a = Row("2026-11-07T10:00:00Z", HistoryKind.Harvest, Mirror, Fc);
        var b = Row("2026-11-07T11:00:00Z", HistoryKind.Harvest, Mirror, Fc, by: Hand.OwnSlug);
        var c = Row("2026-11-07T12:00:00Z", HistoryKind.Plant, Mirror, Fc);
        var d = Row("2026-11-07T13:00:00Z", HistoryKind.Harvest, Krakka, Fc);
        var e = Row("2026-11-07T14:00:00Z", HistoryKind.Harvest, Mirror, Mama);
        var history = History(a, b, c, d, e);

        var page = Page(history, new HistoryFilter(Estate: Fc, Species: Mirror, Own: false, Kind: HistoryKind.Harvest));
        Assert.Equal([a.Id], Ids(page));
        Assert.Equal(1, page.Shown);
        Assert.Equal(5, page.Total);

        Assert.Equal([e.Id, d.Id, b.Id, a.Id], Ids(Page(history, new HistoryFilter(Kind: HistoryKind.Harvest))));
        Assert.Equal([b.Id], Ids(Page(history, new HistoryFilter(Own: true))));
        Assert.Equal([e.Id], Ids(Page(history, new HistoryFilter(Estate: Mama))));
        Assert.Equal([d.Id], Ids(Page(history, new HistoryFilter(Species: Krakka))));
    }

    [Fact] // a filter that hides every row: no lines, the record's size still known
    public void AFilterThatMatchesNothingLeavesNoLines()
    {
        var page = Page(History(Row("2026-11-07T10:00:00Z", HistoryKind.Plant)), new HistoryFilter(Kind: HistoryKind.Harvest));
        Assert.Empty(page.Lines);
        Assert.Equal(0, page.Shown);
        Assert.Equal(1, page.Total);
        Assert.NotNull(page.Since);
    }

    [Fact] // the estate and crop lists offer only what the record holds, by name, whatever the filter
    public void TheFilterListsHoldWhatTheRecordHoldsByName()
    {
        var page = Page(History(
                Row("2026-11-07T10:00:00Z", HistoryKind.Plant, Onion, Mama, pot: true),
                Row("2026-11-07T11:00:00Z", HistoryKind.Plant, Mirror, Fc),
                Row("2026-11-07T12:00:00Z", HistoryKind.Plant, Krakka, Unnamed),
                Row("2026-11-07T13:00:00Z", HistoryKind.Harvest, Mirror, Fc)),
            new HistoryFilter(Estate: Fc));

        Assert.Equal(["FC Estate", "Mama's Place", "Ward 4 Plot 51"], page.Estates.Select(e => e.Name).ToArray());
        Assert.Equal([Fc, Mama, Unnamed], page.Estates.Select(e => e.Key).ToArray());
        Assert.Equal([Krakka, Mirror, Onion], page.Crops.ToArray()); // Krakka Root, Mirror Apple, Thavnairian Onion
    }
}
