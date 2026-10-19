using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

public class ChatLineTests
{
    private const int SystemMessage = 57;
    private const int ErrorMessage = 60;
    private const int LootNotice = 62;

    // ---- The obtain lane (capture 2026-08-26: harvest closes on LootNotice/62, not 57) ----

    [Theory]
    [InlineData("You obtain 9 krakka roots.")]
    [InlineData("You obtain a bouquet of red sunflowers.")]
    [InlineData("You obtain 3 kukuru beans.")]
    public void An_obtain_line_on_the_loot_channel_from_us_is_a_harvest_candidate(string text)
        => Assert.Equal(ChatVerdict.HarvestObtain, ChatLine.ClassifyObtain(LootNotice, true, text));

    [Fact]
    public void The_obtain_words_on_the_success_channel_are_nothing()
        // 2026-08-26's deafness lesson cuts both ways: the 57 catalog never hears obtains, and
        // the obtain lane never hears 57 - one line, one lane, decided by structure.
        => Assert.Equal(ChatVerdict.None, ChatLine.ClassifyObtain(SystemMessage, true, "You obtain 9 krakka roots."));

    [Fact]
    public void Somebody_elses_obtain_is_not_our_harvest()
        => Assert.Equal(ChatVerdict.None, ChatLine.ClassifyObtain(LootNotice, false, "You obtain 9 krakka roots."));

    [Theory]
    [InlineData("You spent 303 gil.")]
    [InlineData("Drift says: You obtain 9 krakka roots.")]
    [InlineData("")]
    public void Non_obtain_words_on_the_loot_channel_are_nothing(string text)
        => Assert.Equal(ChatVerdict.None, ChatLine.ClassifyObtain(LootNotice, true, text));

    [Fact]
    public void A_harvest_obtain_is_a_harvest_receipt()
        => Assert.Equal(SyncKind.Harvest, ChatLine.KindOf(ChatVerdict.HarvestObtain));

