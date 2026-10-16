using System.Text.Json.Serialization;
using BalambGarden.Engine.Census;

namespace BalambGarden.Engine.Ledger;

/// <summary>One claimed bed or pot: its identity, its current reading, and the TENANCY RECORD -
/// what is known about the plant growing there now (infrastructure fix, ruled 2026-10-14; design
/// doc "Balamb Garden - Infrastructure Fix"). Replaces the eight-slot observation ring, which the
/// census filled one entry per READ and so held about a minute of history and evicted every
/// plant anchor it was ever given.
///
/// <para>THE RULE every write path below obeys (ruled 2026-10-14): an older receipt never
/// overwrites newer evidence. If it agrees with the current plant it adds its evidence (an old
/// stage-2 sighting tightens the stage-2 bracket); if it contradicts, it goes to history only.
/// Neither ever resets the bed. The fence (<see cref="TenancyResetAt"/>, a reset instant that only
/// moves forward) is the special case where the newer evidence IS a reset: wire facts at or
/// before it describe a plant that already left. Live acts and live reads on this install are
/// never fenced and never "older" - the game wins (2026-08-18), and a fence ahead of wall-clock
/// can only be another install's clock.</para></summary>
public sealed class ClaimedBed
{
    // ---- identity (unchanged from v2)
    public required EstateKey Estate { get; init; }
    public required int MapKey { get; init; }
    public required int PatchOrdinal { get; init; }
    public required int BedSlot { get; init; }
    public bool IsPot { get; init; }
    [JsonPropertyName("ClaimedAt")]
    public DateTimeOffset FirstRecorded { get; init; }

    /// <summary>This row's pseudonym on the household wire, minted at census time from live
    /// game data (piece 4, ruled 2026-08-29) - the HMAC inputs (raw HouseId, gimmick patch
    /// id) only exist while standing at the estate. Null until a visit mints it; wiped
    /// wholesale when the household phrase changes (a new naming key invalidates every
    /// cached id at once).</summary>
    public string? WireEntity { get; set; }

    // ---- the current reading = the species of record
    /// <summary>What the bed last read as, and when we last LOOKED (Q6, ruled 2026-10-14: the
    /// timestamp means last look, not last change - the stage brackets carry change times).
    /// Written by every live sighting; moved forward by newer wire evidence; nulled by a rebase.
    /// Null = the record holds no contents (display "empty").</summary>
    public Observation? Latest { get; set; }

    // ---- the tenancy
    /// <summary>The sowing instant, when a plant receipt witnessed it (either hand). The ONE
    /// legitimate anchor for a ripe time.</summary>
    public DateTimeOffset? PlantedAt { get; set; }
    public Hand? PlantedBy { get; set; }
    /// <summary>What the sowing said was planted. Agreement check only - the species of record
    /// is always <see cref="Latest"/> when a reading exists.</summary>
    public ushort PlantedSpecies { get; set; }

    /// <summary>First/last instant each stage (1..4) was seen during this tenancy.</summary>
    public Dictionary<byte, StageBracket> StageSeen { get; init; } = [];

    /// <summary>The water clock, whose hand set it, and what that hand said was growing.</summary>
    public DateTimeOffset? LastTended { get; set; }
    public Hand? LastTendedBy { get; set; }
    public ushort LastTendedSpecies { get; set; }

    /// <summary>THE FENCE: the instant of the last reset, and why. Only ever moves forward.</summary>
    public DateTimeOffset? TenancyResetAt { get; set; }
    public RebaseReason? TenancyResetReason { get; set; }

    /// <summary>v2 read seam only: the old observation ring, folded into the record by
    /// <see cref="LedgerMigration.FoldLegacyRings"/> on first load and nulled so it never writes.</summary>
    [JsonPropertyName("RingStorage")]
    public List<Observation>? LegacyRing { get; set; }

    // ---- derived (never stored twice)

    /// <summary>Whether the record claims anything at all about a plant here - the rebase gate.
    /// (Display "empty" stays <c>Latest is null</c>: no contents to show.)</summary>
    [JsonIgnore]
    public bool HasTenancy =>
        Latest is not null || PlantedAt is not null || StageSeen.Count > 0 || LastTended is not null;

    /// <summary>Ruling 10 display law: a household hand marks itself, ours says nothing extra -
    /// and never WHOSE, only external.</summary>
    [JsonIgnore]
    public bool LastTendedIsExternal => LastTendedBy is { IsOwn: false };

    [JsonIgnore]
    public ushort SpeciesOfRecord => Latest?.SpeciesIndex ?? PlantedSpecies;

    [JsonIgnore]
    public byte MaxStageSeen => StageSeen.Count == 0 ? (byte)0 : StageSeen.Keys.Max();

