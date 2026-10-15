namespace BalambGarden.Engine.Census;

/// <summary>Persisted by NAME in the trail (JsonStringEnumConverter), so renaming a member is
/// the hazard, not reordering (corrected 2026-10-14). Fertilize was appended 2026-08-23.</summary>
public enum ReceiptVerb { Tend, Harvest, Plant, PotWater, Fertilize }

/// <summary>A completed interaction, parsed from dialogue by the game adapter.
/// Receipts are the ONLY thing that binds and the only thing that claims.</summary>
public sealed record ReceiptEvent(
    EstateKey Estate,
    int PatchOrdinal,
    int BedSlot,
    ReceiptVerb Verb,
    ushort SpeciesIndex,
    byte Stage,
    DateTimeOffset At,
    bool IsPot = false);
