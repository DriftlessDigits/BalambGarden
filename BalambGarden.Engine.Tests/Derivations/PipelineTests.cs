using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

public class PipelineTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly EstateKey GardenerHouse = new(340, 11, 32);
    private static readonly EstateKey FcHouse = new(340, 11, 57);
    private static readonly EstateKey DriftHouse = new(641, 3, 10);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T18:00:00Z");

    private static List<ClaimedBed> Patch(EstateKey estate, int ordinal, int mapKey,
        ushort speciesEven, ushort speciesOdd, byte stage)
    {
        var beds = new List<ClaimedBed>();
        for (var slot = 0; slot < 8; slot++)
        {
            var bed = new ClaimedBed
                { Estate = estate, MapKey = mapKey, PatchOrdinal = ordinal, BedSlot = slot };
            bed.Observe(new Observation(Now.AddHours(-2),
                slot % 2 == 0 ? speciesEven : speciesOdd, stage, ObservationSource.TendReceipt));
            beds.Add(bed);
        }
        return beds;
    }

    // The receipt-verified household (08-12): Fig x Mirror -> Kukuru seeds;
    // Krakka x Mirror -> Curiel seeds; Kukuru x Curiel -> Thavnairian Onion.
    private static List<ClaimedBed> Household()
    {
        var beds = new List<ClaimedBed>();
        beds.AddRange(Patch(GardenerHouse, 0, 110, 0x41, 0x11, stage: 1)); // Fig x Mirror
        beds.AddRange(Patch(FcHouse, 0, 1293, 0x31, 0x11, stage: 1));     // Krakka x Mirror
        beds.AddRange(Patch(DriftHouse, 0, 1038, 0x24, 0x2C, stage: 3));    // Kukuru x Curiel
        return beds;
    }

    [Fact] // pairs are genuinely multi-result, so a patch can carry more than one intent
    public void RecognizesAllThreeIntents()
    {
        var intents = PipelineReader.RecognizeIntents(Household(), T);
        Assert.True(intents.Count >= 3, $"expected at least 3 intents, got {intents.Count}");
        Assert.Contains(intents, i => i.Estate == GardenerHouse
            && T.CropBySeedId(i.ResultSeedId)!.Name.Contains("Royal Kukuru"));
        Assert.Contains(intents, i => i.Estate == FcHouse
            && T.CropBySeedId(i.ResultSeedId)!.Name.Contains("Curiel Root"));
        Assert.Contains(intents, i => i.Estate == DriftHouse
            && T.CropBySeedId(i.ResultSeedId)!.Name.Contains("Thavnairian Onion"));
    }

    [Fact] // Stock lines name what each patch is making - one line per patch
    public void StockTipsNameTheProducts()
    {
        var tips = PipelineReader.Tips(Household(), T, Now);
        var stock = tips.Where(t => t.Kind == TipKind.Stock).ToList();
        Assert.Equal(3, stock.Count);
        Assert.Contains(stock, t => t.Text.Contains("Thavnairian Onion"));
    }

    [Fact] // a patch with two possible products names both, joined by "or"
    public void StockTipJoinsMultipleProducts()
    {
        var tips = PipelineReader.Tips(Household(), T, Now);
        var onionLine = tips.Single(t => t.Kind == TipKind.Stock && t.Text.Contains("Thavnairian Onion"));
        Assert.Contains("Royal Kukuru x Curiel Root -> Apricot or Thavnairian Onion", onionLine.Text);
    }

    [Fact] // chained intents (result feeds a parent) surface as pipeline awareness
    public void ChainedIntentsProduceBottleneckLine()
    {
        var tips = PipelineReader.Tips(Household(), T, Now);
        Assert.Contains(tips, t => t.Kind == TipKind.Bottleneck);
    }

    [Fact] // one line per real relationship - multi-result patches must not multiply the prose
    public void BottleneckLinesAreOnePerRelationship()
    {
        var tips = PipelineReader.Tips(Household(), T, Now);
        var bottlenecks = tips.Where(t => t.Kind == TipKind.Bottleneck).ToList();
        Assert.Equal(2, bottlenecks.Count);
        Assert.Contains(bottlenecks, t => t.Text.Contains("Royal Kukuru seeds"));
        Assert.Contains(bottlenecks, t => t.Text.Contains("Curiel Root seeds"));
        // Consumer is named by PLACE, not by product - the stock line above already
        // names what the patch makes, and saying it twice was the confusing sentence.
        Assert.All(bottlenecks, t => Assert.Contains("replants with", t.Text));
    }

    // Three FC patches all running the same cross - the real household shape (08-16).
    private static List<ClaimedBed> HouseholdWithTriplicateFc()
    {
        var beds = new List<ClaimedBed>();
        beds.AddRange(Patch(DriftHouse, 0, 1038, 0x24, 0x2C, stage: 3));    // Kukuru x Curiel
        beds.AddRange(Patch(FcHouse, 0, 1293, 0x31, 0x11, stage: 1));     // Krakka x Mirror
        beds.AddRange(Patch(FcHouse, 1, 1294, 0x31, 0x11, stage: 1));
        beds.AddRange(Patch(FcHouse, 2, 1295, 0x31, 0x11, stage: 1));
        return beds;
    }

    [Fact] // three patches running the same cross are ONE fact - one stock line, not three
    public void SamePairPatchesCollapseIntoOneStockLine()
    {
        var tips = PipelineReader.Tips(HouseholdWithTriplicateFc(), T, Now);
        var fcStock = tips.Where(t => t.Kind == TipKind.Stock && t.Text.Contains("Krakka")).ToList();
        Assert.Single(fcStock);
        Assert.Contains("patches 1-3", fcStock[0].Text);
    }

    [Fact] // three feeder patches of one relationship are ONE bottleneck line
    public void SameRelationshipFeedersCollapseIntoOneLine()
    {
        var tips = PipelineReader.Tips(HouseholdWithTriplicateFc(), T, Now);
        var bottlenecks = tips.Where(t => t.Kind == TipKind.Bottleneck
            && t.Text.Contains("Curiel Root seeds")).ToList();
        Assert.Single(bottlenecks);
        Assert.Contains("patches 1-3", bottlenecks[0].Text);
    }

    [Fact] // tips speak the names the tabs wear, not ward-plot serial numbers
    public void TipsUseTheCallersEstateNames()
    {
        var tips = PipelineReader.Tips(Household(), T, Now,
            k => k == DriftHouse ? "Drift's Place" : k == FcHouse ? "FC Estate" : "Gardener's");
        Assert.Contains(tips, t => t.Text.Contains("Drift's Place"));
        Assert.DoesNotContain(tips, t => t.Text.Contains("Ward"));
    }

    private sealed class FakeInventory(Dictionary<uint, int> counts) : IInventorySource
    {
        public int CountOf(uint itemId) => counts.GetValueOrDefault(itemId);
    }

    private static uint Seed(ushort species) => T.SeedIdBySpeciesIndex(species)!.Value;

    [Fact] // the state join: demand from the layout, supply from bags, shortage = attention
    public void ShortBottleneckCarriesDemandSupplyAndAttention()
    {
        var inv = new FakeInventory(new() { [Seed(0x2C)] = 2, [Seed(0x24)] = 10 });
        var tips = PipelineReader.Tips(Household(), T, Now, inventory: inv);

        var curiel = tips.Single(t => t.Kind == TipKind.Bottleneck && t.Text.Contains("Curiel Root seeds"));
        Assert.Contains("needs 4", curiel.Text);
        Assert.Contains("2 in bags", curiel.Text);
        Assert.True(curiel.Attention);

        var kukuru = tips.Single(t => t.Kind == TipKind.Bottleneck && t.Text.Contains("Royal Kukuru seeds"));
        Assert.Contains("10 in bags", kukuru.Text);
        Assert.Contains("covered", kukuru.Text);
        Assert.False(kukuru.Attention);
    }

    [Fact] // consumer ripe NOW + feeder still growing = the real alarm: feeder lands after
    public void FeederRipeningAfterTheReplantSaysAfter()
    {
        var beds = new List<ClaimedBed>();
        beds.AddRange(Patch(GardenerHouse, 0, 110, 0x41, 0x11, stage: 1)); // Fig x Mirror
        beds.AddRange(Patch(FcHouse, 0, 1293, 0x31, 0x11, stage: 1));     // feeder: growing
        beds.AddRange(Patch(DriftHouse, 0, 1038, 0x24, 0x2C, stage: 4));    // consumer: ripe now
        var inv = new FakeInventory(new() { [Seed(0x2C)] = 0, [Seed(0x24)] = 0 });

        var tips = PipelineReader.Tips(beds, T, Now, inventory: inv);
        var curiel = tips.Single(t => t.Kind == TipKind.Bottleneck && t.Text.Contains("Curiel Root seeds"));
        Assert.Contains("after the replant", curiel.Text);
        Assert.True(curiel.Attention);
    }

    [Fact] // feeder ripe NOW + consumer still growing = short but the seeds land in time
    public void FeederRipeningBeforeTheReplantSaysBefore()
    {
        var beds = new List<ClaimedBed>();
        beds.AddRange(Patch(GardenerHouse, 0, 110, 0x41, 0x11, stage: 1));
        beds.AddRange(Patch(FcHouse, 0, 1293, 0x31, 0x11, stage: 4));     // feeder: ripe now
        beds.AddRange(Patch(DriftHouse, 0, 1038, 0x24, 0x2C, stage: 1));    // consumer: growing
        var inv = new FakeInventory(new() { [Seed(0x2C)] = 0, [Seed(0x24)] = 0 });

        var tips = PipelineReader.Tips(beds, T, Now, inventory: inv);
        var curiel = tips.Single(t => t.Kind == TipKind.Bottleneck && t.Text.Contains("Curiel Root seeds"));
        Assert.Contains("before the replant", curiel.Text);
    }

    [Fact] // no inventory source = no supply claims at all - fail-closed, never a guess
    public void NoInventoryMakesNoSupplyClaims()
    {
        var tips = PipelineReader.Tips(Household(), T, Now);
        foreach (var tip in tips.Where(t => t.Kind == TipKind.Bottleneck))
        {
            Assert.DoesNotContain("in bags", tip.Text);
            Assert.False(tip.Attention);
        }
    }

    [Fact] // one bed off-pattern: anomaly, phrased as a question, never a correction
    public void BrokenAlternationIsAnAnomaly()
    {
        var beds = Patch(GardenerHouse, 0, 110, 0x41, 0x11, 1);
        beds[5].Observe(new Observation(Now.AddHours(-1), 0x31, 1, ObservationSource.TendReceipt));

        var tips = PipelineReader.Tips(beds, T, Now);
        var anomaly = tips.Single(t => t.Kind == TipKind.Anomaly);
        Assert.True(anomaly.Attention);
    }

    [Fact] // a single-species patch is not a cross - no intent, no tips noise
    public void MonocultureProducesNoIntent()
    {
        var beds = Patch(GardenerHouse, 0, 110, 0x31, 0x31, 2);
        Assert.Empty(PipelineReader.RecognizeIntents(beds, T));
    }
}
