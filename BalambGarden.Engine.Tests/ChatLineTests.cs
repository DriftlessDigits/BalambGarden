using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

public class ChatLineTests
{
    private const int SystemMessage = 57;
    private const int ErrorMessage = 60;

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
