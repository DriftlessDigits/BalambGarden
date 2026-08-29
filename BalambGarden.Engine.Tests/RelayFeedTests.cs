using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

/// <summary>Parsing the relay's GET answer: {"receipts":[...envelopes...],"cursor":"N"}.
/// The relay stores payloads verbatim and never re-serializes, so what comes back is
/// byte-identical wire JSON - the same WireOptions read it back.</summary>
public class RelayFeedTests
{
    [Fact]
    public void Parses_envelopes_and_cursor()
    {
        const string json = """
            {"receipts":[
              {"id":"k7f3qx:era2ax:1","entity":"abc123","entityType":"bed","kind":"tend",
               "at":"2026-08-29T06:00:00+00:00","source":"chain","data":{}},
              {"id":"k7f3qx:era2ax:2","entity":"def456","entityType":"pot","kind":"potwater",
               "at":"2026-08-29T06:01:00+00:00","source":"chain-unconfirmed","data":{"species":50}}
            ],"cursor":"77"}
            """;

        var page = RelayFeed.Parse(json);

        Assert.NotNull(page);
        Assert.Equal("77", page!.Cursor);
        Assert.Equal(2, page.Receipts.Count);
        Assert.Equal(SyncKind.Tend, page.Receipts[0].Kind);
        Assert.Equal(SyncEntityType.Bed, page.Receipts[0].EntityType);
        Assert.Equal(SyncSource.ChainUnconfirmed, page.Receipts[1].Source);
        Assert.Equal((ushort)50, page.Receipts[1].Data.Species);
    }

    [Fact]
    public void Empty_page_echoes_the_cursor()
    {
        var page = RelayFeed.Parse("""{"receipts":[],"cursor":"1042"}""");
        Assert.NotNull(page);
        Assert.Empty(page!.Receipts);
        Assert.Equal("1042", page.Cursor);
    }

    /// <summary>Garbage is null, never a half-page: a poller acting on a partial parse could
    /// advance its cursor past receipts it never saw.</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"receipts":[{"id":"x"}]}""")]
    public void Malformed_answers_parse_to_null(string json)
    {
        Assert.Null(RelayFeed.Parse(json));
    }
}
