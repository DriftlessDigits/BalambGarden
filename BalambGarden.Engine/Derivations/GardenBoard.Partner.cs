using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>What a bed sowing could cross with, worked out from its patch at sow (rethink slice 8,
/// ruling 12): the crop <see cref="Species"/> beside it, nothing that crosses
/// (<see cref="PartnerKind.NoPartner"/>), or no patch on record (<see cref="PartnerKind.NotRecorded"/>,
/// every sowing before v0.4.1). Derived when the yield book is built, never stored.</summary>
public readonly record struct Partner(PartnerKind Kind, ushort Species = 0)
{
    public static Partner Beside(ushort species) => new(PartnerKind.Beside, species);

    public static Partner NoPartner => new(PartnerKind.NoPartner);

    public static Partner NotRecorded => new(PartnerKind.NotRecorded);
}

public enum PartnerKind { Beside, NoPartner, NotRecorded }

/// <summary>A sowing's soil as a split of the yield book: the soil's item id, or null when the
/// sowing's soil was not heard (every sowing before v0.4.1, a hand sow with no sow question heard).</summary>
public readonly record struct SowSoil(uint? Item);

public static partial class GardenBoard
{
    /// <summary>The crossbreed walk's order from the sown bed, on the patch's grid
    /// (<see cref="RingPosition"/>): right, down, up, left; the first valid partner wins. Community
    /// knowledge (glossary Crossbreed pair), unreceipted by our own captures.</summary>
    private static readonly (int Column, int Row)[] WalkOrder = [(1, 0), (0, 1), (0, -1), (-1, 0)];

    /// <summary>The partner a bed sowing could cross with: the walk over its patch at sow skips a
    /// bed the map read empty, the same crop, a dead plant (withered plants never cross) and a crop
    /// the pairs table has no pair for; a seedling counts (neighbours need only be there at the
    /// sow). No patch on record, or a slot outside the patch, is <see cref="Partner.NotRecorded"/>.</summary>
    private static Partner PartnerAtSow(HistoryEntry sowing, DomainTables tables)
    {
        if (sowing.Patch is not { } patch || sowing.BedSlot is < 0 or >= PatchSlots)
            return Partner.NotRecorded;

        var sown = tables.SeedIdBySpeciesIndex(sowing.Species);
        var (column, row) = RingPosition(sowing.BedSlot);
        foreach (var (right, down) in WalkOrder)
        {
            var slot = Array.IndexOf(Ring, (column + right, row + down));
            if (slot < 0 || patch.FirstOrDefault(b => b.Slot == slot) is not { Occupied: true } bed)
                continue;
            if (bed.Species == sowing.Species || bed.Stage == GardenStage.Dead)
                continue;
            if (sown is { } a && tables.SeedIdBySpeciesIndex(bed.Species) is { } b && tables.CanCross(a, b))
                return Partner.Beside(bed.Species);
        }

        return Partner.NoPartner;
    }
}
