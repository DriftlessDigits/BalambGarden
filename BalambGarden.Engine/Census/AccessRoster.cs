namespace BalambGarden.Engine.Census;

/// <summary>One estate the game itself lists for this player - a teleport-list row or an
/// owned-estate read - converted to our key. Kind is the game's EstateType name, kept for
/// labels only.</summary>
public sealed record RosterEstate(EstateKey Key, string Kind);

/// <summary>The access roster (spec: Permission Architecture, 2026-08-15). Presence here is
/// the game saying "you can act at this estate" (Drift's v1 ruling: rostered = assume
/// actionable; the composed menu refuses per-verb weirdness gracefully at act time).
/// Coverage is also the census scope: an estate not covered is not tracked at all.</summary>
public sealed class AccessRoster(IReadOnlyList<RosterEstate> estates)
{
    public IReadOnlyList<RosterEstate> Estates { get; } = estates;

    public static readonly AccessRoster Empty = new([]);

    /// <summary>From a HouseId's own decoded fields. Receipt (roster recon 2026-08-15):
    /// the embedded HouseId carries RAW ward/plot - Gardener's row read ward=11 plot=32,
    /// already the ledger convention - and room 0 is the house itself. Null for anything
    /// that fits no receipted shape: a row we cannot name is dropped loudly by the caller,
    /// never misfiled quietly here.</summary>
    public static EstateKey? FromHouseParts(
        ushort territory, int ward, int plot, int room, bool isApartment, int division)
    {
        if (territory == 0 || ward < 0)
            return null;
        if (isApartment)
            return room > 0 ? EstateKey.Apartment(territory, ward, division, room) : null;
        if (plot < 0)
            return null;
        return room > 0
            ? new EstateKey(territory, ward, plot, room)
            : new EstateKey(territory, ward, plot);
    }

    /// <summary>Whether the game-granted set contains this estate. A house row covers its
    /// whole plot, rooms included (recon receipt: chambers have no row of their own - the
    /// FC row is their parent). An apartment row covers exactly its own room - the
    /// building's other doors are other people's homes.</summary>
    public bool Covers(EstateKey estate)
    {
        foreach (var entry in Estates)
        {
            if (entry.Key == estate)
                return true;
            if (!entry.Key.IsApartment && entry.Key.Room < 0
                && entry.Key.TerritoryId == estate.TerritoryId
                && entry.Key.Ward == estate.Ward
                && entry.Key.Plot == estate.Plot)
                return true;
        }
        return false;
    }
}
