namespace BalambGarden.Engine.Census;

/// <summary>Fertilize is APPENDED, never inserted: persisted ledgers hold these as ordinals,
/// so the existing four keep their numbers forever.</summary>
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
