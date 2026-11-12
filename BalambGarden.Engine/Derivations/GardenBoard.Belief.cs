using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;

namespace BalambGarden.Engine.Derivations;

/// <summary>The one state a planting is in (spec ruling 24): exactly one at a time.</summary>
public enum PlantState { Growing, Ripe, Wilting, Dead }

/// <summary>The three moments of a planting the plugin saw sown (spec ruling 24), from its crop's
/// hours: <see cref="RipeAt"/> = the sowing + its growing hours; <see cref="WiltAt"/> = where the
/// water clock starts (the last watering recorded, else the sowing, raised for a pot by its
/// seen-fine floor) + its wilt hours; <see cref="DeathAt"/> = that start + its wither hours (the
/// wilt time + 24 h for every crop in the table). <see cref="Start"/> is that clock start.</summary>
public sealed record PlantClock(Crop Crop, DateTimeOffset RipeAt, DateTimeOffset Start)
{
    /// <summary>Ripe shows this long after the ripe time: the game updates plants on a cycle of
    /// about an hour (ruled 2026-11-11: "for ripe, go with an hour after").</summary>
    public static readonly TimeSpan RipeShows = TimeSpan.FromHours(1);

    public DateTimeOffset WiltAt => Start.AddHours(Crop.WiltHours);
    public DateTimeOffset DeathAt => Start.AddHours(Crop.WitherHours);

    /// <summary>Halfway from wilting to death: the wilting state's red "dying" half (ruling 22).</summary>
    public DateTimeOffset DyingAt => Start.AddHours(Crop.WiltHours + (Crop.WitherHours - Crop.WiltHours) / 2.0);

    /// <summary>Three quarters of the wilt hours: the growing state's amber "water soon" (ruling 22).</summary>
    public DateTimeOffset WaterSoonAt => Start.AddHours(Crop.WiltHours * ClockWiltSource.DueFraction);

    /// <summary>The state at <paramref name="at"/>. <paramref name="ripeShowsAt"/> is when ripe
    /// shows (the ripe time + an hour, unless a look moved it). Dead only when the death time came
    /// before the plant ripened: a wilting plant keeps growing, and a death inside the ripe hour
    /// came after the ripening, so it reads wilting until ripe shows (scope call 2).</summary>
    public PlantState At(DateTimeOffset at, DateTimeOffset ripeShowsAt)
    {
        var ripened = ripeShowsAt - RipeShows;
        if (DeathAt < ripened && at >= DeathAt)
            return PlantState.Dead;
        if (at >= ripeShowsAt)
            return PlantState.Ripe;
        return at >= WiltAt ? PlantState.Wilting : PlantState.Growing;
    }
}

/// <summary>What the card believes about one planting, and on whose word. <see cref="ByClock"/>:
/// the clock alone holds this state, the game never showed it (a bed's wilting always, since
/// beds publish no wilt). <see cref="Since"/>: when the clock's ripe, wilting or dead began.
/// <see cref="OutlivedClock"/>: a bed seen alive after its clock's death time - a watering went
/// unrecorded, so the clock makes no water claim until one is recorded.</summary>
public sealed record Belief(PlantState State, bool ByClock, PlantClock Clock, DateTimeOffset? Since = null,
    bool OutlivedClock = false);

public static partial class GardenBoard
{
    /// <summary>The planting's clock: only for a sowing the plugin saw (an anchored ripe time)
    /// of a crop in the table. Null for a planting it did not see, a flowerpot flower, or an empty
    /// record. <paramref name="withFloor"/> false leaves a pot's seen-fine floor out (a held read
    /// wrote it).</summary>
    public static PlantClock? ClockOf(ClaimedBed bed, DomainTables tables, bool withFloor = true)
    {
        if (bed.PlantedAt is not { } sown || bed.Latest is not { } latest)
            return null;
        var species = bed.PlantedSpecies != 0 ? bed.PlantedSpecies : latest.SpeciesIndex;
        if (tables.CropBySpeciesIndex(species) is not { } crop)
            return null;
        var start = bed.LastTended is { } tended && tended > sown ? tended : sown;
        if (withFloor && bed.IsPot && bed.SeenFineAt is { } fine && fine.AddHours(-crop.WiltHours) > start)
            start = fine.AddHours(-crop.WiltHours);
        return new PlantClock(crop, sown.AddHours(crop.GrowHours), start);
    }

    /// <summary>The clock's state alone at <paramref name="at"/>, no read considered: what a look
    /// is measured against. Null where no clock speaks.</summary>
    public static PlantState? ClockState(ClaimedBed bed, DomainTables tables, DateTimeOffset at)
        => ClockOf(bed, tables, withFloor: false) is { } clock ? clock.At(at, clock.RipeAt + PlantClock.RipeShows) : null;

    /// <summary>
    /// What the card believes about a planting where the player is not standing over it, and on
    /// whose word (spec ruling 24). The one place the board decides a container's state: the clocks
    /// and the reads the plugin already holds. Null where no clock speaks (a planting the plugin did
    /// not see, a flower, an empty record): the board reads the record as it always has.
    ///
    /// <para>A held read (<paramref name="held"/>: the arrival look, not yet settled by the room
    /// re-read's 5 s and 15 s looks) never overrules the clock: the clock alone speaks. Otherwise the
    /// newest read is the truth as of its time: seen ripe and seen dead stand; a plant read growing
    /// moves on by the clock's later moments, and a moment the read disproved is moved: ripe shows an
    /// hour after a growing read taken past it (ruled at the plan call, X), and a bed read alive past
    /// its death time makes no water claim until a watering is recorded.</para>
    /// </summary>
    public static Belief? Believe(ClaimedBed bed, DomainTables tables, DateTimeOffset now, bool held = false)
    {
        if (ClockOf(bed, tables, withFloor: !held) is not { } clock || bed.Latest is not { } read)
            return null;

        var ripeShows = clock.RipeAt + PlantClock.RipeShows;
        var outlived = false;
        if (!held)
        {
            if (read.Stage >= GardenStage.Dead)
                return new Belief(PlantState.Dead, ByClock: false, clock);
            if (read.Stage >= GardenStage.Ripe)
                return new Belief(PlantState.Ripe, ByClock: false, clock);
            if (read.At >= ripeShows)
                ripeShows = read.At + PlantClock.RipeShows;
            outlived = read.At >= clock.DeathAt && clock.DeathAt < ripeShows - PlantClock.RipeShows;
        }

        if (outlived)
            return now >= ripeShows
                ? new Belief(PlantState.Ripe, ByClock: true, clock, ripeShows)
                : new Belief(PlantState.Growing, ByClock: false, clock, OutlivedClock: true);

        var state = clock.At(now, ripeShows);
        return state switch
        {
            PlantState.Ripe => new Belief(state, ByClock: true, clock, ripeShows),
            PlantState.Dead => new Belief(state, ByClock: true, clock, clock.DeathAt),
            PlantState.Wilting => new Belief(state, ByClock: true, clock, clock.WiltAt),
            _ => new Belief(state, ByClock: false, clock),
        };
    }
}
