namespace BalambGarden.Engine.Sync;

/// <summary>One harvest listening for its cross seed (ruled 2026-10-18). A harvest prints the crop
/// line, then - only when the cross takes - a seed-bag line in the same instant ("You obtain 12
/// mirror apples." then "You obtain a bag of sun lemon seeds."); a miss prints nothing at all
/// (capture 2026-10-18: four harvests, three seed lines, every one in the crop line's
/// millisecond). Because the game never announces a miss, the only way to record one is to
/// listen for a fixed window and close it empty.
///
/// <para>THREE STATES on the harvest row, never two: crossed (qty &gt; 0, item), missed (0, null),
/// and NOT HEARD (null, null) - every harvest nobody listened for (rows before this existed, a
/// chain act that timed out unconfirmed, an install that does not report seeds). Not-heard rows
/// never count as misses, so the cross rate is computed only over harvests that were heard.</para>
///
/// <para>A pair can cross to more than one seed (Krakka Root + Mirror Apple gave both Curiel Root
/// and Sun Lemon the same night), so the item is recorded, not just yes/no. A second seed line
/// of the SAME item adds to the count; a second line of a DIFFERENT item is refused - one row has
/// one seed column, and a shape the game has never shown is not guessed at.</para></summary>
public sealed class HarvestGather(DateTimeOffset cropLineAt)
{
    /// <summary>How long a harvest listens after its crop line. Every observed seed line landed
    /// in the same millisecond; two seconds is generous margin and still invisible to the player.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    public DateTimeOffset CropLineAt { get; } = cropLineAt;
    public uint? SeedItem { get; private set; }
    public int SeedQty { get; private set; }

    /// <summary>Offers a seed line. False when it is outside the window (late - the caller says
    /// so) or names a different seed than one already taken.</summary>
    public bool Offer(uint seedItem, int qty, DateTimeOffset at)
    {
        if (at < CropLineAt || at - CropLineAt > Window || qty <= 0)
            return false;
        if (SeedItem is { } taken && taken != seedItem)
            return false;
        SeedItem = seedItem;
        SeedQty += qty;
        return true;
    }

    /// <summary>The listening window has closed.</summary>
    public bool Due(DateTimeOffset now) => now - CropLineAt > Window;

    /// <summary>What the harvest row records. <paramref name="heard"/> false (the window was cut
    /// short and nothing arrived - a plugin unload mid-listen) is NOT HEARD, never a miss.</summary>
    public (int? SeedQty, uint? SeedItem) Result(bool heard)
        => SeedQty > 0 ? (SeedQty, SeedItem)
         : heard ? (0, null)
         : (null, null);
}
