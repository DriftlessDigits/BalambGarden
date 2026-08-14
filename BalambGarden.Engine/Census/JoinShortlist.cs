namespace BalambGarden.Engine.Census;

/// <summary>Diff-pattern candidate finder: ward keys preserve patch-id pairwise diffs per
/// estate (proven 08-12, low-byte rule dead). A shortlist only PROPOSES - binding
/// requires a receipt. There is deliberately no auto-bind path here.</summary>
public static class JoinShortlist
{
    public static IReadOnlyList<IReadOnlyList<int>> Candidates(
        IReadOnlyList<ushort> patchIdsInOrdinalOrder, IReadOnlyList<int> wardKeys)
    {
        var results = new List<IReadOnlyList<int>>();
        if (patchIdsInOrdinalOrder.Count == 0)
            return results;

        var diffs = new int[patchIdsInOrdinalOrder.Count - 1];
        for (var i = 1; i < patchIdsInOrdinalOrder.Count; i++)
            diffs[i - 1] = patchIdsInOrdinalOrder[i] - patchIdsInOrdinalOrder[i - 1];

        var keys = wardKeys.Distinct().Order().ToArray();
        var keySet = keys.ToHashSet();
        foreach (var start in keys)
        {
            var candidate = new List<int> { start };
            var current = start;
            var ok = true;
            foreach (var d in diffs)
            {
                current += d;
                if (!keySet.Contains(current)) { ok = false; break; }
                candidate.Add(current);
            }
            if (ok)
                results.Add(candidate);
        }
        return results;
    }
}
