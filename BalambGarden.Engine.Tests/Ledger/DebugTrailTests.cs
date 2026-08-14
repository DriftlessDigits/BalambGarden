using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Ledger;

public class DebugTrailTests
{
    [Fact]
    public void AppendsOneJsonLinePerReceipt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"balamb-trail-{Guid.NewGuid():N}.jsonl");
        try
        {
            var trail = new DebugTrail(path);
            var e = new ReceiptEvent(new EstateKey(340, 11, 32), 0, 3, ReceiptVerb.Tend,
                0x41, 1, DateTimeOffset.Parse("2026-08-13T19:00:00Z"));
            trail.Append(e);
            trail.Append(e with { BedSlot = 4 });

            var lines = DebugTrail.ReadLines(path);
            Assert.Equal(2, lines.Count);
            Assert.Contains("\"BedSlot\":3", lines[0]);
            Assert.Contains("Tend", lines[0]);
        }
        finally { File.Delete(path); }
    }
}
