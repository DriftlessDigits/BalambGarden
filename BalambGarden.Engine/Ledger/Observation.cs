namespace BalambGarden.Engine.Ledger;

public enum Provenance { Anchored, Bracketed, Estimated }

public enum ObservationSource { MapSighting, TendReceipt, PlantReceipt, HarvestReceipt, StatusTalk, RipeSkip }

public sealed record Observation(DateTimeOffset At, ushort SpeciesIndex, byte Stage, ObservationSource Source);
