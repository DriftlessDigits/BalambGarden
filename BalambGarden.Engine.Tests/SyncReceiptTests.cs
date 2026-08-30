using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

public class SyncReceiptTests
{
    private static SyncReceipt Sample(SyncKind kind = SyncKind.Tend, SyncData? data = null) => new(
        Id: "k7f3q:412",
        Entity: "9f2c81d4a0b7e6ff",
        EntityType: SyncEntityType.Bed,
        Kind: kind,
        At: new DateTimeOffset(2026, 8, 23, 16, 3, 18, TimeSpan.Zero),
        Source: SyncSource.Chat,
        Data: data ?? SyncData.Empty);

    [Fact]
    public void Envelope_round_trips()
    {
        var receipt = Sample(SyncKind.Plant, new SyncData(Species: 50, Soil: 7031));
        var back = SyncReceipt.FromJson(receipt.ToJson());
        Assert.Equal(receipt, back);
    }

    [Fact]
    public void Wire_names_match_the_ratified_schema()
    {
        var json = Sample().ToJson();
        Assert.Contains("\"id\":\"k7f3q:412\"", json);
        Assert.Contains("\"entityType\":\"bed\"", json);
        Assert.Contains("\"kind\":\"tend\"", json);
        Assert.Contains("\"source\":\"chat\"", json);
    }

    [Fact]
    public void Multiword_kinds_are_single_lowercase_words()
    {
        Assert.Contains("\"kind\":\"potwater\"", Sample(SyncKind.PotWater).ToJson());
        Assert.Contains("\"kind\":\"potwilt\"", Sample(SyncKind.PotWilt).ToJson());
    }

    [Theory]
    [InlineData(SyncSource.Chain, "chain")]
    [InlineData(SyncSource.Chat, "chat")]
    [InlineData(SyncSource.Sighting, "sighting")]
    public void The_ratified_source_words_are_unchanged_by_the_kebab_policy(SyncSource source, string word)
    {
        // The kebab policy arrived with the lifecycle (2026-08-23). Single-word names must come
        // out byte-identical to what the relay was promised, or the amendment renamed facts it
        // had no business renaming.
        var json = (Sample() with { Source = source }).ToJson();
        Assert.Contains($"\"source\":\"{word}\"", json);
    }

    [Fact]
    public void The_unconfirmed_source_lands_hyphenated()
    {
        // Schema amendment 2026-08-23: the word must be "chain-unconfirmed" on the wire, and it
        // has to land before the relay ships.
        var json = (Sample() with { Source = SyncSource.ChainUnconfirmed }).ToJson();
        Assert.Contains("\"source\":\"chain-unconfirmed\"", json);
        Assert.Equal(SyncSource.ChainUnconfirmed, SyncReceipt.FromJson(json)!.Source);
    }

    [Fact]
    public void Empty_data_serializes_with_no_fields()
    {
        Assert.Contains("\"data\":{}", Sample().ToJson());
    }

    [Fact]
    public void Yields_round_trip()
    {
        var receipt = Sample(SyncKind.Harvest,
            new SyncData(Yields: [new SyncYield(4842, 2), new SyncYield(7715, 1)]));
        var back = SyncReceipt.FromJson(receipt.ToJson());
        Assert.Equal(2, back!.Data.Yields!.Count);
        Assert.Equal(4842u, back.Data.Yields[0].Item);
    }

    [Fact]
    public void At_survives_as_utc_instant()
    {
        var back = SyncReceipt.FromJson(Sample().ToJson());
        Assert.Equal(Sample().At, back!.At);
    }
}

public class HouseholdKeysTests
{
    private const string Phrase = "chocobos prefer gysahl greens";

    [Fact]
    public void Same_passphrase_derives_identical_keys_on_both_installs()
    {
        Assert.Equal(HouseholdKeys.AuthToken(Phrase), HouseholdKeys.AuthToken(Phrase));
        Assert.Equal(HouseholdKeys.NamingKey(Phrase), HouseholdKeys.NamingKey(Phrase));
    }

