namespace BalambGarden.Engine.Census;

public enum ReceiptVerb { Tend, Harvest, Plant, PotWater }

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