    [Fact]
    public void The_obtain_line_names_no_container()
    {
        // "You obtain 9 krakka roots." says nothing about bed-vs-pot - unlike every 57 line,
        // where the game's own sentence names the container. The desk must know the difference.
        Assert.False(ChatLine.NamesContainer(ChatVerdict.HarvestObtain));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChatLine.IsPotVerdict(ChatVerdict.HarvestObtain));
    }

    [Theory]
    [InlineData(ChatVerdict.FertilizeBed)]
    [InlineData(ChatVerdict.FertilizePot)]
    [InlineData(ChatVerdict.PlantPot)]
    [InlineData(ChatVerdict.TendBed)]
    [InlineData(ChatVerdict.WaterPot)]
    [InlineData(ChatVerdict.PlantBed)]
    public void Every_success_channel_verdict_names_its_container(ChatVerdict v)
        => Assert.True(ChatLine.NamesContainer(v));

    [Fact]
    public void The_bed_success_line_classifies()
        => Assert.Equal(
            ChatVerdict.FertilizeBed,
            ChatLine.Classify(SystemMessage, true, "You fertilize the crop."));

    [Fact]
    public void The_pot_success_line_classifies()
        => Assert.Equal(
            ChatVerdict.FertilizePot,
            ChatLine.Classify(SystemMessage, true, "Pomace added to flowerpot."));

    [Theory]
    [InlineData("Seeds planted in flowerpot.", ChatVerdict.PlantPot)]
    [InlineData("This crop is doing well.", ChatVerdict.TendBed)]
    [InlineData("The crops appear to be healthy.", ChatVerdict.WaterPot)]
    public void The_lifecycle_close_lines_classify(string text, ChatVerdict expected)
        => Assert.Equal(expected, ChatLine.Classify(SystemMessage, true, text));

    [Fact]
    public void A_gil_line_rides_the_same_structure_and_is_still_nothing()
    {
        // Capture 2026-08-23: spend lines arrive as SystemMessage/57 attributed to the local
        // player, exactly like a success line - which is the whole reason the words are matched
        // exactly and never with Contains. Structure alone would receipt every purchase.
        Assert.Equal(ChatVerdict.None, ChatLine.Classify(SystemMessage, true, "You spent 303 gil."));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(56)]
    [InlineData(58)]
    [InlineData(ErrorMessage)]
    public void The_right_words_on_the_wrong_channel_are_nothing(int logKind)
    {
        Assert.Equal(ChatVerdict.None, ChatLine.Classify(logKind, true, "You fertilize the crop."));
        Assert.Equal(ChatVerdict.None, ChatLine.Classify(logKind, true, "Pomace added to flowerpot."));
    }

    [Fact]
    public void Somebody_elses_fertilize_is_not_our_receipt()
    {
        Assert.Equal(ChatVerdict.None, ChatLine.Classify(SystemMessage, false, "You fertilize the crop."));
        Assert.Equal(ChatVerdict.None, ChatLine.Classify(SystemMessage, false, "Pomace added to flowerpot."));
    }

    [Theory]
    [InlineData("Additional pomace is not necessary in this flowerpot.")]
    [InlineData("This crop has already been sufficiently fertilized.")]
    public void Refusals_receipt_nothing(string refusal)
    {
        // On their own channel (where they actually land) and on the success channel with a
        // local actor - a refusal is not an action from any angle.
        Assert.Equal(ChatVerdict.None, ChatLine.Classify(ErrorMessage, false, refusal));
        Assert.Equal(ChatVerdict.None, ChatLine.Classify(SystemMessage, true, refusal));
    }

    [Theory]
    [InlineData("You fertilize the crop. ")]
    [InlineData(" You fertilize the crop.")]
    [InlineData("you fertilize the crop.")]
    [InlineData("You fertilize the crop")]
    [InlineData("Drift says: You fertilize the crop.")]
    [InlineData("pomace added to flowerpot.")]
    [InlineData("Pomace added to flowerpot")]
    [InlineData("")]
    public void Near_misses_are_nothing(string text)
        => Assert.Equal(ChatVerdict.None, ChatLine.Classify(SystemMessage, true, text));

    [Fact]
    public void An_unattributed_system_line_has_the_inspect_shape()
    {
        Assert.True(ChatLine.IsInspectLine(SystemMessage, unattributed: true));
        Assert.False(ChatLine.IsInspectLine(SystemMessage, unattributed: false));
        Assert.False(ChatLine.IsInspectLine(ErrorMessage, unattributed: true));
    }

    [Fact]
    public void A_success_line_and_an_inspect_line_are_never_the_same_line()
    {
        // The success shape is attributed, the inspect shape is not - so no line can be read
        // as both, and the adapter never has to choose between them.
        Assert.Equal(ChatVerdict.None, ChatLine.Classify(SystemMessage, false, "Royal Kukuru Bean"));
        Assert.False(ChatLine.IsInspectLine(SystemMessage, unattributed: false));
    }

    [Theory]
    [InlineData(ChatVerdict.FertilizeBed, SyncKind.Fertilize)]
    [InlineData(ChatVerdict.FertilizePot, SyncKind.Fertilize)]
    [InlineData(ChatVerdict.PlantPot, SyncKind.Plant)]
    [InlineData(ChatVerdict.TendBed, SyncKind.Tend)]
    [InlineData(ChatVerdict.WaterPot, SyncKind.PotWater)]
    [InlineData(ChatVerdict.PlantBed, SyncKind.Plant)]
    public void Every_verdict_names_its_kind(ChatVerdict verdict, SyncKind kind)
        => Assert.Equal(kind, ChatLine.KindOf(verdict));

    [Theory]
    [InlineData(ChatVerdict.FertilizePot)]
    [InlineData(ChatVerdict.PlantPot)]
    [InlineData(ChatVerdict.WaterPot)]
    public void Pot_sentences_name_a_pot(ChatVerdict verdict)
        => Assert.True(ChatLine.IsPotVerdict(verdict));

    [Theory]
    [InlineData(ChatVerdict.FertilizeBed)]
    [InlineData(ChatVerdict.TendBed)]
    [InlineData(ChatVerdict.PlantBed)]
    public void Bed_sentences_name_a_bed(ChatVerdict verdict)
        => Assert.False(ChatLine.IsPotVerdict(verdict));

    [Fact]
    public void None_is_not_an_act_and_has_neither_kind_nor_container()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChatLine.KindOf(ChatVerdict.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChatLine.IsPotVerdict(ChatVerdict.None));
    }

    // ---- The bed sow line (capture 2026-10-18: three hand sows, FC Estate patch 1) ----------

    [Theory]
    [InlineData("You plant seeds in the 1st bed, 1st patch.", 0, 0)]
    [InlineData("You plant seeds in the 2nd bed, 1st patch.", 0, 1)]
    [InlineData("You plant seeds in the 3rd bed, 1st patch.", 0, 2)]
    [InlineData("You plant seeds in the 8th bed, 3rd patch.", 2, 7)]
    public void The_captured_sow_lines_classify_and_name_their_bed(string text, int patch, int bed)
    {
        Assert.Equal(ChatVerdict.PlantBed, ChatLine.Classify(SystemMessage, true, text));
        Assert.Equal((patch, bed), ChatLine.ParseBedSow(text));
    }

    [Theory]
    [InlineData("You plant seeds in the 1nd bed, 1st patch.")]   // wrong suffix
    [InlineData("You plant seeds in the 9th bed, 1st patch.")]   // no ninth bed
    [InlineData("You plant seeds in the 0th bed, 1st patch.")]
    [InlineData("You plant seeds in the 1st bed, 1st patch")]    // no full stop
    [InlineData("He said You plant seeds in the 1st bed, 1st patch.")]
    [InlineData("You plant seeds in the 1st bed, 1st patch. Nice.")]
    public void Anything_off_the_sow_shape_is_nothing(string text)
    {
        Assert.Null(ChatLine.ParseBedSow(text));
        Assert.Equal(ChatVerdict.None, ChatLine.Classify(SystemMessage, true, text));
    }

    [Fact]
    public void The_sow_words_from_anyone_else_or_on_another_channel_are_nothing()
    {
        const string line = "You plant seeds in the 1st bed, 1st patch.";
        Assert.Equal(ChatVerdict.None, ChatLine.Classify(SystemMessage, false, line));
        Assert.Equal(ChatVerdict.None, ChatLine.Classify(LootNotice, true, line));
    }
}

