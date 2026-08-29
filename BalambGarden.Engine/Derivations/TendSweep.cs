namespace BalambGarden.Engine.Derivations;

/// <summary>Whether a bed belongs in a tend SWEEP (Tend All / Water Patch) - the bed-side twin
/// of the pot Water All rule (08-22: the button must not claim waters it cannot pour; extended
/// to beds 2026-08-28). The game refuses a tend at ripe (the menu offers Harvest, no Tend) and
/// at empty (no menu at all), so a sweep that visits them pays a full dialogue walk to hear a
/// refusal the live map already announced. Skip only what the map CONFIRMED skippable.
///
/// <para>FAIL OPEN on map silence - the opposite pole from the pot rule's fail-closed, and both
/// are honest: a pot without a map key has no identity to claim water against, but a bed's
/// identity is its gimmick, map or no map. Skipping on ignorance would leave a thirsty crop dry
/// to save ten seconds; including it hands the decision to the game's own menu, which is
/// exactly the pre-filter behavior. Single-bed presses never come through here - a deliberate
/// press does what it is told.</para></summary>
public static class TendSweep
{
    public static bool Include(bool hasReading, bool occupied, byte stage)
        => !hasReading || (occupied && stage < PotWiltRule.RipeStage);
}
