namespace BalambGarden.Engine.Sensing;

/// <summary>Whether a pot's live map wilt byte is a water claim - one rule for every reader, so
/// they cannot drift: the census (which keeps a pot's last wilt sighting in the ledger), the
/// garden board, the estate tabs and the pot chain's post-water settle. It reads only the map's
/// own bytes, so it lives with the sensing layer and every consumer points down to it.
///
/// <para>Field receipt 2026-08-26 (the apartment Krakka pair): ripe pots read b4=1 while the
/// game itself offers NO watering verb at stage 4 - "wilting - water now" is an impossible
/// instruction there, refuted by the game's own menu. Whether that 1 is a wilt clock frozen at
/// ripening or a ripe marker (the crop analog of the flowers' bloom-2) is still open; the
/// ambient discriminator is a pot that ripens freshly watered (b4=0 says frozen wilt, 1 says
/// ripe marker). The DISPLAY claim is settled either way: live stage outranks the byte,
/// mirroring the bed rule (fully grown cannot wilt - <c>Derivations.ClockWiltSource</c> forces
/// NotApplicable at stage 4).</para>
///
/// <para>Only b4=1 ever claims: flowers broadcast 2 at bloom (08-16 twins) and that was never
/// a water claim either.</para></summary>
public static class PotWiltRule
{
    /// <summary>The ripe stage - the map's b2 low nibble reads 4 when the crop is harvestable.</summary>
    public const byte RipeStage = 4;

    public static bool Claims(byte wilt, byte stage) => wilt == 1 && stage < RipeStage;
}
