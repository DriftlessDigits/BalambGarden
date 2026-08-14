namespace BalambGarden.Engine.Sensing;

/// <summary>Bed GimmickId layout [bed idx byte3][patch ordinal byte2][patch-id u16],
/// receipt-verified at three estates 08-12/08-13.</summary>
public readonly record struct BedGimmick(byte BedIndex, byte PatchOrdinal, ushort PatchId);

public static class GimmickId
{
    public static BedGimmick Decode(uint raw) => new(
        BedIndex: (byte)(raw >> 24),
        PatchOrdinal: (byte)(raw >> 16),
        PatchId: (ushort)raw);
}
