using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Sensing;

/// <summary>Rethink slice 25: the room is read again when it changes. The game's map can be a stage
/// behind on arrival and catch up by itself (pot 131, FC Estate, 2026-11-11: stage 3 at the 19:24:59
/// arrival read, stage 4 in the 19:27:58 Recon dump, nothing touched), and a hand disposal prints a
/// line nothing receipts. So the side underfoot is read again a bounded few times after an arrival
/// and after a gardening line, never inside an act's lag, and only while he still stands where the
/// read was armed. The worked example's times are the ticket's.</summary>
public class RoomRereadTests
{
    private static readonly DateTimeOffset Arrival = DateTimeOffset.Parse("2026-11-11T19:24:59-06:00");

    private static readonly EstateKey FcEstate = new(340, 11, 57);
    private static readonly EstateKey Other = new(340, 11, 32);
    private static readonly RoomSide House = new(FcEstate, Inside: true);
    private static readonly RoomSide Yard = new(FcEstate, Inside: false);

    private static DateTimeOffset At(string local) => DateTimeOffset.Parse($"2026-11-11T{local}-06:00");

    /// <summary>Asks at every second from <paramref name="from"/> to <paramref name="to"/>, standing at
    /// <paramref name="here"/> (by default where the read was armed: the FC Estate's house), and returns
    /// when a read was due - the census tick asks far more often than reads fall due.</summary>
    private static List<DateTimeOffset> Reads(RoomReread reread, DateTimeOffset from, DateTimeOffset to,
        Func<DateTimeOffset, bool>? busy = null, DateTimeOffset? lastOwnReceipt = null,
        Func<DateTimeOffset, RoomSide?>? here = null)
    {
        var reads = new List<DateTimeOffset>();
        for (var t = from; t <= to; t = t.AddSeconds(1))
            if (reread.Take(here is null ? House : here(t), t, busy?.Invoke(t) ?? false, lastOwnReceipt) is not null)
                reads.Add(t);
        return reads;
    }

    [Fact]
    public void AfterAnArrivalTheRoomIsReadAt5_15_30And60SecondsThenNotAgain()
    {
        var reread = new RoomReread();
        reread.Arrived(House, Arrival);

        var reads = Reads(reread, Arrival, Arrival.AddMinutes(10));

        Assert.Equal([At("19:25:04"), At("19:25:14"), At("19:25:29"), At("19:25:59")], reads);
        Assert.False(reread.Any);
    }

    [Fact]
    public void EachReadSaysWhatArmedItWhereAndWhen()
    {
        var reread = new RoomReread();
        reread.Arrived(House, Arrival);

        var due = reread.Take(House, At("19:25:04"), busy: false, lastOwnReceipt: null);

        Assert.Equal(new RereadDue(RereadCause.Arrival, House, Arrival), due);
    }

    [Fact]
    public void ClearDropsEveryReadStillDue()
    {
        var reread = new RoomReread();
        reread.Arrived(House, Arrival);
        reread.LineHeard(House, Arrival.AddSeconds(3));

        reread.Clear();

        Assert.Empty(Reads(reread, Arrival, Arrival.AddMinutes(10)));
        Assert.False(reread.Any);
    }

    [Fact]
    public void AGardenLineArmsReadsAt2And6Seconds()
    {
        var reread = new RoomReread();
        var dispose = At("19:40:00");   // "You dispose of the crops growing in the flowerpot."
        reread.LineHeard(House, dispose);

        var due = reread.Take(House, At("19:40:02"), busy: false, lastOwnReceipt: null);
        var reads = Reads(reread, At("19:40:03"), At("19:45:00"));

        Assert.Equal(new RereadDue(RereadCause.GardenLine, House, dispose), due);
        Assert.Equal([At("19:40:06")], reads);
    }

    [Fact]
    public void ALineRidesBesideTheArrivalReads()
    {
        var reread = new RoomReread();
        reread.Arrived(House, Arrival);
        reread.LineHeard(House, At("19:25:20"));

        var reads = Reads(reread, Arrival, Arrival.AddMinutes(10));

        Assert.Equal([At("19:25:04"), At("19:25:14"), At("19:25:22"), At("19:25:26"), At("19:25:29"), At("19:25:59")], reads);
    }

    [Fact] // the ticket's worked example: pot 392 watered by hand at 19:25:10
    public void NoReadWithin15SecondsOfAnOwnReceiptAndTheHeldReadRunsWhenTheQuietEnds()
    {
        var reread = new RoomReread();
        reread.Arrived(House, Arrival);
        var watered = At("19:25:10");

        var early = Reads(reread, Arrival, watered.AddSeconds(-1));
        var after = Reads(reread, watered, Arrival.AddMinutes(10), lastOwnReceipt: watered);

        Assert.Equal([At("19:25:04")], early);
        Assert.Equal([At("19:25:25"), At("19:25:29"), At("19:25:59")], after);
    }

    [Fact]
    public void AReceiptLineHeldInTheQuietIsOneRead()
    {
        var reread = new RoomReread();
        var line = At("19:30:00");           // a hand watering's line arms +2 and +6
        reread.LineHeard(House, line);

        var reads = Reads(reread, line, line.AddMinutes(1), lastOwnReceipt: line);

        Assert.Equal([At("19:30:15")], reads);
        Assert.False(reread.Any);
    }

