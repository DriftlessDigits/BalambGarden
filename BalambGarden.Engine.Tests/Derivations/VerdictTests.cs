using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

public class VerdictTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly EstateKey Chelsea = new(340, 11, 32);
    private static readonly EstateKey Fc = new(641, 3, 51);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T18:00:00Z");

    private static EstateRecord Estate(EstateKey key, string nickname) => new()
    {
        Key = key,
        Nickname = nickname,
        FirstSeen = Now.AddDays(-30),
        LastVisited = Now.AddHours(-1),
    };

    private static ClaimedBed Bed(
        EstateKey estate, int patch, int slot, byte stage, double tendedHoursAgo,
        bool isPot = false)
    {
        var bed = new ClaimedBed
        {
            Estate = estate, MapKey = 110 + patch, PatchOrdinal = patch, BedSlot = slot,
            IsPot = isPot,
            LastTended = Now.AddHours(-tendedHoursAgo),
        };
        // Krakka Root (0x31): the 24h wilt tier the other derivation tests use.
        bed.Observe(new Observation(Now.AddHours(-tendedHoursAgo), 0x31, stage,
            ObservationSource.TendReceipt));
        return bed;
    }

    [Fact] // nothing in the ledger at all: an invitation, not a fake status
    public void EmptyGardenSaysSo()
    {
        var verdict = Verdicts.ForGarden([], [], T, new ClockWiltSource(), Now);
        Assert.Contains("Nothing claimed yet", verdict.Text);
        Assert.Null(verdict.Window);
    }

    [Fact] // the brief's own example line
    public void NamesThePatchAndTheEstateThatIsThirsty()
    {
        List<ClaimedBed> beds =
        [
            Bed(Chelsea, 1, 0, 2, 20), Bed(Chelsea, 1, 1, 2, 20), Bed(Chelsea, 1, 2, 2, 20),
            Bed(Chelsea, 0, 0, 2, 1),
        ];

        var verdict = Verdicts.ForGarden(
            [Estate(Chelsea, "Papa's Place")], beds, T, new ClockWiltSource(), Now);

        Assert.Equal("Patch 2 at Papa's Place: 3 beds thirsty", verdict.Text);
    }

    [Fact] // thirst can kill; ripeness only waits - so thirst leads even when outnumbered
    public void ThirstOutranksRipeness()
    {
        List<ClaimedBed> beds =
        [
            Bed(Chelsea, 0, 0, 2, 20),
            Bed(Fc, 0, 0, 4, 1), Bed(Fc, 0, 1, 4, 1), Bed(Fc, 0, 2, 4, 1),
        ];

        var verdict = Verdicts.ForGarden(
            [Estate(Chelsea, "Papa's Place"), Estate(Fc, "FC")], beds, T, new ClockWiltSource(), Now);

        Assert.StartsWith("Patch 1 at Papa's Place: 1 bed thirsty", verdict.Text);
    }

    [Fact] // a bed in the danger band beats a bigger merely-due pile, and says why
    public void DangerOutranksCount()
    {
        List<ClaimedBed> beds =
        [
            Bed(Chelsea, 0, 0, 2, 500),
            Bed(Fc, 0, 0, 2, 20), Bed(Fc, 0, 1, 2, 20), Bed(Fc, 0, 2, 2, 20),
        ];

        var verdict = Verdicts.ForGarden(
            [Estate(Chelsea, "Papa's Place"), Estate(Fc, "FC")], beds, T, new ClockWiltSource(), Now);

        Assert.StartsWith("Patch 1 at Papa's Place: 1 bed thirsty (1 critical)", verdict.Text);
        Assert.Contains("3 more thirsty elsewhere", verdict.Text);
    }

    [Fact] // ripe counts by estate: harvesting is one visit's worth of work
    public void RipeCountsByEstate()
    {
        var beds = new List<ClaimedBed>();
        for (var slot = 0; slot < 8; slot++)
            beds.Add(Bed(Fc, 0, slot, 4, 1));
        beds.Add(Bed(Chelsea, 0, 0, 4, 1));

        var verdict = Verdicts.ForGarden(
            [Estate(Chelsea, "Papa's Place"), Estate(Fc, "FC")], beds, T, new ClockWiltSource(), Now);

        Assert.StartsWith("8 beds ripe at FC", verdict.Text);
        Assert.Contains("1 more ripe elsewhere", verdict.Text);
    }

    [Fact] // nothing needs anyone: name the wait, and hand back the window it quoted
    public void QuietGardenQuotesTheNextWindow()
    {
        var verdict = Verdicts.ForGarden(
            [Estate(Chelsea, "Papa's Place")], [Bed(Chelsea, 0, 0, 2, 1)], T,
            new ClockWiltSource(), Now, _ => "Fri 09:00");

        Assert.Equal("Nothing to do but wait - next window ~Fri 09:00", verdict.Text);
        Assert.NotNull(verdict.Window);
    }

    [Fact] // pots never wilt, so a stale pot can never make the garden look thirsty
    public void PotsNeverReadAsThirsty()
    {
        var verdict = Verdicts.ForGarden(
            [Estate(Chelsea, "Papa's Place")], [Bed(Chelsea, 0, 0, 2, 500, isPot: true)], T,
            new ClockWiltSource(), Now);

        Assert.DoesNotContain("thirsty", verdict.Text);
    }
}
