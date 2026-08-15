using BalambGarden.Engine.Census;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

public class SowPromptTests
{
    // Verbatim from captures/2026-08-15-plant-flow.log, SelectYesno AtkValues[0].
    private const string Captured =
        "Prepare the bed with a bag of potting soil and a bag of daisy seeds?";

    [Fact]
    public void ParsesTheCapturedPrompt()
    {
        var parts = SowPrompt.Parse(Captured);
        Assert.NotNull(parts);
        Assert.Equal("potting soil", parts!.Soil);
        Assert.Equal("daisy", parts.Seed);
    }

    [Fact] // multi-word item names are the normal case outdoors
    public void ParsesMultiWordItemNames()
    {
        var parts = SowPrompt.Parse(
            "Prepare the bed with a bag of grade 2 thanalan topsoil and a bag of ala mhigan mustard seeds?");
        Assert.NotNull(parts);
        Assert.Equal("grade 2 thanalan topsoil", parts!.Soil);
        Assert.Equal("ala mhigan mustard", parts.Seed);
    }

    [Fact] // dialogue text arrives wrapped; a newline must not defeat the parse
    public void ParsesAcrossLineBreaks()
    {
        var parts = SowPrompt.Parse(
            "Prepare the bed with a bag of grade 1 shroud topsoil\nand a bag of almond seeds?");
        Assert.NotNull(parts);
        Assert.Equal("almond", parts!.Seed);
    }

    [Fact]
    public void RejectsUnrelatedPrompts()
    {
        Assert.Null(SowPrompt.Parse("Remove the crop from this bed?"));
        Assert.Null(SowPrompt.Parse(""));
    }

    [Fact] // table names are title case and carry the " Seeds" suffix the prompt drops
    public void MatchesTableNamesCaseAndSuffixInsensitively()
    {
        var check = SowPrompt.Check(
            "Prepare the bed with a bag of grade 2 thanalan topsoil and a bag of almond seeds?",
            expectedSoil: "Grade 2 Thanalan Topsoil",
            expectedSeed: "Almond Seeds");
        Assert.True(check.Ok);
        Assert.Null(check.Reason);
    }

    [Fact]
    public void MismatchedSeedIsRefusedWithBothNames()
    {
        var check = SowPrompt.Check(
            "Prepare the bed with a bag of grade 2 thanalan topsoil and a bag of almond seeds?",
            expectedSoil: "Grade 2 Thanalan Topsoil",
            expectedSeed: "Curiel Root Seeds");
        Assert.False(check.Ok);
        Assert.Equal("planted item mismatch: expected Curiel Root Seeds, dialog says almond", check.Reason);
    }

    [Fact]
    public void MismatchedSoilIsRefused()
    {
        var check = SowPrompt.Check(
            Captured, expectedSoil: "Grade 3 Shroud Topsoil", expectedSeed: null);
        Assert.False(check.Ok);
        Assert.Contains("expected Grade 3 Shroud Topsoil", check.Reason);
        Assert.Contains("dialog says potting soil", check.Reason);
    }

    [Fact] // no expectation = no claim: the human chose, we only report what was chosen
    public void NullExpectationsAcceptWhateverWasFilled()
    {
        var check = SowPrompt.Check(Captured, expectedSoil: null, expectedSeed: null);
        Assert.True(check.Ok);
        Assert.Equal("daisy", check.Parts!.Seed);
    }

    [Fact] // an unreadable prompt is never a pass, even with nothing to compare against
    public void UnparseablePromptFailsTheCheck()
    {
        var check = SowPrompt.Check("Discard this item?", null, null);
        Assert.False(check.Ok);
        Assert.Contains("unrecognized sow prompt", check.Reason);
    }
}
