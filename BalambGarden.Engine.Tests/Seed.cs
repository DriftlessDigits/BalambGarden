using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Tests;

/// <summary>Test seeding for the planting record (pass-2 #12): no test constructs an
/// observation ring - it says what happened to the bed. Each helper returns the bed so
/// fixtures chain.</summary>
internal static class Seed
{
    /// <summary>A live map read on this install at <paramref name="at"/>.</summary>
    public static ClaimedBed Sight(ClaimedBed bed, DateTimeOffset at, ushort species, byte stage,
        ObservationSource source = ObservationSource.MapSighting)
    {
        bed.Sight(at, species, stage, source);
        return bed;
    }

    /// <summary>A sowing witnessed at <paramref name="at"/>, by own hands unless told otherwise.</summary>
    public static ClaimedBed Plant(ClaimedBed bed, DateTimeOffset at, ushort species, byte stage = 0, Hand? by = null)
    {
        bed.Plant(at, species, stage, by ?? Hand.Own, out _);
        return bed;
    }

    /// <summary>A watering at <paramref name="at"/>, by own hands unless told otherwise.</summary>
    public static ClaimedBed Tend(ClaimedBed bed, DateTimeOffset at, ushort species = 0, Hand? by = null)
    {
        bed.Tend(at, by ?? Hand.Own, species);
        return bed;
    }

    /// <summary>The ripe-skip sighting: a bed read at stage 4 by the chain's own refusal to tend it.</summary>
    public static ClaimedBed Ripe(ClaimedBed bed, DateTimeOffset at, ushort species)
        => Sight(bed, at, species, GardenStage.Ripe, ObservationSource.RipeSkip);

    /// <summary>A household hand with a neutral test slug (never a name).</summary>
    public static readonly Hand Household = new("hhbbaa");
}
