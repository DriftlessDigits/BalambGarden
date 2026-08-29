namespace BalambGarden.Engine.Ledger;

public enum Provenance { Anchored, Bracketed, Estimated }

/// <summary>The one number both sync sides and the census agree on: stage 4 is ripe, the
/// payoff signal (its own wire kind). A shared constant so the sender's "reaching 4 emits
/// Ripe" and the receiver's "Ripe means stage 4" cannot drift apart.</summary>
public static class GardenStage
{
    public const byte Ripe = 4;
}

// External is APPENDED (2026-08-29, piece 4): persisted rings hold these as ordinals, so the
// pre-existing values keep their numbers forever - the ChainUnconfirmed precedent.
public enum ObservationSource { MapSighting, TendReceipt, PlantReceipt, HarvestReceipt, StatusTalk, RipeSkip, External }

public sealed record Observation(DateTimeOffset At, ushort SpeciesIndex, byte Stage, ObservationSource Source);
