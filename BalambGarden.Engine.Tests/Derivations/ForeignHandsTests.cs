using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>The settings window's foreign hands (rethink slice 10, spec ruling 15): every hand tag
/// the ledger or the history holds, other than this install's own, each with how much of the history
/// is its and its newest act there; and the one rule that writes a label into the local map.</summary>
public class ForeignHandsTests
{
    private const ushort Krakka = 49, Onion = 50;

    private static readonly EstateKey Fc = new(340, 11, 56), Unnamed = new(641, 3, 50);

    /// <summary>Six hours behind UTC, no daylight saving: the household's November clock.</summary>
    private static readonly TimeZoneInfo Cst = TimeZoneInfo.CreateCustomTimeZone("test-cst", TimeSpan.FromHours(-6), "test-cst", "test-cst");

    private static readonly IReadOnlyList<EstateRecord> Estates = [new() { Key = Fc, Nickname = "FC Estate" }];

    private static int seq;

    private static HistoryEntry Row(string at, string by, HistoryKind kind = HistoryKind.Plant, ushort species = Krakka,
        EstateKey? estate = null)
        => new($"{by}:eraaaa:{++seq}", DateTimeOffset.Parse(at), kind, species, estate ?? Fc, false, 2, 6,
            by, SyncSource.Chain, null);

    private static GardenHistory History(params HistoryEntry[] rows)
    {
        var history = new GardenHistory();
        foreach (var row in rows)
            history.Record(row);
        return history;
    }

    private static ClaimedBed Bed(int slot, Hand? plantedBy = null, Hand? tendedBy = null)
        => new() { Estate = Fc, MapKey = 1, PatchOrdinal = 0, BedSlot = slot, PlantedBy = plantedBy, LastTendedBy = tendedBy };

    private static IReadOnlyList<SeenHand> Seen(IEnumerable<ClaimedBed> beds, GardenHistory history)
        => GardenBoard.ForeignHands(beds, history, Estates, Cst);

    [Fact] // nothing on record: no hands to name
    public void NoHandsOnRecordListsNone()
        => Assert.Empty(Seen([], new GardenHistory()));

    [Fact] // the own tag reads "you" by rule and is never offered for a label, wherever it appears
    public void TheOwnHandIsNeverListed()
    {
        var history = History(Row("2026-11-07T16:47:00Z", Hand.OwnSlug));
        Assert.Empty(Seen([Bed(0, Hand.Own, Hand.Own)], history));
    }

    [Fact] // a tag in the history: how many rows are its, and its newest one with the estate named and the time local
    public void AHistoryHandCarriesItsCountAndNewestAct()
    {
        var history = History(
            Row("2026-11-07T20:00:00Z", "jw9cdj", HistoryKind.Harvest, Onion),
            Row("2026-11-08T00:18:00Z", "jw9cdj"),
            Row("2026-11-07T23:00:00Z", "jw9cdj"),
            Row("2026-11-08T01:00:00Z", Hand.OwnSlug));

        var hand = Assert.Single(Seen([], history));
        Assert.Equal("jw9cdj", hand.Slug);
        Assert.Equal(3, hand.OnRecord);
        Assert.NotNull(hand.Last);
        Assert.Equal(DateTimeOffset.Parse("2026-11-08T00:18:00Z"), hand.Last!.Entry.At);
        Assert.Equal("FC Estate", hand.Last.EstateName);
        Assert.Equal(TimeSpan.FromHours(-6), hand.Last.LocalAt.Offset);
        Assert.Equal(18, hand.Last.LocalAt.Hour);
    }

    [Fact] // an estate the ledger does not name reads its label, as on the History tab
    public void ANewestActAtAnUnnamedEstateReadsItsLabel()
    {
        var hand = Assert.Single(Seen([], History(Row("2026-11-08T00:18:00Z", "jw9cdj", estate: Unnamed))));
        Assert.Equal(Unnamed.DisplayLabel(), hand.Last!.EstateName);
    }

    [Fact] // a tag only the ledger holds (planted by, or last tended by) is listed, with nothing from the history
    public void ALedgerOnlyHandIsListedWithNoHistory()
    {
        var hands = Seen([Bed(0, plantedBy: new Hand("p1ant0")), Bed(1, tendedBy: new Hand("t3nd00"))], new GardenHistory());
        Assert.Equal(["p1ant0", "t3nd00"], hands.Select(h => h.Slug));
        Assert.All(hands, h => Assert.Equal(0, h.OnRecord));
        Assert.All(hands, h => Assert.Null(h.Last));
    }

    [Fact] // one tag on many beds and many rows is one hand
    public void ATagSeenInBothPlacesIsListedOnce()
    {
        var hand = Assert.Single(Seen(
            [Bed(0, new Hand("jw9cdj"), new Hand("jw9cdj")), Bed(1, new Hand("jw9cdj"))],
            History(Row("2026-11-08T00:18:00Z", "jw9cdj"))));
        Assert.Equal(1, hand.OnRecord);
    }

    [Fact] // newest act first; hands with nothing in the history after them, by tag
    public void HandsReadNewestActFirstThenLedgerOnlyByTag()
    {
        var hands = Seen(
            [Bed(0, new Hand("zz0000")), Bed(1, new Hand("aa0000"))],
            History(Row("2026-11-01T12:00:00Z", "older1"), Row("2026-11-08T00:18:00Z", "newer1")));
        Assert.Equal(["newer1", "older1", "aa0000", "zz0000"], hands.Select(h => h.Slug));
    }

    [Fact] // a label is set as typed, without the spaces round it
    public void ALabelIsSetTrimmed()
    {
        var labels = new Dictionary<string, string>();
        Assert.True(Aliases.Set(labels, "jw9cdj", "  the laptop "));
        Assert.Equal("the laptop", labels["jw9cdj"]);
    }

    [Fact] // an emptied box clears the label: the tag reads "household" again
    public void AnEmptyOrBlankBoxClearsTheLabel()
    {
        var labels = new Dictionary<string, string> { ["jw9cdj"] = "the laptop", ["k7f3qx"] = "upstairs" };
        Assert.True(Aliases.Set(labels, "jw9cdj", ""));
        Assert.True(Aliases.Set(labels, "k7f3qx", "   "));
        Assert.Empty(labels);
    }

    [Fact] // the same words again, or clearing a tag with no label, changes nothing and asks for no save
    public void AnUnchangedLabelReportsNoChange()
    {
        var labels = new Dictionary<string, string> { ["jw9cdj"] = "the laptop" };
        Assert.False(Aliases.Set(labels, "jw9cdj", "the laptop "));
        Assert.False(Aliases.Set(labels, "k7f3qx", ""));
        Assert.Equal(["jw9cdj"], labels.Keys);
    }

    [Fact] // a label for one tag leaves the others alone
    public void ALabelTouchesOnlyItsOwnTag()
    {
        var labels = new Dictionary<string, string> { ["k7f3qx"] = "upstairs" };
        Aliases.Set(labels, "jw9cdj", "the laptop");
        Aliases.Set(labels, "jw9cdj", "");
        Assert.Equal("upstairs", labels["k7f3qx"]);
        Assert.Single(labels);
    }

    [Fact] // the own hand always reads "you": the map never takes a label for it
    public void TheOwnTagTakesNoLabel()
    {
        var labels = new Dictionary<string, string>();
        Assert.False(Aliases.Set(labels, Hand.OwnSlug, "me"));
        Assert.Empty(labels);
    }
}
