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

    /// <summary>This row's pseudonym on the household wire, minted at census time from live
    /// game data (piece 4, ruled 2026-08-29) - the HMAC inputs (raw HouseId, gimmick patch
    /// id) only exist while standing at the estate. Null until a visit mints it; wiped
    /// wholesale when the household phrase changes (a new naming key invalidates every
    /// cached id at once).</summary>
    public string? WireEntity { get; set; }

    public List<Observation> RingStorage { get; init; } = [];   // public for serialization

    public IReadOnlyList<Observation> Ring => RingStorage;

    /// <summary>Whether the watering clock was set by a household receipt rather than these
    /// hands (sync ruling 10: display marks external, local says nothing extra). DERIVED,
    /// never stored twice (no-dual-state law): external is true exactly when the ring's
    /// External observation carries the clock's own instant - ForeignApply writes the two
    /// together. A later local tend moves the clock past the stamp and the answer flips
    /// back by construction.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LastTendedIsExternal => LastTended is { } t && HasExternalAt(t);

    /// <summary>Whether the ring holds an External observation at this exact instant - the
    /// ONE rule both the display flag above and ForeignApply's replay dedup share, so the
    /// two can never drift apart on what "external" means.</summary>
    public bool HasExternalAt(DateTimeOffset at)
        => RingStorage.Any(o => o.Source == ObservationSource.External && o.At == at);
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
