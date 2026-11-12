using BalambGarden.Engine.Census;

namespace BalambGarden.Engine.Sensing;

/// <summary>Where a map read reads: one estate, and which side of its front door. The two sides
/// are different maps (the yard's beds, the house's pots), read by different code paths.</summary>
public sealed record RoomSide(EstateKey Estate, bool Inside);

/// <summary>What armed a re-read: arriving (a zone line or the front door) or a gardening line
/// from the player's own hands at a bed or pot.</summary>
public enum RereadCause { Arrival, GardenLine }

/// <summary>A re-read that is due: what armed it, for which place, and when.</summary>
public sealed record RereadDue(RereadCause Cause, RoomSide Side, DateTimeOffset ArmedAt);

/// <summary>When the census reads the side underfoot AGAIN (rethink slice 25). The game's map can
/// stand a stage behind on arrival and catch up by itself (pot 131, FC Estate, 2026-11-11: stage 3
/// at the 19:24:59 arrival read, stage 4 in the game's own map at 19:27:58, nothing touched; on
/// 2026-11-03 a read 7 s after arrival was current), and a hand disposal prints a line nothing
/// receipts. So: a bounded few reads after an arrival and after a gardening line, then none - never
/// a timer while the player stands still ("events, not polls").
///
/// <para>NEVER IN AN ACT'S LAG. The map shows an act a beat late: a watered pot's wilt flag stays
/// up, a fresh sowing reads empty. A census read in that beat would keep a wilt sighting newer than
/// the watering, or reset the sowing as emptied. So a due read waits while the caller is busy (a
/// verb running, a sowing waiting for its seed) and for <see cref="Quiet"/> after any receipt of
/// the player's own; held reads then run as one.</para>
///
/// <para>A READ BELONGS TO THE PLACE THAT ARMED IT. Every pending read carries its place; asked
/// anywhere else (or nowhere: loading, logout) the schedule drops them all, so a read armed in one
/// room can never run in the next and be booked there. Arriving somewhere replaces the schedule;
/// a line heard somewhere else starts that place's.</para>
///
/// <para>Pure: the caller passes the clock, the place and the busy state.</para></summary>
public sealed class RoomReread
{
    /// <summary>The reads after an arrival, from the arrival read: inside the first minute (ruled
    /// 2026-11-11 on the player's look: "I expect the data will come quickly, within a minute").</summary>
    public static readonly IReadOnlyList<TimeSpan> AfterArrival =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    /// <summary>The reads after a gardening line: the map zeroes or changes a beat after the line.</summary>
    public static readonly IReadOnlyList<TimeSpan> AfterLine = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)];

    /// <summary>No re-read this soon after a receipt of the player's own. Unmeasured: the pot chain
    /// gives a watering's flag 6 s to clear (and sometimes it takes longer), a hand sow's seed shows
    /// within 2.8 s; this sits above both.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(15);

    private RoomSide? side;
    private readonly List<(DateTimeOffset At, RereadCause Cause, DateTimeOffset ArmedAt)> due = [];

    /// <summary>Whether any read is still pending or held.</summary>
    public bool Any => due.Count > 0;

    /// <summary>The arrival read at <paramref name="side"/> has answered: its reads replace any others.</summary>
    public void Arrived(RoomSide side, DateTimeOffset at)
    {
        Clear();
        this.side = side;
        foreach (var after in AfterArrival)
            due.Add((at + after, RereadCause.Arrival, at));
    }

    /// <summary>A gardening line at <paramref name="side"/>: its reads join that place's schedule,
    /// or start it, dropping another place's.</summary>
    public void LineHeard(RoomSide side, DateTimeOffset at)
    {
        if (this.side != side)
        {
            Clear();
            this.side = side;
        }
        foreach (var after in AfterLine)
            due.Add((at + after, RereadCause.GardenLine, at));
    }

    /// <summary>Drops every pending and held read.</summary>
    public void Clear()
    {
        due.Clear();
        side = null;
    }

    /// <summary>Whether to read now, standing at <paramref name="here"/> (null: nowhere). Standing
    /// anywhere but the armed place drops every read. Otherwise a read is due when one has come due,
    /// nothing is busy, and the last own receipt is <see cref="Quiet"/> behind; every read come due
    /// is answered by the one read, which carries the earliest one's cause.</summary>
    public RereadDue? Take(RoomSide? here, DateTimeOffset now, bool busy, DateTimeOffset? lastOwnReceipt)
    {
        if (due.Count == 0)
            return null;
        if (here is null || here != side)
        {
            Clear();
            return null;
        }
        if (busy || (lastOwnReceipt is { } act && now < act + Quiet))
            return null;

        var ready = due.Where(d => d.At <= now).ToList();
        if (ready.Count == 0)
            return null;
        due.RemoveAll(d => d.At <= now);
        var first = ready.MinBy(d => d.At);
        var result = new RereadDue(first.Cause, side!, first.ArmedAt);
        if (due.Count == 0)
            side = null;
        return result;
    }

    /// <summary>What a re-read found changed in a room's pots, one phrase per pot, by key: for the
    /// log, so how late the map runs can be measured. Empty when nothing changed.</summary>
    public static IReadOnlyList<string> Changes(
        IReadOnlyDictionary<int, PotReading> before, IReadOnlyDictionary<int, PotReading> after)
    {
        var lines = new List<string>();
        foreach (var key in before.Keys.Union(after.Keys).Order())
        {
            var where = $"pot (key {key})";
            var had = before.GetValueOrDefault(key);
            var has = after.GetValueOrDefault(key);
            if (has is null)
            {
                if (had is { Occupied: true })
                    lines.Add($"{where}: gone from the map");
                continue;
            }
            if (Content(had?.Occupied ?? false, had?.SpeciesIndex ?? 0, had?.Stage ?? 0, has.Occupied, has.SpeciesIndex, has.Stage) is { } content)
            {
                lines.Add($"{where}: {content}");
                continue;
            }
            if (has.Occupied && had is not null && had.Wilt != has.Wilt)
                lines.Add($"{where}: wilt byte {had.Wilt} -> {has.Wilt}");
        }
        return lines;
    }

    /// <summary>What a re-read found changed in a yard's beds, by map key and bed (one-based).</summary>
    public static IReadOnlyList<string> Changes(
        IReadOnlyDictionary<int, IReadOnlyList<BedReading>> before, IReadOnlyDictionary<int, IReadOnlyList<BedReading>> after)
    {
        var lines = new List<string>();
        foreach (var key in after.Keys.Order())
        {
            var had = before.GetValueOrDefault(key);
            foreach (var bed in after[key])
            {
                var old = had?.FirstOrDefault(b => b.Slot == bed.Slot);
                if (Content(old?.Occupied ?? false, old?.SpeciesIndex ?? 0, old?.Stage ?? 0, bed.Occupied, bed.SpeciesIndex, bed.Stage) is { } content)
                    lines.Add($"map key {key} bed {bed.Slot + 1}: {content}");
            }
        }
        return lines;
    }

    private static string? Content(bool wasOccupied, ushort wasSpecies, byte wasStage, bool occupied, ushort species, byte stage)
    {
        if (wasOccupied && !occupied)
            return "emptied";
        if (!occupied)
            return null;
        if (!wasOccupied || wasSpecies != species)
            return $"now species {species} at stage {stage}";
        return wasStage != stage ? $"stage {wasStage} -> {stage}" : null;
    }
}
