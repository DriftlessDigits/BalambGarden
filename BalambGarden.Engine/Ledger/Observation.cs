namespace BalambGarden.Engine.Ledger;

public enum Provenance { Anchored, Bracketed, Estimated }

// External is APPENDED (2026-08-29, piece 4): persisted rings hold these as ordinals, so the
// pre-existing values keep their numbers forever - the ChainUnconfirmed precedent.
public enum ObservationSource { MapSighting, TendReceipt, PlantReceipt, HarvestReceipt, StatusTalk, RipeSkip, External }

public sealed record Observation(DateTimeOffset At, ushort SpeciesIndex, byte Stage, ObservationSource Source);
