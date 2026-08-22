using BalambGarden.Engine.Census;

namespace BalambGarden.Engine.Ledger;

/// <summary>One claimed bed or pot: current state + the observation ring that feeds
/// brackets, wilt clocks, and provenance (spec: approach C).</summary>
public sealed class ClaimedBed
{
    public const int RingCapacity = 8;

    public required EstateKey Estate { get; init; }
    public required int MapKey { get; init; }
    public required int PatchOrdinal { get; init; }
    public required int BedSlot { get; init; }
    public bool IsPot { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("ClaimedAt")]
    public DateTimeOffset FirstRecorded { get; init; }
    public DateTimeOffset? LastTended { get; set; }

    public List<Observation> RingStorage { get; init; } = [];   // public for serialization

    public IReadOnlyList<Observation> Ring => RingStorage;
    public Observation? Latest => RingStorage.Count == 0 ? null : RingStorage[^1];

    public void Observe(Observation o)
    {
        RingStorage.Add(o);
        RingStorage.Sort((a, b) => a.At.CompareTo(b.At));
        if (RingStorage.Count > RingCapacity)
            RingStorage.RemoveRange(0, RingStorage.Count - RingCapacity);
    }

    /// <summary>New tenancy: the live map contradicted the ledger's idea of what grows here
    /// (2026-08-18 ruling: the game wins on content mismatch), so every claim about the OLD
    /// plant - its observations, its tend clock - leaves with it. The row itself survives;
    /// the bed is still ours, it just holds something we haven't watched grow.</summary>
    public void Rebase()
    {
        RingStorage.Clear();
        LastTended = null;
    }
}
