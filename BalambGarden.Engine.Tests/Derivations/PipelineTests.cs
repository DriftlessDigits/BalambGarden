using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

public class PipelineTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly EstateKey ChelseaHouse = new(340, 11, 32);
    private static readonly EstateKey FcHouse = new(340, 11, 57);
    private static readonly EstateKey SamHouse = new(641, 3, 10);
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
        beds.AddRange(Patch(ChelseaHouse, 0, 110, 0x41, 0x11, stage: 1)); // Fig x Mirror
        beds.AddRange(Patch(FcHouse, 0, 1293, 0x31, 0x11, stage: 1));     // Krakka x Mirror
        beds.AddRange(Patch(SamHouse, 0, 1038, 0x24, 0x2C, stage: 3));    // Kukuru x Curiel
        return beds;
    }

    [Fact] // pairs are genuinely multi-result, so a patch can carry more than one intent
    public void RecognizesAllThreeIntents()
    {
        var intents = PipelineReader.RecognizeIntents(Household(), T);
        Assert.True(intents.Count >= 3, $"expected at least 3 intents, got {intents.Count}");
        Assert.Contains(intents, i => i.Estate == ChelseaHouse
            && T.CropBySeedId(i.ResultSeedId)!.Name.Contains("Royal Kukuru"));
        Assert.Contains(intents, i => i.Estate == FcHouse
            && T.CropBySeedId(i.ResultSeedId)!.Name.Contains("Curiel Root"));
        Assert.Contains(intents, i => i.Estate == SamHouse
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

    [Fact] // one bed off-pattern: anomaly, phrased as a question, never a correction
    public void BrokenAlternationIsAnAnomaly()
    {
        var beds = Patch(ChelseaHouse, 0, 110, 0x41, 0x11, 1);
        beds[5].Observe(new Observation(Now.AddHours(-1), 0x31, 1, ObservationSource.TendReceipt));

        var tips = PipelineReader.Tips(beds, T, Now);
        Assert.Contains(tips, t => t.Kind == TipKind.Anomaly);
    }

    [Fact] // a single-species patch is not a cross - no intent, no tips noise
    public void MonocultureProducesNoIntent()
    {
        var beds = Patch(ChelseaHouse, 0, 110, 0x31, 0x31, 2);
        Assert.Empty(PipelineReader.RecognizeIntents(beds, T));
    }
}
