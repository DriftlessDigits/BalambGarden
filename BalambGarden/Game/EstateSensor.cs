using BalambGarden.Engine.Census;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BalambGarden.Game;

/// <summary>Estate identity from HousingManager, raw 0-based (verified via probe
/// 08-12/08-13). Room rides only when inside; outdoors an estate is (territory,
/// ward, plot).</summary>
internal static unsafe class EstateSensor
{
    internal static EstateKey? Current()
    {
        var housing = HousingManager.Instance();
        if (housing == null || housing->CurrentTerritory == null)
            return null;

        var ward = housing->GetCurrentWard();
        var plot = housing->GetCurrentPlot();
        if (ward < 0 || plot < 0)
            return null;

        var room = housing->IsInside() ? housing->GetCurrentRoom() : -1;
        // ClientState hands territory out as uint; the ledger key is ushort (every real
        // territory id fits) - narrow here, at the boundary.
        return new EstateKey((ushort)Plugin.ClientState.TerritoryType, ward, plot, room);
    }

    internal static bool IsInside()
    {
        var housing = HousingManager.Instance();
        return housing != null && housing->IsInside();
    }
}