    [Fact]
    public void NoReadWhileBusyAndTheHeldReadsRunAsOneAfter()
    {
        var reread = new RoomReread();
        reread.Arrived(House, Arrival);
        var runEnds = At("19:25:45");   // a verb ran from 19:25:00 to 19:25:45

        var reads = Reads(reread, Arrival, Arrival.AddMinutes(10),
            busy: t => t >= At("19:25:00") && t < runEnds);

        Assert.Equal([At("19:25:45"), At("19:25:59")], reads);
    }

    [Fact]
    public void NothingArmedNothingDue()
    {
        var reread = new RoomReread();

        Assert.Null(reread.Take(House, Arrival, busy: false, lastOwnReceipt: null));
        Assert.False(reread.Any);
    }

    // ---- leaving (the player, 2026-11-11: "what if I leave after 45 seconds?"): a read never runs, and never
    // books what it reads, anywhere but the place that armed it.

    [Fact]
    public void LeavingAt45SecondsDropsThe60SecondReadAndNothingRunsInTheNextPlace()
    {
        var reread = new RoomReread();
        reread.Arrived(House, Arrival);
        var left = Arrival.AddSeconds(45);   // the 5, 15 and 30 s reads ran; the 60 s read is pending

        // In the loading screen there is no place underfoot; then the other estate's yard, not yet arrived (the
        // map there has not answered, so the pump has not armed it).
        RoomSide? Where(DateTimeOffset t) => t < left ? House : t < left.AddSeconds(8) ? null : new RoomSide(Other, Inside: false);
        var reads = Reads(reread, Arrival, Arrival.AddMinutes(10), here: Where);

        Assert.Equal([At("19:25:04"), At("19:25:14"), At("19:25:29")], reads);
        Assert.False(reread.Any);
    }

    [Fact]
    public void LeavingWithAReadHeldBehindTheQuietDropsIt()
    {
        var reread = new RoomReread();
        reread.Arrived(House, Arrival);
        var watered = At("19:25:10");        // the 19:25:14 read is held to 19:25:25
        var left = At("19:25:20");           // he teleports out with it still held

        var reads = Reads(reread, watered, Arrival.AddMinutes(10), lastOwnReceipt: watered,
            here: t => t < left ? House : new RoomSide(Other, Inside: false));

        Assert.Empty(reads);
        Assert.False(reread.Any);
    }

    [Fact]
    public void ASecondEstateBeforeTheFirstScheduleEndsReplacesIt()
    {
        var reread = new RoomReread();
        reread.Arrived(House, Arrival);
        var otherYard = new RoomSide(Other, Inside: false);
        var landed = At("19:25:40");         // the first schedule's 60 s read is still pending
        reread.Arrived(otherYard, landed);

        var times = new List<DateTimeOffset>();
        var dues = new List<RereadDue>();
        for (var t = landed; t <= landed.AddMinutes(10); t = t.AddSeconds(1))
            if (reread.Take(otherYard, t, busy: false, lastOwnReceipt: null) is { } due)
            {
                times.Add(t);
                dues.Add(due);
            }

        // The other estate's own four, and the FC Estate's 19:25:59 read never among them.
        Assert.Equal([At("19:25:45"), At("19:25:55"), At("19:26:10"), At("19:26:40")], times);
        Assert.All(dues, d => Assert.Equal(new RereadDue(RereadCause.Arrival, otherYard, landed), d));
    }

    [Fact]
    public void WalkingFromTheHouseIntoTheYardDropsTheHousesReadsUntilTheYardIsArmed()
    {
        var reread = new RoomReread();
        reread.Arrived(House, Arrival);
        reread.LineHeard(House, At("19:25:20"));
        var door = At("19:25:24");           // out through the front door, the 19:25:26 line read pending

        var outside = Reads(reread, door, Arrival.AddMinutes(10), here: _ => Yard);
        reread.Arrived(Yard, door.AddSeconds(2));   // the pump arms the yard once its map answers
        var yard = new List<RereadDue>();
        for (var t = door.AddSeconds(2); t <= Arrival.AddMinutes(10); t = t.AddSeconds(1))
            if (reread.Take(Yard, t, busy: false, lastOwnReceipt: null) is { } due)
                yard.Add(due);

        Assert.Empty(outside);
        Assert.Equal(4, yard.Count);
        Assert.All(yard, d => Assert.Equal(Yard, d.Side));
    }

    [Fact]
    public void ALineHeardSomewhereElseStartsThatPlacesReadsAndDropsTheOldOnes()
    {
        var reread = new RoomReread();
        reread.Arrived(House, Arrival);
        var otherYard = new RoomSide(Other, Inside: false);
        reread.LineHeard(otherYard, At("19:25:20"));

        var atOther = Reads(reread, Arrival, Arrival.AddMinutes(10), here: _ => otherYard);

        Assert.Equal([At("19:25:22"), At("19:25:26")], atOther);
    }
}
