using System.Runtime.CompilerServices;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests.Ledger;

/// <summary>The ledger file's reference bytes. <c>ledger-reference.json</c> beside this file is
/// what the plugin writes for the example below, and git holds it at every commit, so the bytes
/// any earlier build wrote are <c>git show &lt;commit&gt;:</c> that path, never a hand capture.
///
/// A new optional field on the planting record must NOT join the example: the plugin then writes
/// the same bytes, this test stays green, and that green is the proof that an older file loads
/// and saves unchanged (pin the field itself with its own round-trip test). An on-purpose format
/// change (a version bump, a renamed field) fails <see cref="TheExampleSavesAsTheReferenceFile"/>,
/// which writes what it got to <c>ledger-reference.received.json</c> beside it (gitignored); read
/// the difference, copy it over the reference, and the commit carries the format change as a diff.</summary>
public class LedgerReferenceTests
{
    private static readonly EstateKey Yard = new(TerritoryId: 340, Ward: 11, Plot: 32);
    private static readonly EstateKey Room = new(TerritoryId: 340, Ward: 11, Plot: 32, Room: 0);
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-01T18:00:00Z");
    private const ushort Krakka = 0x41, Onion = 0x31;

    private static string Folder([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
    private static string ReferencePath => Path.Combine(Folder(), "ledger-reference.json");
    private static string ReceivedPath => Path.Combine(Folder(), "ledger-reference.received.json");

    /// <summary>Every part of the file at least once: a bed bound to its map key with a
    /// household-sown planting, a stage bracket per stage and an own watering; a pot sown,
    /// watered, seen fine and then seen wilting; a known estate with a nickname; the relay
    /// cursor; one parked foreign receipt.</summary>
    private static LedgerStore Example()
    {
        var store = new LedgerStore { RelayCursor = "4242" };
        store.UpsertEstate(Yard, T0).Nickname = "Home";
        store.UpsertEstate(Yard, T0.AddDays(3));

        var bed = new ClaimedBed { Estate = Yard, MapKey = 110, PatchOrdinal = 0, BedSlot = 3, FirstRecorded = T0, WireEntity = "entity-bed" };
        Seed.Plant(bed, T0, Krakka, by: Seed.Household);
        Seed.Sight(bed, T0.AddHours(1), Krakka, 1);
        Seed.Sight(bed, T0.AddHours(30), Krakka, 2);
        Seed.Tend(bed, T0.AddHours(31), Krakka);
        store.Beds.Add(bed);
        store.Bindings[$"{Yard.TerritoryId}:{Yard.Ward}:{Yard.Plot}:-1#0"] = 110;

        var pot = new ClaimedBed { Estate = Room, MapKey = 4, PatchOrdinal = 4, BedSlot = 0, IsPot = true, FirstRecorded = T0, WireEntity = "entity-pot" };
        Seed.Plant(pot, T0.AddHours(2), Onion);
        Seed.Tend(pot, T0.AddHours(3), Onion);
        Seed.Sight(pot, T0.AddHours(40), Onion, 2);
        pot.SightWilt(T0.AddHours(40), wilting: false, stage: 2);   // seen fine first: a later wilting read leaves it standing
        Seed.Sight(pot, T0.AddHours(50), Onion, 3);
        pot.SightWilt(T0.AddHours(50), wilting: true, stage: 3);
        store.Beds.Add(pot);

        var parked = new SyncReceipt("hhbbaa:eraaaa:7", "entity-elsewhere", SyncEntityType.Bed, SyncKind.Plant,
            T0.AddHours(4), SyncSource.Chain, new SyncData(Species: Krakka, Soil: 16));
        store.ForeignHold.Add(new ParkedForeign(parked, T0.AddHours(5)));
        return store;
    }

    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    [Fact]
    public void TheExampleSavesAsTheReferenceFile()
    {
        var got = Lf(Example().ToJson());
        var want = File.Exists(ReferencePath) ? Lf(File.ReadAllText(ReferencePath)) : null;
        if (got != want)
        {
            File.WriteAllText(ReceivedPath, got);
            Assert.Fail($"The ledger file's bytes changed. What the plugin writes now is in {ReceivedPath}. " +
                "If the change is on purpose (a version bump, a renamed field), read the difference and copy it over " +
                "ledger-reference.json; if a new optional field did this, it joined the example - take it out.");
        }
        File.Delete(ReceivedPath);
    }

    [Fact] // an older file, as the plugin wrote it, loads and saves back to the same bytes
    public void TheReferenceFileLoadsAndSavesUnchanged()
    {
        var reference = Lf(File.ReadAllText(ReferencePath));

        var store = LedgerStore.FromJson(reference);

        Assert.Equal(LedgerStore.CurrentVersion, store.Version);
        Assert.Equal(reference, Lf(store.ToJson()));
    }

    [Fact] // the example holds what it says it holds, so the reference covers every part of the file
    public void TheExampleFillsEveryPartOfTheFile()
    {
        var store = Example();
        var pot = store.Beds.Single(b => b.IsPot);

        Assert.Single(store.Estates);
        Assert.NotEmpty(store.Bindings);
        Assert.Single(store.ForeignHold);
        Assert.Equal(Seed.Household, store.Beds.Single(b => !b.IsPot).PlantedBy);
        Assert.NotNull(pot.WiltSeenAt);
        Assert.NotNull(pot.SeenFineAt);
        Assert.NotNull(pot.LastTended);
    }
}
