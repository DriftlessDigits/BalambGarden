using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

public class JoinConfirmTests
{
    private static IReadOnlyList<BedReading> Patch(params ushort[] species)
        => species.Select((s, i) => new BedReading(i, s, 1, 0, s != 0)).ToList();

    // Two candidates propose different keys for ordinal 0; the map shows Fig (0x41)
    // at slot 3 only under key 110 -> the receipt confirms candidate [110,116,117].
    [Fact]
    public void ReceiptSpeciesMatchPicksTheOneCandidate()
    {
        var candidates = new[] { new[] { 110, 116, 117 }, new[] { 285, 291, 292 } };
        var map = new Dictionary<int, IReadOnlyList<BedReading>>
        {
            [110] = Patch(0, 0, 0, 0x41, 0, 0, 0, 0),
            [285] = Patch(0, 0, 0, 0x11, 0, 0, 0, 0),
        };
        var confirmed = JoinConfirm.Confirm(
            candidates, patchOrdinal: 0, bedSlot: 3, speciesIndex: 0x41,
            key => map.GetValueOrDefault(key));
        Assert.NotNull(confirmed);
        Assert.Equal([110, 116, 117], confirmed);
    }

    [Fact] // both candidates show the same species at that slot -> ambiguous -> null
    public void AmbiguousMatchBindsNothing()
    {
        var candidates = new[] { new[] { 110 }, new[] { 285 } };
        var map = new Dictionary<int, IReadOnlyList<BedReading>>
        {
            [110] = Patch(0x41), [285] = Patch(0x41),
        };
        Assert.Null(JoinConfirm.Confirm(candidates, 0, 0, 0x41, k => map.GetValueOrDefault(k)));
    }

    [Fact] // species mismatch everywhere -> null, never a forced guess
    public void NoMatchBindsNothing()
    {
        var candidates = new[] { new[] { 110 } };
        var map = new Dictionary<int, IReadOnlyList<BedReading>> { [110] = Patch(0x11) };
        Assert.Null(JoinConfirm.Confirm(candidates, 0, 0, 0x41, k => map.GetValueOrDefault(k)));
    }

    [Fact] // a candidate whose key is missing from the map simply doesn't survive
    public void MissingMapEntryEliminatesCandidate()
    {
        var candidates = new[] { new[] { 110 }, new[] { 999 } };
        var map = new Dictionary<int, IReadOnlyList<BedReading>> { [110] = Patch(0x41) };
        Assert.Equal([110], JoinConfirm.Confirm(candidates, 0, 0, 0x41, k => map.GetValueOrDefault(k)));
    }
}
