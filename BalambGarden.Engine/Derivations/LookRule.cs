using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;

namespace BalambGarden.Engine.Derivations;

/// <summary>
/// When a map read may overrule the clock (spec ruling 24, the look rule; ruled 2026-11-11: "we
/// obviously don't trust the initial data load. so that shouldn't over rule the clock ... prefer
/// the clock unless the 5 second check disagrees. If that happens, the 15 second check becomes
/// truth and we log it loudly"). An answered arrival read can still lag a stage (rethink slice 25),
/// so the arrival look never overrules a clock state:
/// <list type="bullet">
/// <item>At the arrival (a new estate or the front door), every container on that side whose
/// clock speaks is HELD: the card believes the clock, not the read. A read an act makes before
/// the 5 s look is no better than the arrival look; the hold stands.</item>
/// <item>The 5 s look (the room re-read's first arrival read): a container whose look agrees with
/// the clock is released, its read now the truth; one that disagrees stays held.</item>
/// <item>The 15 s look (the second): every container still held is released, its read the truth,
/// and each that disagreed at 5 s gives a warning line (the clock's state, both looks and their
/// times).</item>
/// <item>Every read after that is the room's truth. Leave before the 15 s look and the hold stays:
/// the clock keeps speaking until the next arrival there settles.</item>
/// </list>
/// A look agrees or disagrees only on what the map publishes: growing, ripe or dead (an empty read empties the record itself, before any look), and for
/// a pot its wilt flag; a bed's living read agrees with a clock's wilting (beds publish no wilt).
/// The hold lives in memory only: nothing new is saved, and a plugin reload forgets it.
/// Pure: the caller passes the ledger's containers on the side (after the census read landed) and
/// the clock. The read compared is each container's newest read taken at that moment.
/// </summary>
public sealed class LookRule(DomainTables tables)
{
    /// <summary>What a read or the clock says, in the words of the warning line.</summary>
    public enum Said { Growing, Ripe, Wilting, Dead }

    private sealed record Hold(RoomSide Side, Said? Clock = null, Said? Five = null, DateTimeOffset? FiveAt = null);

    private readonly Dictionary<(EstateKey Estate, bool IsPot, int Key, int Patch, int Slot), Hold> holds = [];
    private RoomSide? side;
    private int looks;

    private static (EstateKey, bool, int, int, int) Id(ClaimedBed bed)
        => bed.IsPot ? (bed.Estate, true, bed.MapKey, 0, 0) : (bed.Estate, false, 0, bed.PatchOrdinal, bed.BedSlot);

    /// <summary>Whether the card must believe this container's clock over its newest read.</summary>
    public bool Holds(ClaimedBed bed) => holds.ContainsKey(Id(bed));

    /// <summary>The arrival read at <paramref name="side"/> has landed: hold every container there
    /// whose clock speaks, and count the looks from here.</summary>
    public void Arrived(RoomSide side, IEnumerable<ClaimedBed> onSide, DateTimeOffset at)
    {
        this.side = side;
        looks = 0;
        foreach (var key in holds.Where(h => h.Value.Side == side).Select(h => h.Key).ToList())
            holds.Remove(key);
        foreach (var bed in onSide)
            if (GardenBoard.ClockState(bed, tables, at) is not null)
                holds[Id(bed)] = new Hold(side);
    }

    /// <summary>A room re-read that answers the arrival schedule landed at <paramref name="side"/>
    /// at <paramref name="at"/>: the first is the 5 s look, the second the 15 s look; by then
    /// nothing on the side is held, so later ones change nothing. Returns the warning lines, one per container whose 5 s look disagreed.</summary>
    public IReadOnlyList<string> Looked(RoomSide side, IEnumerable<ClaimedBed> onSide, DateTimeOffset at)
    {
        if (this.side != side)
            return [];
        looks++;

        var warnings = new List<string>();
        foreach (var bed in onSide)
        {
            if (!holds.TryGetValue(Id(bed), out var hold) || hold.Side != side)
                continue;
            var look = Read(bed, at);
            if (looks == 1)
            {
                var clock = GardenBoard.ClockState(bed, tables, at) is { } state ? Say(state) : (Said?)null;
                if (clock is null)
                {
                    // The record changed under the hold (emptied, replanted unseen): nothing to hold.
                    holds.Remove(Id(bed));
                    continue;
                }
                if (look is null)
                    continue;
                if (Agrees(look.Value, clock.Value, bed.IsPot))
                    holds.Remove(Id(bed));
                else
                    holds[Id(bed)] = hold with { Clock = clock, Five = look, FiveAt = at };
                continue;
            }

            holds.Remove(Id(bed));
            if (hold.Five is { } five && look is { } fifteen)
                warnings.Add(Warning(bed, hold.Clock!.Value, five, hold.FiveAt!.Value, fifteen, at));
        }
        return warnings;
    }

    /// <summary>The warning line, for the plugin log only.</summary>
    public static string Warning(ClaimedBed bed, Said clock, Said five, DateTimeOffset fiveAt, Said fifteen,
        DateTimeOffset fifteenAt)
    {
        var where = bed.IsPot ? $"pot (key {bed.MapKey})" : $"patch {bed.PatchOrdinal + 1} bed {bed.BedSlot + 1}";
        return $"a look disagreed with the clock at {bed.Estate.DisplayLabel()}, {where}: the clock said {Word(clock)};"
               + $" the 5s look at {fiveAt:u} said {Word(five)}; the 15s look at {fifteenAt:u} said {Word(fifteen)} and stands";
    }

    private static string Word(Said said) => said.ToString().ToLowerInvariant();

    private static Said Say(PlantState state) => state switch
    {
        PlantState.Ripe => Said.Ripe,
        PlantState.Wilting => Said.Wilting,
        PlantState.Dead => Said.Dead,
        _ => Said.Growing,
    };

    /// <summary>What the read taken at <paramref name="at"/> says; null when this read did not
    /// reach the container (its newest read is older).</summary>
    private static Said? Read(ClaimedBed bed, DateTimeOffset at)
    {
        if (bed.Latest is not { } latest || latest.At != at)
            return null;
        return latest.Stage switch
        {
            >= GardenStage.Dead => Said.Dead,
            >= GardenStage.Ripe => Said.Ripe,
            _ => bed.IsPot && bed.WiltSeenAt == at ? Said.Wilting : Said.Growing,
        };
    }

    private static bool Agrees(Said look, Said clock, bool isPot)
        => look == clock || (!isPot && look == Said.Growing && clock == Said.Wilting);
}