    [Fact]
    public void Auth_and_naming_are_independent_values()
    {
        var auth = HouseholdKeys.AuthToken(Phrase);
        var naming = Convert.ToHexStringLower(HouseholdKeys.NamingKey(Phrase));
        Assert.NotEqual(auth, naming);
    }

    [Fact]
    public void Different_passphrases_share_nothing()
    {
        Assert.NotEqual(HouseholdKeys.AuthToken(Phrase), HouseholdKeys.AuthToken(Phrase + "!"));
    }

    [Fact]
    public void Keys_are_32_bytes()
    {
        Assert.Equal(HouseholdKeys.KeyBytes, HouseholdKeys.NamingKey(Phrase).Length);
        Assert.Equal(HouseholdKeys.KeyBytes * 2, HouseholdKeys.AuthToken(Phrase).Length);
    }

    /// <summary>THE cross-language contract (relay repo: test/derive.test.js). These bytes
    /// were frozen by the relay's Node implementation; both sides must derive them from
    /// "test-vector-passphrase" forever. If this test fails, the derivation changed and every
    /// configured install's identity breaks - fix the code, never the vector.</summary>
    [Fact]
    public void Frozen_vectors_match_the_relay_contract()
    {
        Assert.Equal(
            "a62b718db4317f073fb53c872fda423b37b34689153b6c239a28d555bc769d9d",
            HouseholdKeys.AuthToken("test-vector-passphrase"));
        Assert.Equal(
            "07433adc87dee6f9e77737bd5a5cfbe8ee56f4013ed400a0890bdd8b87232c34",
            Convert.ToHexStringLower(HouseholdKeys.NamingKey("test-vector-passphrase")));
    }

    /// <summary>The fingerprint is what the relay's auth.js stores and compares: SHA-256 of
    /// the bearer token STRING (the lowercase hex, as UTF-8 - not of the token's raw bytes).
    /// The admin copies this out of the mod config and sets it as AUTH_TOKEN_HASH; the
    /// expected value here was computed independently over the frozen auth-token vector.</summary>
    [Fact]
    public void Fingerprint_is_the_relays_stored_hash_of_the_auth_token()
    {
        Assert.Equal(
            "deb74306e1057c16c01a559344fc721108fda38ad565124a15343a9e6cce32d8",
            HouseholdKeys.RelayFingerprint("test-vector-passphrase"));
    }
}

public class EntityNameTests
{
    private static readonly byte[] Key = HouseholdKeys.NamingKey("test household");
    private const ulong House = 0x0037015401CB0039;

    [Fact]
    public void Both_installs_mint_the_same_id_independently()
    {
        var other = HouseholdKeys.NamingKey("test household");
        Assert.Equal(
            EntityName.ForBed(Key, House, 0x3312, 2),
            EntityName.ForBed(other, House, 0x3312, 2));
    }

    [Fact]
    public void Bed_and_pot_namespaces_never_collide()
    {
        Assert.NotEqual(
            EntityName.ForBed(Key, House, 208, 0),
            EntityName.ForPot(Key, House, 208));
    }

    [Fact]
    public void Delimiters_prevent_field_bleed()
    {
        // bed|h|13|24 must not equal bed|h|132|4 - the pipe is the wall.
        Assert.NotEqual(
            EntityName.ForBed(Key, House, 13, 24),
            EntityName.ForBed(Key, House, 132, 4));
    }

    [Fact]
    public void A_different_household_secret_yields_unrelated_ids()
    {
        var stranger = HouseholdKeys.NamingKey("some other house");
        Assert.NotEqual(
            EntityName.ForBed(Key, House, 0x3312, 2),
            EntityName.ForBed(stranger, House, 0x3312, 2));
    }

    [Fact]
    public void Ids_are_64_hex_chars()
    {
        var id = EntityName.ForPot(Key, House, 0);
        Assert.Equal(64, id.Length);
        Assert.True(id.All(c => "0123456789abcdef".Contains(c)));
    }
}