    /// <summary>Newest CONTENT evidence (the water clock is not content) - what an incoming
    /// receipt must beat to change anything.</summary>
    [JsonIgnore]
    public DateTimeOffset? NewestEvidenceAt
    {
        get
        {
            DateTimeOffset? newest = Latest?.At;
            if (PlantedAt is { } p && (newest is null || p > newest)) newest = p;
            foreach (var seen in StageSeen.Values)
                if (newest is null || seen.LastSeen > newest) newest = seen.LastSeen;
            return newest;
        }
    }

    /// <summary>Does a (species, stage) reading contradict the current plant? A different known
    /// species, or the same species at an EARLIER stage than this tenancy has already reached
    /// (stages only climb, so a regression is a replant). Species 0 on either side is a shrug,
    /// never a contradiction; stage 0 stays out of the regression test. The ONE copy of this
    /// rule - the live sighting path and the wire path both ask it.</summary>
    public bool Contradicts(ushort species, byte stage)
        => HasTenancy && SpeciesOfRecord != 0 && species != 0
           && (species != SpeciesOfRecord
               || (stage >= 1 && MaxStageSeen >= 1 && stage < MaxStageSeen));

    private static bool IsOwnHand(Hand? by) => by is null || by.Value.IsOwn;

    /// <summary>Wire evidence at or before the fence describes a plant that already left. Own
    /// hands are never fenced: an own act is live, and a fence ahead of it can only be another
    /// install's clock running ahead (pass-3 must-fix #2: stored instants are never altered;
    /// the skew is absorbed here instead).</summary>
    private bool Fenced(DateTimeOffset at, Hand? by)
        => TenancyResetAt is { } r && at <= r && !IsOwnHand(by);

    private bool OlderThanEvidence(DateTimeOffset at)
        => NewestEvidenceAt is { } n && at < n;

    private static bool SpeciesCompatible(ushort a, ushort b) => a == 0 || b == 0 || a == b;

    // ---- writes

    /// <summary>A LIVE map read on this install. Never fenced, never "older" - it is now.</summary>
    public void Sight(DateTimeOffset at, ushort species, byte stage, ObservationSource source)
    {
        Latest = new Observation(at, species, stage, source);
        FoldStage(at, stage);
    }

    /// <summary>Wire evidence about contents (a household Stage or Ripe receipt). THE RULE lives
    /// here. Fenced: nothing. Older than the newest evidence: agrees -> the stage bracket
    /// tightens (ruled 2026-10-14: "update the stage-2 timestamps, don't trigger a replant");
    /// contradicts -> nothing. Newer: a contradiction rebases first (the event comes back for
    /// logging), then the evidence folds and <see cref="Latest"/> moves forward.</summary>
    public AbsorbOutcome Absorb(DateTimeOffset at, ushort species, byte stage, Hand by, out RebaseEvent? rebase)
    {
        rebase = null;
        if (Fenced(at, by))
            return AbsorbOutcome.Fenced;

        if (OlderThanEvidence(at))
        {
            if (!AgreesAsOlder(species, stage, at))
                return AbsorbOutcome.Contradicted;
            FoldStage(at, stage);
            return AbsorbOutcome.FoldedOlder;
        }

        if (Contradicts(species, stage))
            rebase = Rebase(RebaseReason.NewTenancySighted, NewestEvidenceAt ?? at, by, newSpecies: species);

        FoldStage(at, stage);
        if (Latest is null || at > Latest.At)
            Latest = new Observation(at, species != 0 ? species : SpeciesOfRecord, stage, ObservationSource.External);
        return AbsorbOutcome.Applied;
    }

    /// <summary>Whether an older reading is consistent with this tenancy. Monotone in time
    /// (pass-3 should #3): it must name a compatible species, must not predate a known sowing,
    /// no LOWER stage may have been seen after it, and no HIGHER stage before it. "Stage 2 at
    /// 7pm" against "stage 3 at 8pm" passes; "ripe at 5pm" against "stage 3 at 8pm" fails.</summary>
    private bool AgreesAsOlder(ushort species, byte stage, DateTimeOffset at)
    {
        if (!SpeciesCompatible(species, SpeciesOfRecord))
            return false;
        if (PlantedAt is { } p && at < p)
            return false;
        foreach (var (s, seen) in StageSeen)
        {
            if (s < stage && seen.LastSeen > at) return false;
            if (s > stage && seen.FirstSeen < at) return false;
        }
        return true;
    }

    private void FoldStage(DateTimeOffset at, byte stage)
    {
        if (stage is >= 1 and <= 4)
            StageSeen[stage] = StageSeen.TryGetValue(stage, out var s) ? s.Fold(at) : new StageBracket(at, at);
    }

