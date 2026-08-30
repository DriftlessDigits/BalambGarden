using System.Security.Cryptography;
using System.Text;

namespace BalambGarden.Engine.Sync;

/// <summary>
/// The passphrase's two children (sync design ruling 16). One household passphrase,
/// typed identically on each install (the out-of-band channel is the actual house);
/// HKDF-SHA256 derives two INDEPENDENT values locally:
///
/// <list type="bullet">
/// <item><see cref="AuthToken"/> - sent as the relay bearer over TLS, stored HASHED
/// server-side. Compromising it never yields the naming key.</item>
/// <item><see cref="NamingKey"/> - the HMAC key entity pseudonyms are minted with
/// (ruling 15). Never leaves the machine, never sent anywhere.</item>
/// </list>
///
/// Determinism is the point: both installs derive byte-identical keys from the same
/// passphrase, so both mint identical entity IDs independently at census time - no
/// mapping table, no exchange (ruling 14). The raw passphrase itself is used and
/// dropped; nothing here stores it.
/// </summary>
public static class HouseholdKeys
{
    // Fixed application salt: not a secret, just domain separation - the same passphrase
    // used with some other tool derives unrelated keys. Versioned so a future derivation
    // change is a NEW key family, never a silent drift of this one.
    //
    // THE CONSTANTS ARE THE RELAY'S (scripts/derive.js in DriftlessDigits/BalambRelay,
    // pinned there and here by the same frozen vectors). The relay side minted the live
    // credential first, so its spelling IS the contract - this file was realigned to it
    // 2026-08-29 after shipping with a different salt/info trio that no vector guarded.
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("BalambGarden.HouseholdSync.v1");

    private static readonly byte[] AuthInfo = Encoding.UTF8.GetBytes("auth-token");
    private static readonly byte[] NamingInfo = Encoding.UTF8.GetBytes("naming-key");

    public const int KeyBytes = 32;

    /// <summary>The relay bearer, lowercase hex. Empty passphrase = sync off (the
    /// on-switch, ruling 16) - callers gate on that BEFORE asking for keys; this method
    /// answers for whatever it is given.</summary>
    public static string AuthToken(string passphrase)
        => Convert.ToHexStringLower(Derive(passphrase, AuthInfo));

    /// <summary>The entity-naming HMAC key, raw bytes for <see cref="EntityName"/>.</summary>
    public static byte[] NamingKey(string passphrase)
        => Derive(passphrase, NamingInfo);

    /// <summary>The relay fingerprint - the admin/user role split, ruled 2026-08-28. This is
    /// exactly what the relay stores and compares (auth.js: SHA-256 over the bearer token
    /// STRING as UTF-8, not over its raw bytes), so the admin sets AUTH_TOKEN_HASH by copying
    /// this value verbatim - the mod displays it, and the phrase itself never leaves the
    /// config. Safe to show and to travel: it is one-way twice from the passphrase.</summary>
    public static string RelayFingerprint(string passphrase)
        => Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(AuthToken(passphrase))));

    private static byte[] Derive(string passphrase, byte[] info)
        => HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: Encoding.UTF8.GetBytes(passphrase),
            outputLength: KeyBytes,
            salt: Salt,
            info: info);
}

/// <summary>
/// Entity pseudonyms (sync design rulings 14/15). A bed or pot travels as
/// HMAC-SHA256(namingKey, canonicalInput) - physical access derives the identity,
/// both installs mint the same ID independently, and the enumerable input space
/// (~1M houses x slots) is useless without the household secret.
///
/// <para>Canonical inputs are pipe-delimited with a leading type word, decimal numbers,
/// HouseId as fixed-width lowercase 16-hex (the raw 8 bytes; it never travels bare,
/// only inside the HMAC): <c>bed|0037015401cb0039|13074|2</c>,
/// <c>pot|0037015401cb0039|208</c>. Beds key by GimmickId values (the game's own
/// identity, constant); pots key by furniture index - the known wobble (ruling 14):
/// rearrangement orphans the old entity and the live map re-anchors.</para>
/// </summary>
public static class EntityName
{
    public static string ForBed(byte[] namingKey, ulong houseId, ushort patchId, int bedIndex)
        => Mint(namingKey, $"bed|{houseId:x16}|{patchId}|{bedIndex}");

    public static string ForPot(byte[] namingKey, ulong houseId, int furnitureIndex)
        => Mint(namingKey, $"pot|{houseId:x16}|{furnitureIndex}");

    /// <summary>The one pot-vs-bed dispatch (QA pass 2026-08-29 - a second mint site had
    /// appeared in the pump, and the two must agree byte-for-byte or a locally-minted id
    /// never matches an incoming one). Null when the canonical's inputs are incomplete
    /// (house id 0; a bed with patch id 0) - the honest "cannot mint yet", never a guess.</summary>
    public static string? For(
        byte[] namingKey, ulong houseId, bool isPot, int potKey, ushort patchId, int bedSlot)
    {
        if (houseId == 0)
            return null;
        if (isPot)
            return ForPot(namingKey, houseId, potKey);
        return patchId == 0 ? null : ForBed(namingKey, houseId, patchId, bedSlot);
    }

    private static string Mint(byte[] namingKey, string canonical)
        => Convert.ToHexStringLower(
            HMACSHA256.HashData(namingKey, Encoding.UTF8.GetBytes(canonical)));
}
