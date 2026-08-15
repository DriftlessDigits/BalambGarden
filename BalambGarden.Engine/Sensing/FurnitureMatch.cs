using System.Numerics;

namespace BalambGarden.Engine.Sensing;

/// <summary>One entry of the housing furniture vector, reduced to the two fields identity
/// needs: where it stands and which slot of the vector it is.</summary>
public readonly record struct FurniturePlacement(int Index, Vector3 Position);

/// <summary>
/// Matching a placed object on the screen to its entry in the housing furniture vector,
/// by position. This is the pure half of pot identity - see the receipt on the caller.
///
/// <para>Fail-closed by construction: it answers with an index only when exactly one entry
/// can be meant. Two entries at one spot, or two inside the tolerance, is not an answer -
/// it is a question - and the caller renders untracked rather than pick one.</para>
/// </summary>
public static class FurnitureMatch
{
    /// <summary>How far apart the two readings of one placement may be. They have been seen
    /// AGREEING (08-15: object 'Oasis Flowerpot' at &lt;-1.5,-0.0,-1.3&gt; against furniture
    /// idx=0 at the same printed coordinates), so this is not slack for a guess - it is
    /// slack for the last bits of a float, and it is an order of magnitude tighter than the
    /// 0.6y gap between the closest two pots we have ever measured.</summary>
    public const float Tolerance = 0.05f;

    /// <summary>The furniture index standing at this position, or null when nothing does
    /// or when more than one thing might.</summary>
    public static int? IndexAt(IReadOnlyList<FurniturePlacement> furniture, Vector3 position)
    {
        int? exact = null;
        int? nearest = null;
        var nearestDistance = float.MaxValue;
        var candidates = 0;

        foreach (var entry in furniture)
        {
            if (entry.Position == position)
            {
                // Two different entries claiming the identical spot: no evidence either way.
                if (exact is not null && exact != entry.Index)
                    return null;
                exact = entry.Index;
                continue;
            }

            var distance = Vector3.Distance(entry.Position, position);
            if (distance > Tolerance)
                continue;

            candidates++;
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = entry.Index;
            }
        }

        if (exact is not null)
            return exact;

        // Only an unambiguous near-miss counts. "Nearest of two" would be a guess dressed
        // as a measurement, and a wrong pot identity is the one failure this must not have.
        return candidates == 1 ? nearest : null;
    }
}