public class FertilizeCorroborationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 21, 0, 0, TimeSpan.Zero);
    private const ushort Kukuru = 88;
    private const ushort Sunflower = 103;

    [Fact]
    public void Agreeing_species_proceeds()
    {
        var (proceed, _) = FertilizeCorroboration.Check(
            Kukuru, Now.AddSeconds(-3), Kukuru, Now);
        Assert.True(proceed);
    }

    [Fact]
    public void A_live_disagreement_stops_the_receipt()
    {
        var (proceed, reason) = FertilizeCorroboration.Check(
            Kukuru, Now.AddSeconds(-3), Sunflower, Now);
        Assert.False(proceed);
        Assert.Equal("crop name disagrees with the bed", reason);
    }

    [Fact]
    public void No_crop_name_line_proceeds()
    {
        Assert.True(FertilizeCorroboration.Check(0, null, Sunflower, Now).Proceed);
        Assert.True(FertilizeCorroboration.Check(Kukuru, null, Sunflower, Now).Proceed);
        Assert.True(FertilizeCorroboration.Check(0, Now.AddSeconds(-1), Sunflower, Now).Proceed);
    }

    [Fact]
    public void A_stale_crop_name_line_proceeds()
    {
        var stale = Now - FertilizeCorroboration.InspectWindow - TimeSpan.FromSeconds(1);
        var (proceed, reason) = FertilizeCorroboration.Check(Kukuru, stale, Sunflower, Now);
        Assert.True(proceed);
        Assert.Equal("crop-name line too old to corroborate against", reason);
    }

    [Fact]
    public void A_line_from_the_future_is_not_evidence()
        => Assert.True(FertilizeCorroboration.Check(Kukuru, Now.AddMinutes(5), Sunflower, Now).Proceed);

    [Fact]
    public void An_unknown_bed_species_proceeds()
    {
        // The unbound-patch case: contradicting evidence needs two known values, and demanding
        // corroboration here would kill exactly the receipts that come first.
        var (proceed, reason) = FertilizeCorroboration.Check(
            Kukuru, Now.AddSeconds(-2), 0, Now);
        Assert.True(proceed);
        Assert.Equal("bed species unknown - nothing to contradict", reason);
    }

    [Fact]
    public void The_window_edge_is_still_evidence()
    {
        var edge = Now - FertilizeCorroboration.InspectWindow;
        Assert.False(FertilizeCorroboration.Check(Kukuru, edge, Sunflower, Now).Proceed);
    }
}
