namespace BalambGarden.Engine.Ledger;

public enum Provenance { Anchored, Bracketed, Estimated }

/// <summary>The one number both sync sides and the census agree on: stage 4 is ripe, the
/// payoff signal (its own wire kind). A shared constant so the sender's "reaching 4 emits
/// Ripe" and the receiver's "Ripe means stage 4" cannot drift apart.</summary>
public static class GardenStage
{
    public const byte Ripe = 4;
}

// Persisted BY NAME (the ledger's JsonStringEnumConverter), never by ordinal - so RENAMING a
// member is the hazard, not reordering (corrected 2026-10-14; earlier comments here said
// ordinals). Every member stays because v2 files still carry rings with these words
// (TendReceipt, HarvestReceipt, StatusTalk) and the v2->v3 fold reads them. Since the tenancy
// record (2026-10-14) only MapSighting, RipeSkip, PlantReceipt and External are ever written.
public enum ObservationSource { MapSighting, TendReceipt, PlantReceipt, HarvestReceipt, StatusTalk, RipeSkip, External }

public sealed record Observation(DateTimeOffset At, ushort SpeciesIndex, byte Stage, ObservationSource Source);
