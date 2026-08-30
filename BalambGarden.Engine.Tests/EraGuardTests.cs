using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

public class EraGuardTests
{
    private const string Machine = "m-9f2c";
    private static readonly EraMemory Remembered = new("q7f3nx", 42, Machine);

    [Fact]
    public void Intact_install_keeps_its_era()
    {
        var (verdict, _) = EraGuard.Check(Remembered, "q7f3nx", 42, Machine);
        Assert.Equal(EraVerdict.Keep, verdict);
    }

    [Fact]
    public void A_counter_that_moved_forward_keeps_its_era()
    {
        var (verdict, _) = EraGuard.Check(Remembered, "q7f3nx", 999, Machine);
        Assert.Equal(EraVerdict.Keep, verdict);
    }

    /// <summary>The v2 upgrade is a mint REASON, not a plugin bolt-on (QA pass 2026-08-29:
    /// hoisted from an || in Plugin.cs so every way a counter's life ends lives in this one
    /// tested rule). An otherwise-intact v1 log must still mint - its entries can never mint
    /// wire entities, and one of them would dam the outbox forever.</summary>
    [Fact]
    public void A_pre_house_id_log_mints_even_when_otherwise_intact()
    {
        var (verdict, reason) = EraGuard.Check(Remembered, "q7f3nx", 42, Machine, logVersion: 1);
        Assert.Equal(EraVerdict.MintFresh, verdict);
        Assert.Equal("receipt log v1 -> v2", reason);
    }

    [Fact]
    public void A_fresh_or_parked_log_mints()
    {
        var (verdict, reason) = EraGuard.Check(Remembered, "", 0, Machine);
        Assert.Equal(EraVerdict.MintFresh, verdict);
        Assert.Equal("fresh log", reason);
    }

    [Fact]
    public void No_config_memory_mints()
    {
        var (verdict, reason) = EraGuard.Check(null, "q7f3nx", 42, Machine);
        Assert.Equal(EraVerdict.MintFresh, verdict);
        Assert.Equal("no memory", reason);
    }

    [Fact]
    public void Config_and_log_naming_different_eras_mints()
    {
        var (verdict, reason) = EraGuard.Check(Remembered, "zzzzzz", 42, Machine);
        Assert.Equal(EraVerdict.MintFresh, verdict);
        Assert.Equal("era mismatch", reason);
    }

    [Fact]
    public void A_changed_machine_mints_even_with_a_healthy_counter()
    {
        // The clone case: nothing rewound, the era matches, the counter marched forward.
        // Only the fingerprint says two installs are now spending the same numbers.
        var (verdict, reason) = EraGuard.Check(Remembered, "q7f3nx", 43, "m-other");
        Assert.Equal(EraVerdict.MintFresh, verdict);
        Assert.Equal("machine changed", reason);
    }

    [Fact]
    public void A_rewound_counter_mints()
    {
        var (verdict, reason) = EraGuard.Check(Remembered, "q7f3nx", 7, Machine);
        Assert.Equal(EraVerdict.MintFresh, verdict);
        Assert.Equal("counter rewound", reason);
    }
}

public class SyncIdTests
{
    private const string Alphabet = "abcdefghjkmnpqrstvwxyz23456789";

    [Fact]
    public void New_slugs_are_six_chars_from_the_stated_alphabet()
    {
        for (var i = 0; i < 200; i++)
        {
            var slug = SyncId.NewSlug();
            Assert.Equal(6, slug.Length);
            Assert.All(slug, c => Assert.Contains(c, Alphabet));
        }
    }

    [Fact]
    public void New_slugs_are_distinct_across_many_draws()
    {
        var drawn = new HashSet<string>();
        for (var i = 0; i < 500; i++)
            drawn.Add(SyncId.NewSlug());

        // ~29.5 bits of entropy: 500 draws colliding more than a couple of times would mean
        // the source is not random. Exact-uniqueness would be a flaky assertion, this is not.
        Assert.True(drawn.Count >= 495, $"only {drawn.Count} distinct slugs in 500 draws");
    }

    [Fact]
    public void Format_and_parse_round_trip()
    {
        var id = SyncId.Format("k7f3qw", "q7f3nx", 4821);
        Assert.Equal("k7f3qw:q7f3nx:4821", id);

        Assert.True(SyncId.TryParse(id, out var user, out var era, out var counter));
        Assert.Equal("k7f3qw", user);
        Assert.Equal("q7f3nx", era);
        Assert.Equal(4821, counter);
    }

    [Fact]
    public void Format_round_trips_a_freshly_minted_pair()
    {
        var id = SyncId.Format(SyncId.NewSlug(), SyncId.NewSlug(), 1);
        Assert.True(SyncId.TryParse(id, out _, out _, out var counter));
        Assert.Equal(1, counter);
    }

    [Theory]
    [InlineData("a:b")]          // two parts
    [InlineData("a:b:c:d")]      // four parts
    [InlineData("a::5")]         // empty era
    [InlineData(":b:5")]         // empty user
    [InlineData("a:b:")]         // empty counter
    [InlineData("a:b:0")]        // counters start at 1
    [InlineData("a:b:-3")]       // negative
    [InlineData("a:b:x")]        // non-numeric
    [InlineData("a:b:4 ")]       // trailing whitespace
    [InlineData("")]             // nothing at all
    public void Malformed_ids_are_rejected(string id)
    {
        Assert.False(SyncId.TryParse(id, out var user, out var era, out var counter));
        Assert.Equal("", user);
        Assert.Equal("", era);
        Assert.Equal(0, counter);
    }

    [Fact]
    public void Format_refuses_a_colon_in_a_slug()
    {
        Assert.Throws<ArgumentException>(() => SyncId.Format("a:b", "era", 1));
        Assert.Throws<ArgumentException>(() => SyncId.Format("user", "e:ra", 1));
    }

    [Fact]
    public void Format_refuses_an_empty_slug()
    {
        Assert.Throws<ArgumentException>(() => SyncId.Format("", "era", 1));
        Assert.Throws<ArgumentException>(() => SyncId.Format("user", "", 1));
    }
}
