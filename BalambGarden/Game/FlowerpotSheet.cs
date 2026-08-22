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
/// logs loudly instead of silently untracking Drift's pots. A fourth Flowerpot item, should
/// one ever ship, walks in through the name scan.</para></summary>
internal static class FlowerpotSheet
{
    // Riviera 197051, Glade 197052, Oasis 197053 - all receipted in Drift's pots. The same
    // rows appear as GameObject.BaseId on a placed pot's object (08-16, four FC pots all
    // read 0x301BD = Oasis), which is what the object sensor gates on.
    private static readonly uint[] Receipted = [197051, 197052, 197053];

    private static IReadOnlyCollection<uint>? rows;

    internal static IReadOnlyCollection<uint> Rows => rows ??= Derive();

    private static IReadOnlyCollection<uint> Derive()
    {
        var derived = new HashSet<uint>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<HousingFurniture>())
        {
            if (row.Item.ValueNullable?.Name.ExtractText().EndsWith("Flowerpot") == true)
                derived.Add(row.RowId);
        }

        var missing = Receipted.Where(id => !derived.Contains(id)).ToList();
        if (missing.Count > 0)
            Plugin.Log.Warning(
                $"[PotGate] sheet scan missed receipted flowerpot row(s) {string.Join(",", missing)}"
                + " - keeping them anyway; the sheet or the name drifted, look at it");
        derived.UnionWith(Receipted);

        Plugin.Log.Information(
            $"[PotGate] flowerpot rows: {string.Join(",", derived.OrderBy(i => i))}");
        return derived;
    }
}
