using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sensing;
using Xunit;

namespace BalambGarden.Engine.Tests.Census;

public class JoinTests
{
    [Fact] // capture 08-13: 0x05013927 = bed 5, ordinal 1, patch-id 0x3927 (FC estate)
    public void GimmickDecodesFcBed()
    {
        var g = GimmickId.Decode(0x05013927);
        Assert.Equal(5, g.BedIndex);
        Assert.Equal(1, g.PatchOrdinal);
        Assert.Equal(0x3927, g.PatchId);
    }

    [Fact] // capture 08-13: 0x0200200A = bed 2, ordinal 0, patch-id 0x200A (Gardener)
    public void GimmickDecodesGardenerBed()
    {
        var g = GimmickId.Decode(0x0200200A);
        Assert.Equal(2, g.BedIndex);
        Assert.Equal(0, g.PatchOrdinal);
        Assert.Equal(0x200A, g.PatchId);
    }

    // Ward keys observed 08-13 at the shared Gardener/FC ward (subset with both estates present)
    private static readonly int[] WardKeys =
        [62, 110, 116, 117, 285, 286, 290, 365, 447, 891, 1067, 1150, 1293, 1313, 1319];

    [Fact] // Gardener: patch-ids 0x200A/0x2010/0x2011, diffs +6,+1 -> keys 110/116/117
    public void GardenerDiffPatternShortlists()
    {
        var candidates = JoinShortlist.Candidates([0x200A, 0x2010, 0x2011], WardKeys);
        Assert.Contains(candidates, c => c.SequenceEqual([110, 116, 117]));
    }

    [Fact] // FC: patch-ids 0x390D/0x3921/0x3927, diffs +20,+6 -> keys 1293/1313/1319
    public void FcDiffPatternShortlists()
    {
        var candidates = JoinShortlist.Candidates([0x390D, 0x3921, 0x3927], WardKeys);
        Assert.Contains(candidates, c => c.SequenceEqual([1293, 1313, 1319]));
    }

    [Fact] // a diff pattern nothing matches -> empty shortlist, never a forced guess
    public void NoMatchMeansEmptyShortlist()
        => Assert.Empty(JoinShortlist.Candidates([0x1000, 0x1003, 0x1009], WardKeys));

    [Fact] // single-patch estates (Drift's house) have no diffs: every key is a candidate
    public void SinglePatchShortlistsEveryKey()
        => Assert.Equal(WardKeys.Length, JoinShortlist.Candidates([0x200A], WardKeys).Count);
}
