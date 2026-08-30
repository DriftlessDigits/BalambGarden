using BalambGarden.Engine.Census;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

/// <summary>The mint sweep's pure half (QA pass 2026-08-29: hoisted from the pump so the
/// stance rule is desk-tested). Stance is part of the identity - indoor HouseId carries the
/// room - so a pass mints only the rows matching where the player stands.</summary>
public class WireIdentityTests
{
    private static readonly EstateKey Garden = new(340, 11, 32);
    private static readonly byte[] Key = HouseholdKeys.NamingKey("test household");
    private const ulong House = 0x0037015401CB0039;

    private static LedgerStore WithRows()
    {
        var ledger = new LedgerStore();
        ledger.Beds.Add(new ClaimedBed
            { Estate = Garden, MapKey = 2, PatchOrdinal = 0, BedSlot = 3, IsPot = false });
        ledger.Beds.Add(new ClaimedBed
            { Estate = Garden, MapKey = 208, PatchOrdinal = 208, BedSlot = 0, IsPot = true });
        return ledger;
    }

    [Fact]
    public void Inside_mints_pots_only()
    {
        var ledger = WithRows();
        var minted = WireIdentity.MintPending(ledger, Garden, inside: true, Key, House, _ => 0x3312);
        Assert.Equal(1, minted);
        Assert.Null(ledger.Beds[0].WireEntity);
        Assert.Equal(EntityName.ForPot(Key, House, 208), ledger.Beds[1].WireEntity);
    }

    [Fact]
    public void Outside_mints_beds_only()
    {
        var ledger = WithRows();
        var minted = WireIdentity.MintPending(ledger, Garden, inside: false, Key, House, _ => 0x3312);
        Assert.Equal(1, minted);
        Assert.Equal(EntityName.ForBed(Key, House, 0x3312, 3), ledger.Beds[0].WireEntity);
        Assert.Null(ledger.Beds[1].WireEntity);
    }

    [Fact]
    public void A_bed_with_no_gimmick_in_range_stays_null_for_the_next_pass()
    {
        var ledger = WithRows();
        var minted = WireIdentity.MintPending(ledger, Garden, inside: false, Key, House, _ => 0);
        Assert.Equal(0, minted);
        Assert.Null(ledger.Beds[0].WireEntity);
    }

    [Fact]
    public void No_house_id_mints_nothing()
    {
        var ledger = WithRows();
        Assert.Equal(0, WireIdentity.MintPending(ledger, Garden, inside: true, Key, 0, _ => 0x3312));
    }

    [Fact]
    public void Already_minted_rows_are_left_alone()
    {
        var ledger = WithRows();
        ledger.Beds[1].WireEntity = "already";
        var minted = WireIdentity.MintPending(ledger, Garden, inside: true, Key, House, _ => 0);
        Assert.Equal(0, minted);
        Assert.Equal("already", ledger.Beds[1].WireEntity);
    }
}
