using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Sync;

/// <summary>The mint sweep's pure half (piece 4; hoisted from the pump at the QA pass so
/// the stance rule is desk-tested). Entity ids mint at census time because the HMAC inputs
/// (raw HouseId, gimmick patch id) only exist standing at the estate; the pump contributes
/// exactly those two sensed values and keeps the throttle, the save and the log line.
///
/// <para>STANCE IS PART OF THE IDENTITY: indoors the HouseId carries the room, outdoors it
/// is the plot's - so a pass mints only rows matching where the player stands, or two
/// installs on opposite sides of the front door would mint two names for one container.
/// A bed whose gimmick is out of scan range stays null for the next pass (patchIdOf's
/// honest 0, refused by <see cref="EntityName.For"/>).</para></summary>
public static class WireIdentity
{
    /// <summary>Mints wire entities for this estate's unminted rows on the current stance.
    /// Returns how many minted.</summary>
    public static int MintPending(
        LedgerStore ledger, EstateKey estate, bool inside,
        byte[] namingKey, ulong houseId, Func<int, ushort> patchIdOf)
    {
        var minted = 0;
        foreach (var row in ledger.Beds)
        {
            if (row.Estate != estate || row.IsPot != inside || row.WireEntity is not null)
                continue;

            var entity = EntityName.For(
                namingKey, houseId, row.IsPot,
                potKey: row.MapKey,
                patchId: row.IsPot ? (ushort)0 : patchIdOf(row.PatchOrdinal),
                bedSlot: row.BedSlot);
            if (entity is null)
                continue;

            row.WireEntity = entity;
            minted++;
        }
        return minted;
    }
}
