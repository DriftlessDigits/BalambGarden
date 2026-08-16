using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace BalambGarden.Game;

/// <summary>The flowerpot id whitelist for the pot-gate, read off the game's own
/// HousingFurniture sheet (the probe's ruling stands: the discriminator comes off the
/// game's data, never off a hardcoded list alone). Sheet row - 0x20000 = furniture vector
/// id, receipted 08-16 (Oasis 197053 -> 65981, Riviera 197051 -> 65979).
///
/// <para>The receipted three (Riviera 65979, Glade 65980, Oasis 65981) are the floor: the
/// derived set must contain them, and does even if a sheet or name ever drifts - drift
/// logs loudly instead of silently untracking Sam's pots. A fourth Flowerpot item, should
/// one ever ship, walks in through the name scan.</para></summary>
internal static class FlowerpotSheet
{
    private static readonly uint[] Receipted = [65979, 65980, 65981];

    private static IReadOnlyCollection<uint>? ids;

    internal static IReadOnlyCollection<uint> Ids => ids ??= Derive();

    private static IReadOnlyCollection<uint> Derive()
    {
        var derived = new HashSet<uint>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<HousingFurniture>())
        {
            if (row.RowId >= 0x20000
                && row.Item.ValueNullable?.Name.ExtractText().EndsWith("Flowerpot") == true)
                derived.Add(row.RowId - 0x20000);
        }

        var missing = Receipted.Where(id => !derived.Contains(id)).ToList();
        if (missing.Count > 0)
            Plugin.Log.Warning(
                $"[PotGate] sheet scan missed receipted flowerpot id(s) {string.Join(",", missing)}"
                + " - keeping them anyway; the sheet or the name drifted, look at it");
        derived.UnionWith(Receipted);

        Plugin.Log.Information(
            $"[PotGate] flowerpot ids: {string.Join(",", derived.OrderBy(i => i))}");
        return derived;
    }
}