    /// <summary>A watering (Tend or PotWater), either hand. The clock takes max(). Under THE
    /// RULE (pass-3 must-fix #1) an older tend that names a known species different from the
    /// species of record watered the plant that left, and lands nowhere. A tend NEWER than the
    /// evidence lands whatever it names - trust event ordering (Q1, ruled 2026-10-14): it may be
    /// the first word of a replant we have not seen yet, and the next sighting sorts it out.
    /// True = the clock moved.</summary>
    public bool Tend(DateTimeOffset at, Hand by, ushort species)
    {
        if (Fenced(at, by))
            return false;
        if (species != 0 && SpeciesOfRecord != 0 && species != SpeciesOfRecord && OlderThanEvidence(at))
            return false;
        if (LastTended is { } t && at <= t)
            return false;
        LastTended = at;
        LastTendedBy = by;
        LastTendedSpecies = species;
        return true;
    }

    /// <summary>A sowing at a known instant, either hand. Last edit wins (ruled 2026-10-03): a
    /// plant on an occupied row is a new tenancy there. Fenced, or the same instant again (a
    /// replay): nothing. A sowing OLDER than a sowing already known: history only. Older than the
    /// newest evidence: it is this plant's own sowing only if it names the same known species
    /// (pass-3 should #4: unknown is not agreement) - then it anchors and the newer evidence
    /// stays; otherwise history only. Newer than everything: new tenancy at <paramref name="at"/>.
    /// <paramref name="stage"/> is what the sower's map read said (0 = unknown), so an own manual
    /// plant that re-read the map first keeps that reading in <see cref="Latest"/> (pass-3 nit).
    /// Returns the rebase event when the ledger changed; <paramref name="ledgerChanged"/> says so
    /// even when the rebase itself removed nothing.</summary>
    public RebaseEvent? Plant(DateTimeOffset at, ushort species, byte stage, Hand by, out bool ledgerChanged)
    {
        ledgerChanged = false;
        if (Fenced(at, by) || PlantedAt == at)
            return null;
        if (PlantedAt is { } known && at < known)
            return null;
        if (OlderThanEvidence(at) && !(species != 0 && species == SpeciesOfRecord))
            return null;

        var ev = Rebase(RebaseReason.PlantReceipt, cut: at, by);   // removes only evidence at or before the sowing
        PlantedAt = at;
        PlantedBy = by;
        PlantedSpecies = species;
        if (Latest is null || at > Latest.At)
        {
            Latest = new Observation(at, species, stage,
                by.IsOwn ? ObservationSource.PlantReceipt : ObservationSource.External);
            FoldStage(at, stage);
        }
        ledgerChanged = true;
        return ev;
    }

    /// <summary>The reset. Evidence at or before <paramref name="cut"/> leaves with the old plant;
    /// newer evidence survives. A cut OLDER than the fence is refused when a household hand asks
    /// (that plant already left; the receipt is history) - never when this install's own act or
    /// own read asks, because the game wins and a fence ahead of now is skew. A cut equal to the
    /// fence is allowed and refreshes the reason.
    ///
    /// <para>Tends: at or before the cut, gone. On a sighted species change, a tend AFTER the cut
    /// that named the OLD species also goes - it watered the plant that left (the apple->fig
    /// scene, pass-3 must-fix #1). A post-cut tend naming the new species or no species stays
    /// (Q1: keep the watering).</para></summary>
    public RebaseEvent? Rebase(RebaseReason reason, DateTimeOffset cut, Hand? by = null, ushort newSpecies = 0)
    {
        if (TenancyResetAt is { } fence && cut < fence && !IsOwnHand(by))
            return null;

        var removed = false;
        var oldSpecies = SpeciesOfRecord;

        if (PlantedAt is { } p && p <= cut)
        {
            PlantedAt = null; PlantedBy = null; PlantedSpecies = 0;
            removed = true;
        }
        if (Latest is { } l && l.At <= cut)
        {
            Latest = null;
            removed = true;
        }
        foreach (var (stage, seen) in StageSeen.ToList())
        {
            if (seen.LastSeen <= cut)
            {
                StageSeen.Remove(stage);
                removed = true;
            }
            else if (seen.FirstSeen <= cut)
            {
                // Straddle: the bracket began on the old plant and was last seen after the cut.
                // Keep the post-cut point only; the pump logs the trim.
                StageSeen[stage] = seen with { FirstSeen = seen.LastSeen };
                removed = true;
            }
        }
        if (LastTended is { } t)
        {
            var leaves = t <= cut
                || (reason == RebaseReason.NewTenancySighted
                    && oldSpecies != 0 && newSpecies != 0 && newSpecies != oldSpecies
                    && LastTendedSpecies == oldSpecies);
            if (leaves)
            {
                LastTended = null; LastTendedBy = null; LastTendedSpecies = 0;
                removed = true;
            }
        }

        if (TenancyResetAt is null || cut >= TenancyResetAt)
        {
            TenancyResetAt = cut;
            TenancyResetReason = reason;
        }
        return new RebaseEvent(this, reason, cut, by, removed);
    }
}
