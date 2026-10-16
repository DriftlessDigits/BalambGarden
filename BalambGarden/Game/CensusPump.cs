using System;
using System.Collections.Generic;
using System.Linq;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using BalambGarden.Engine.Sync;
using ECommons.DalamudServices;

namespace BalambGarden.Game;

/// <summary>The census heartbeat. Sensors read, receipts route, the ledger learns.
/// Acting IS censusing: every chain completion lands here.</summary>
internal static class CensusPump
{
    /// <summary>Whether the estate under our feet is game-granted (spec: the roster is the
    /// census scope). Refreshed with the tick; false anywhere unrostered, and every ledger
    /// write path below checks it - Balamb can SEE a stranger's garden, it does not KEEP it.</summary>
    internal static bool CoveredHere { get; private set; }

    private const string NotCovered = "not on your teleport list - Balamb doesn't track here";

    private static DateTime nextTickUtc = DateTime.MinValue;
    private static EstateKey? announcedEstate;
    private static bool announcedInside;

    /// <summary>Session-only join evidence: every (slot, species) a receipt has shown at
    /// an unbound patch. One receipt rarely narrows a small estate's shortlist to one key
    /// (08-14 bench), so constraints accumulate until they do. Proposal state, not ledger
    /// state - never persisted, dropped on arrival and on a successful bind.</summary>
    private static readonly Dictionary<(EstateKey Estate, int Ordinal), List<(int Slot, ushort Species)>>
        joinEvidence = [];

    /// <summary>Receipts that completed while their patch was still unbound. The engine
    /// cannot claim without a binding, so the first beds of a run would otherwise be
    /// spent before the evidence they contributed finished the join (08-14 bench round 2:
    /// "1st Bed, 1st Patch" tended but unclaimed). These are REAL receipts held until
    /// identity resolved - deferred delivery, never fabrication. Session-only, replayed
    /// and dropped the moment the estate binds.</summary>
    private static readonly Dictionary<(EstateKey Estate, int Ordinal), List<ReceiptEvent>>
        pendingReceipts = [];

    internal static IReadOnlyDictionary<int, IReadOnlyList<BedReading>> LastOutdoor
        { get; private set; } = new Dictionary<int, IReadOnlyList<BedReading>>();
    internal static IReadOnlyDictionary<int, PotReading> LastIndoor
        { get; private set; } = new Dictionary<int, PotReading>();

    internal static void Tick()
    {
        // ABOVE the 2-second gate and above the estate check, both deliberately: a six-second
        // window judged on a two-second tempo would round a timeout up by a third, and a
        // pending is owed its history line whether or not there is covered ground underfoot.
        SweepPending();

#if DEBUG
        // Deliberately ABOVE the 2-second gate below: the plant-flow dialogs open and
        // close inside a couple of seconds, so a sampler that only ran on census tempo
        // would miss whole addons. The watcher's own per-addon shape hash keeps the log
        // quiet - it dumps once per open, not once per frame.
        if (Chains.PlantFlow.Watching)
            Chains.PlantFlow.Tick();
#endif

        if (DateTime.UtcNow < nextTickUtc)
            return;
        nextTickUtc = DateTime.UtcNow.AddSeconds(2);

        var estate = EstateSensor.Current();
        if (estate is null)
        {
            // No ground under us is not "the last ground was fine": the flag gates every
            // write path below, so it never holds an answer about somewhere we have left.
            announcedEstate = null;
            CoveredHere = false;
            return;
        }

        CoveredHere = RosterSensor.Current().Covers(estate);

        // Wire identity mints where the inputs live: here, standing at the estate. Cheap
        // no-op once every row underfoot carries its entity.
        MintWireEntities(estate);

        // The front door no longer changes the estate key (08-15: one plot, one record), so
        // the inside/outside flip is its own sighting trigger - the two DataMaps are read by
        // different code paths and neither refreshes the other.
        var inside = EstateSensor.IsInside();
        if (announcedEstate == estate)
        {
            if (announcedInside != inside)
            {
                SightNow();
                // The front door has the same settle trap as arrival (below): the DataMap
                // can populate a beat after the flip, and a one-shot read that raced it
                // left an empty LastIndoor standing for the whole visit - every FC pot
                // record read "harvested?" against a snapshot of nothing (2026-08-23).
                // Same cure: don't latch until the side we now stand on has answered,
                // when the ledger says it has something to answer with.
                if ((inside ? LastIndoor.Count : LastOutdoor.Count) == 0
                    && Plugin.Garden.Ledger.Beds.Any(b => b.Estate == estate && b.IsPot == inside))
                    return;
                announcedInside = inside;
            }
            return;
        }

        // First tick at a new estate: visit + sight + (maybe) the one chat line.
        SightNow();
        // The map can populate a beat after zone-in; an empty read means try
        // again next tick rather than announcing a garden we haven't seen. Only the
        // side we are standing on can answer for itself.
        if ((inside ? LastIndoor.Count : LastOutdoor.Count) == 0
            && Plugin.Garden.Ledger.Beds.Any(b => b.Estate == estate && b.IsPot == inside))
            return;

        announcedEstate = estate;
        announcedInside = inside;
        // A new visit starts with no proposal evidence: a garden can be replanted
        // between visits, and stale species would argue against the truth. Held
        // receipts go with it - a receipt that outlived its visit has no identity
        // to resolve to.
        joinEvidence.Clear();
        pendingReceipts.Clear();

        // An unrostered estate still announces above - the tick settles, the map still
        // reads - but nothing about it enters the ledger and nothing nudges.
        if (!CoveredHere)
            return;

        Plugin.Garden.Ledger.UpsertEstate(estate, DateTimeOffset.UtcNow);
        Plugin.Garden.Save();

        if (Plugin.Configuration.NudgeEnabled)
        {
            var rollups = Rollups.ForEstate(
                estate, Plugin.Garden.Census.LedgerBeds, Plugin.Tables,
                Plugin.Garden.Wilt, DateTimeOffset.UtcNow);
            if (Rollups.ArrivalNudge(estate, rollups, Plugin.Configuration.NudgeLabel) is { } line)
                Svc.Chat.Print(line);
        }
    }

    private static DateTime nextMintUtc = DateTime.MinValue;

    /// <summary>The mint sweep's game half (piece 4; the pure half - the stance rule, the
    /// row selection - is <see cref="WireIdentity.MintPending"/>, desk-tested). This keeps
    /// the throttle, contributes the two sensed inputs (HouseId underfoot, gimmick patch
    /// ids), retries the parked foreign receipts after any fresh mint - the moment a parked
    /// fact can finally land - and saves.</summary>
    private static void MintWireEntities(EstateKey estate)
    {
        if (!CoveredHere || DateTime.UtcNow < nextMintUtc)
            return;
        nextMintUtc = DateTime.UtcNow.AddSeconds(10);

        var phrase = Plugin.Configuration.SyncPassphrase;
        if (phrase.Length == 0)
            return;

        var minted = WireIdentity.MintPending(
            Plugin.Garden.Ledger, estate, EstateSensor.IsInside(),
            HouseholdKeys.NamingKey(phrase), EstateSensor.CurrentHouseId(), NearestPatchId);
        if (minted == 0)
            return;

        var rebases = new List<RebaseEvent>();
        var landed = ForeignApply.RetryParked(
            Plugin.Garden.Ledger, DateTimeOffset.UtcNow, Plugin.Garden.History, Plugin.Garden.SaveHistory, rebases);
        Plugin.Garden.Save();
        ReportRebases(rebases);
        Plugin.Log.Information(
            $"[Sync] wire identity minted for {minted} row(s) at {estate.DisplayLabel()}"
            + (landed > 0 ? $" - {landed} parked household receipt(s) applied" : ""));
    }

    /// <summary>Every planting reset, after its save (ruled 2026-10-14: rebase is reasoned and
    /// logged). INF when the reset removed something or witnessed a harvest - a harvest is the
    /// event the history is built around, and the map usually read the bed empty a beat before
    /// the receipt arrived (pass-3 nit) - DEBUG for a bare-row plant, which is not news. Each
    /// one also lands in the trail as the row's identity plus the reset's facts.</summary>
    internal static void ReportRebases(IEnumerable<RebaseEvent> rebases)
    {
        foreach (var ev in rebases)
        {
            var bed = ev.Bed;
            var where = bed.IsPot
                ? $"pot (key {bed.MapKey})"
                : $"patch {bed.PatchOrdinal + 1} bed {bed.BedSlot + 1}";
            var line = $"[Census] planting reset {where} at {bed.Estate.DisplayLabel()}: {ev.Reason}"
                       + $" cut={ev.Cut:u} by={ev.By?.Slug ?? "-"}"
                       + (ev.RemovedAnything ? "" : " (nothing to remove)");
            if (ev.RemovedAnything || ev.Reason == RebaseReason.HarvestWitnessed)
                Plugin.Log.Information(line);
            else
                Plugin.Log.Debug(line);
            if (Plugin.Configuration.TrailEnabled)
                Plugin.Garden.Trail.Append(ev);
        }
    }

    /// <summary>Refreshes the UI-facing map reads WITHOUT posting sightings. The post-run
    /// settle poll needs display truth a dozen times in a few seconds; a full sight each poll
    /// would widen every bracket's LastSeen for nothing and run the reconcile against a map
    /// that may not have settled. Census state is untouched: the receipts already carry the
    /// truth.</summary>
    internal static void RefreshDisplayOnly()
    {
        if (EstateSensor.Current() is null)
            return;
        if (EstateSensor.IsInside())
            LastIndoor = MapSensor.ReadIndoor();
        else
            LastOutdoor = MapSensor.ReadOutdoor();
    }

    /// <summary>Corroboration state for the absent-pot verdict, reset whenever the estate
    /// underfoot changes. The DataMap's settle order relative to the object table is
    /// unreceipted, and a wrong "harvested" verdict Rebase-clears real anchors - so absence
    /// is only believed when the map has answered non-empty this visit (proof it is talking)
    /// AND the key was absent from two consecutive settled reads. The cost is honest: an
    /// estate harvested bare between visits never answers non-empty, so its rows keep the
    /// amber "harvested?" question instead of auto-clearing - which is the true state of
    /// our knowledge there.</summary>
    private static EstateKey? potReadEstate;
    private static HashSet<int>? priorPotKeys;
    private static bool mapAnsweredThisVisit;

    /// <summary>Whether the indoor map has answered at the estate underfoot this visit -
    /// proof it is talking. Since the sensor keeps zeroed entries (2026-08-28), an all-empty
    /// room answers too: "every entry zeroed" is an answer, and only a map that has not
    /// settled yet says nothing at all. The reconcile has always demanded this before believing
    /// absence; the DISPLAY's amber "reads empty now - harvested?" needs the same clause
    /// (field receipt 2026-08-28: a cold zone-in settles objects before the DataMap, and
    /// every recorded pot flashed amber off the pre-settle empty read until the retry
    /// landed - the recording side learned this on 08-23, the display side tonight).</summary>
    internal static bool MapAnsweredThisVisit => mapAnsweredThisVisit;

    internal static void SightNow()
    {
        var estate = EstateSensor.Current();
        if (estate is null)
            return;

        if (potReadEstate != estate)
        {
            potReadEstate = estate;
            priorPotKeys = null;
            mapAnsweredThisVisit = false;
        }

        // The pump refreshes CoveredHere every tick for the estate underfoot, so a
        // non-null estate here can trust it as-is.
        var now = DateTimeOffset.UtcNow;
        var landed = 0;
        if (EstateSensor.IsInside())
        {
            LastIndoor = MapSensor.ReadIndoor();

            // Option A (08-16 ruling): rows the pot-gate has disowned die here, not by
            // seven right-clicks. Only keys SEEN this read and turned away are pruned - a
            // key absent from the map entirely (pot picked up, map not settled) is not
            // evidence against its row.
            var pruned = 0;
            if (CoveredHere && MapSensor.LastPhantomKeys.Count > 0)
            {
                pruned = Plugin.Garden.Census.PrunePhantomPots(estate, MapSensor.LastPhantomKeys);
                if (pruned > 0)
                    Plugin.Log.Information(
                        $"[Census] pot-gate pruned {pruned} phantom pot row(s) at {estate.DisplayLabel()}");
            }

            // One reusable change list for the whole pass; receipts save ONCE after the
            // loop (a 30-pot room must not pay 30 whole-file writes in one sighting). The
            // rebase sink is reported after the ledger save, once per pass.
            var changes = new List<SightingChange>();
            var rebases = new List<RebaseEvent>();
            foreach (var (key, pot) in LastIndoor)
            {
                landed += Plugin.Garden.Census.OnMapSighting(estate, key,
                    [new BedReading(0, pot.SpeciesIndex, pot.Stage, pot.Extra, pot.Occupied)], now,
                    isPot: true, mayRecord: CoveredHere, changes: changes, rebases: rebases);
            }
            if (MintSightingReceipts(estate, isPot: true, changes, now) > 0)
                Plugin.Garden.SaveReceipts();

            // A harvested pot's entry VANISHES from the map (08-15), so the loop above
            // never hears about it - absence from a SETTLED read is the emptying receipt
            // (2026-08-18: Drift's harvested pots kept showing their old contents). The
            // settled-object guard is the same one the drift row trusts - but it is not
            // sufficient alone: whether the DataMap fills before or behind the object
            // table is unreceipted, and on 2026-08-23 a settled-objects-empty-map tick
            // came within one gate of clearing every FC record. So the verdict takes the
            // corroboration above: the map must have answered this visit, and the union
            // of two consecutive settled reads spares any key either one saw.
            if (LastIndoor.Count > 0)
                mapAnsweredThisVisit = true;
            // priorPotKeys non-null = a settled read already happened this visit; the
            // first answering read has nothing to corroborate against and clears nothing.
            if (CoveredHere && ObjectSensor.SawHousingObjects && mapAnsweredThisVisit
                && priorPotKeys is not null)
            {
                var present = LastIndoor.Keys.Union(priorPotKeys).ToList();
                var emptied = Plugin.Garden.Census.ReconcileAbsentPots(estate, present, now, rebases);
                if (emptied > 0)
                    Plugin.Log.Information(
                        $"[Census] {emptied} pot(s) read absent (harvested?) - contents cleared at {estate.DisplayLabel()}");
                landed += emptied;
            }
            if (ObjectSensor.SawHousingObjects)
                priorPotKeys = LastIndoor.Keys.ToHashSet();

            if (CoveredHere && (landed > 0 || pruned > 0))
                Plugin.Garden.Save();
            ReportRebases(rebases);
        }
        else
        {
            LastOutdoor = MapSensor.ReadOutdoor();
            var changes = new List<SightingChange>();
            var rebases = new List<RebaseEvent>();
            foreach (var (key, beds) in LastOutdoor)
                landed += Plugin.Garden.Census.OnMapSighting(estate, key, beds, now,
                    mayRecord: CoveredHere, changes: changes, rebases: rebases);
            if (MintSightingReceipts(estate, isPot: false, changes, now) > 0)
                Plugin.Garden.SaveReceipts();
            if (CoveredHere && landed > 0)
                Plugin.Garden.Save();
            ReportRebases(rebases);
        }
    }

    /// <summary>Sighted changes become history lines (piece 6: observations sync on change,
    /// and the mint discipline lives HERE, in the client). Guards are the wire identity's
    /// inputs, honest-skip like everywhere else: no house id or - for a bed - no gimmick in
    /// scan range means a change stays local-only (the record already holds it; only the
    /// receipt line is foregone, and the next change tries again). PotWilt minting
    /// deliberately absent: b4's meaning is still an open watch and the receiver no-ops the
    /// kind. Returns the minted count; the CALLER saves once per pass - a whole-file write
    /// per map key was the QA pass's worst find.</summary>
    private static int MintSightingReceipts(
        EstateKey estate, bool isPot, List<SightingChange> changes, DateTimeOffset now)
    {
        if (changes.Count == 0)
            return 0;

        var houseId = EstateSensor.CurrentHouseId();
        if (houseId == 0)
        {
            Plugin.Log.Debug("[Sync] sighted change with no house id - stays local-only");
            return 0;
        }

        var minted = 0;
        foreach (var change in changes)
        {
            var patchId = isPot ? (ushort)0 : NearestPatchId(change.Ordinal);
            if (!isPot && patchId == 0)
            {
                Plugin.Log.Debug(
                    $"[Sync] sighted change at patch {change.Ordinal + 1} with no gimmick in "
                    + "scan range - stays local-only");
                continue;
            }

            if (SafeAppend(
                    change.Kind, SyncSource.Sighting, now, estate, isPot,
                    patchOrdinal: isPot ? 0 : change.Ordinal,
                    bedSlot: isPot ? 0 : change.Slot,
                    patchId: patchId,
                    potKey: isPot ? change.Ordinal : -1,
                    data: new SyncData(
                        Species: change.Species == 0 ? null : change.Species,
                        Stage: change.Stage),
                    houseId: houseId))
                minted++;
        }
        return minted;
    }

    /// <summary>The append contract, once (QA pass: three sites had grown their own copy):
    /// a receipt line may refuse (no era minted yet, ruled at ReceiptLog.Append), and the
    /// callers are a chat handler and the frame tick - a lost line is a smaller harm than a
    /// dead tick. Goes through <see cref="GardenService.AppendOwn"/> so a plant or harvest
    /// reaches the garden history in the same breath. The caller owns SaveReceipts, batched
    /// at its own boundary.</summary>
    private static bool SafeAppend(
        SyncKind kind, SyncSource source, DateTimeOffset at, EstateKey estate, bool isPot,
        int patchOrdinal, int bedSlot, ushort patchId, int potKey, SyncData data, ulong houseId)
    {
        try
        {
            Plugin.Garden.AppendOwn(
                kind, source, at, estate, isPot,
                patchOrdinal: patchOrdinal, bedSlot: bedSlot, patchId: patchId, potKey: potKey,
                data: data, houseId: houseId);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Sync] {kind} receipt not written to history: {ex.Message}");
            return false;
        }
    }

    internal static string OnBedReceipt(
        ReceiptVerb verb, string bedHeader, string plantName, BedGimmick actBed,
        byte? stageOverride = null)
    {
        // These two gates write nowhere - not the ledger, not the trail - so the line
        // says exactly that instead of implying a log that never happened.
        var estate = EstateSensor.Current();
        if (estate is null)
            return "no estate identity - not recorded";

        if (!CoveredHere)
            return NotCovered;

        if (ReceiptParser.ParseBedHeader(bedHeader) is not { } parsed)
            return $"unparseable bed header '{bedHeader}' - not recorded";

        SightNow();   // acting is censusing: fresh map before the receipt lands

        var species = Plugin.Tables.SpeciesIndexByName(plantName) ?? 0;
        if (species == 0 && plantName.Length > 0)
            Plugin.Log.Warning($"[Census] unknown plant name '{plantName}' - observing as unknown");

        // Bind if this patch has no key yet: shortlist from object patch-ids x map
        // keys, confirmed by THIS receipt's species at (ordinal, slot).
        var boundHere = false;
        if (Plugin.Garden.Census.BoundKey(estate, parsed.PatchOrdinal) is null && species != 0)
        {
            // This receipt joins the evidence pile for its patch. Newest wins per slot:
            // a replanted bed's old species is history, not a constraint.
            var evidenceKey = (estate, parsed.PatchOrdinal);
            if (!joinEvidence.TryGetValue(evidenceKey, out var evidence))
                joinEvidence[evidenceKey] = evidence = [];
            evidence.RemoveAll(c => c.Slot == parsed.BedSlot);
            evidence.Add((parsed.BedSlot, species));

            var candidates = JoinShortlist.Candidates(ShortlistPatchIds(), LastOutdoor.Keys.ToList());
            var confirmed = JoinConfirm.Confirm(
                candidates, parsed.PatchOrdinal, evidence,
                key => LastOutdoor.GetValueOrDefault(key));
            if (confirmed is not null)
            {
                for (var ordinal = 0; ordinal < confirmed.Count; ordinal++)
                    Plugin.Garden.Census.Bind(estate, ordinal, confirmed[ordinal]);
                Plugin.Log.Information(
                    $"[Census] receipt bound {estate.DisplayLabel()} on {evidence.Count} "
                    + $"constraint(s): keys {string.Join(",", confirmed)}");
                boundHere = true;
                ReplayHeldReceipts(estate);
                foreach (var stale in joinEvidence.Keys.Where(k => k.Estate == estate).ToList())
                    joinEvidence.Remove(stale);
            }
            else
            {
                Plugin.Log.Information(
                    $"[Census] no unique key for patch {parsed.PatchOrdinal + 1} yet - "
                    + $"{candidates.Count} candidate(s), {evidence.Count} constraint(s)");
            }
        }

        var stage = stageOverride
            ?? (Plugin.Garden.Census.BoundKey(estate, parsed.PatchOrdinal) is { } key
                && LastOutdoor.TryGetValue(key, out var beds)
                && parsed.BedSlot < beds.Count
                ? beds[parsed.BedSlot].Stage : (byte)0);

        var receipt = new ReceiptEvent(
            estate, parsed.PatchOrdinal, parsed.BedSlot, verb, species, stage,
            ActInstant(verb, isPot: false));

        // Still no binding: hold this receipt so a later one in the run can bring it
        // home. The current receipt is never held when the bind just landed - Deliver
        // below is its one delivery, and the replay above ran before it.
        if (!boundHere && Plugin.Garden.Census.BoundKey(estate, parsed.PatchOrdinal) is null)
        {
            var pendingKey = (estate, parsed.PatchOrdinal);
            if (!pendingReceipts.TryGetValue(pendingKey, out var held))
                pendingReceipts[pendingKey] = held = [];
            held.Add(receipt);
        }

        var outcome = Deliver(receipt, $"{bedHeader}: {DisplayPlant(plantName)}");

        // The pending was opened at the ACT (ruling 4); this is the delivery reporting in with
        // the full parse and the fresh map behind it - the better witness, so its identity
        // overwrites what the act could only estimate.
        MarkDelivered(
            verb, isPot: false, estate,
            patchOrdinal: parsed.PatchOrdinal, bedSlot: parsed.BedSlot,
            patchId: ActPatchId(actBed, parsed.PatchOrdinal), potKey: -1,
            species: species, stage: stage);

        return outcome;
    }

    /// <summary>Delivers the receipts that completed before the estate had an identity,
    /// oldest first. Straight to the census, never through Deliver: each one already
    /// wrote its trail line when it happened, and one interaction is one trail line.</summary>
    private static void ReplayHeldReceipts(EstateKey estate)
    {
        var held = pendingReceipts
            .Where(kv => kv.Key.Estate == estate)
            .SelectMany(kv => kv.Value)
            .OrderBy(r => r.At)
            .ToList();

        foreach (var stale in pendingReceipts.Keys.Where(k => k.Estate == estate).ToList())
            pendingReceipts.Remove(stale);

        if (held.Count == 0)
            return;

        var rebases = new List<RebaseEvent>();
        foreach (var receipt in held)
            Plugin.Garden.Census.OnReceipt(receipt, rebases);
        ReportRebases(rebases);
        Plugin.Log.Information($"[Census] replayed {held.Count} held receipt(s) after bind");

        // The run log's earlier lines read "not claimed: patch unbound", and they were
        // true when they printed - a feed is history, not live state, so it is never
        // rewritten behind the player's back. The correction gets its own line, at the
        // moment it actually happened.
        Chains.ChainBase.NoteOnActiveRun(
            $"patch identified - {held.Count} earlier bed(s) now claimed "
            + "(the 'patch unbound' lines above were true when they printed)");
    }

    /// <summary>Shortlist input: the nearest patch per ordinal, in ordinal order.
    /// The 40y object sweep sees the neighbours' gardens too (08-14 bench: a foreign
    /// patch 37.9y away also called itself ordinal 0), and a diff pattern computed
    /// across two plots describes no estate at all. Collapsing by distance is legal
    /// because it only shapes the PROPOSAL - the proposer may guess, the binder may
    /// not: a key still binds only when the receipt's species match confirms it.</summary>
    private static List<ushort> ShortlistPatchIds()
        => ObjectSensor.Patches()
            .GroupBy(p => p.Ordinal)
            .Select(g => g.OrderBy(p => p.Distance).First())
            .OrderBy(p => p.Ordinal)
            .Select(p => p.PatchId)
            .ToList();

    /// <summary>
    /// A pot receipt. <paramref name="potKey"/> is the pot the chain identified - normally
    /// read straight off the furniture vector, sometimes named by the chain's own map diff -
    /// and when it is present it decides the identity outright; <paramref name="keySource"/>
    /// is how it was arrived at, and goes in the log so a later reader knows which
    /// instrument spoke. Species uniqueness is the fallback for receipts that carry no key
    /// at all; it still works for a lone plant of its kind and cannot work for twins.
    /// </summary>
    internal static string OnPotReceipt(
        ReceiptVerb verb, string plantName, int? potKey = null, string keySource = "map diff")
    {
        var estate = EstateSensor.Current();
        if (estate is null)
            return "no estate identity - not recorded";

        if (!CoveredHere)
            return NotCovered;

        SightNow();
        var species = Plugin.Tables.SpeciesIndexByName(plantName) ?? 0;

        int key;
        if (potKey is { } identified)
        {
            key = identified;
            // The name-variant gap (a ripe pot's Talk says "Red Sunflowers", the index says
            // "Garden Sunflower") stops a NAME resolving, but it has no say over identity
            // here - the pot is already named. Fall back to what the map itself says is
            // growing there, and record 0 (unknown) rather than guess when it says
            // nothing, which is what an emptied pot correctly says after a harvest.
            if (species == 0)
                species = LastIndoor.TryGetValue(key, out var sighted) ? sighted.SpeciesIndex : (ushort)0;
            Plugin.Log.Information(
                $"[Census] pot bound by {keySource}: key {key} at {estate.DisplayLabel()}");
        }
        else
        {
            if (species == 0)
                return $"pot plant '{plantName}' unknown - cannot bind, not recorded";
            if (PotBind.UniqueSpeciesKey(species, LastIndoor) is not { } unique)
                return $"pot with {plantName} is ambiguous (several or none in map) - unbound";
            key = unique;
        }

        // A pot is its own one-bed patch: ordinal = map key, slot 0. The pot namespace keeps
        // that ordinal off the outdoor patch ordinals now that indoors and outdoors share
        // one estate key (the 08-13 probe saw an outdoor map key of 2).
        Plugin.Garden.Census.Bind(estate, key, key, isPot: true);
        var stage = LastIndoor.TryGetValue(key, out var pot) ? pot.Stage : (byte)0;
        var receipt = new ReceiptEvent(
            estate, key, 0, verb, species, stage, ActInstant(verb, isPot: true), IsPot: true);
        var outcome = Deliver(receipt, $"pot (key {key}): {DisplayPlant(plantName)}");

        // A pot's identity in the history is its furniture key alone (ReceiptLog reads PotKey
        // and nothing else when IsPot) - so ordinal and slot stay 0 here, matching what the
        // chat path already writes, and patch id 0 because a pot has no such thing. On the
        // diff-bind route this delivery is the FIRST moment the key exists at all, so it is
        // also where the pending gets opened (late, and honestly so - ruling 4).
        MarkDelivered(
            verb, isPot: true, estate,
            patchOrdinal: 0, bedSlot: 0, patchId: 0, potKey: key,
            species: species, stage: stage);

        return outcome;
    }

    /// <summary>A receipt born in chat (<see cref="ChatReceipts"/>) - the MANUAL branch of the
    /// receipt lifecycle (ratified 2026-08-23): a confirming line that closed no act of ours
    /// was a human's own hands. Identity is already decided by the time it gets here - the
    /// watcher decoded the object and ran the fail-closed checks - so this does the census and
    /// the history together, nothing else.
    ///
    /// <para>Together is the point on this branch. A chain act's history waits for its close
    /// verdict because the source word is not known yet; an unclaimed line has no such
    /// question - the act is already over and the source is Chat - so there is nothing to defer
    /// and deferring would only invent a window in which the fact could be lost.</para>
    ///
    /// <para>Returns nothing on purpose, unlike every other receipt path: a chat receipt has
    /// no chain feed to report into, so the gates say what happened in the log instead of
    /// handing a line to a run nobody launched.</para></summary>
    /// <param name="knownSpecies">What the caller already proved was acted on, 0 for nothing
    /// - the manual harvest's twice-witnessed item species (ruled 2026-08-28: the map reads a
    /// just-harvested container as ZEROED, so the post-SightNow read below answers 0 for the
    /// one verb whose receipt knew the species best; "if we are confident we did a thing,
    /// show it"). The map still wins when it answers - this only fills its silence.</param>
    /// <param name="yields">What a manual harvest's obtain line said arrived - item id from the
    /// payload, count from the text (Q3, ruled 2026-10-14) - null for every other kind.</param>
    internal static void OnChatReceipt(
        SyncKind kind, bool isPot, int potKey, int patchOrdinal, int bedSlot, ushort patchId,
        ushort knownSpecies = 0, IReadOnlyList<SyncYield>? yields = null)
    {
        var estate = EstateSensor.Current();
        if (estate is null)
        {
            Plugin.Log.Information($"[Census] chat {kind} with no estate identity - not recorded");
            return;
        }

        if (!CoveredHere)
        {
            Plugin.Log.Information($"[Census] chat {kind} here: {NotCovered}");
            return;
        }

        SightNow();   // acting is censusing: fresh map before the receipt lands

        // Best effort off the map read, never a guess: 0 stays 0 locally and becomes a null
        // species on the wire, which honestly says "the sender did not know" rather than
        // asserting a crop that might not be there.
        ushort species = 0;
        byte stage = 0;
        if (isPot)
        {
            if (LastIndoor.TryGetValue(potKey, out var pot))
            {
                species = pot.SpeciesIndex;
                stage = pot.Stage;
            }
        }
        else if (Plugin.Garden.Census.BoundKey(estate, patchOrdinal) is { } key
                 && LastOutdoor.TryGetValue(key, out var beds)
                 && bedSlot < beds.Count)
        {
            species = beds[bedSlot].SpeciesIndex;
            stage = beds[bedSlot].Stage;
        }

        if (species == 0)
            species = knownSpecies;

        var now = DateTimeOffset.UtcNow;

        // A pot is its own one-bed patch (ordinal = map key, slot 0), the same shape
        // OnPotReceipt uses - the census keeps one identity convention whoever minted it.
        var receipt = new ReceiptEvent(
            estate,
            isPot ? potKey : patchOrdinal,
            isPot ? 0 : bedSlot,
            VerbFor(kind), species, stage, now, IsPot: isPot);

        if (Plugin.Configuration.TrailEnabled)
            Plugin.Garden.Trail.Append(receipt);

        // A fertilize lands nothing on the record by design (a feed is not a reading), but it
        // still proves the row: we stood there and touched it. A manual TEND sets the water
        // clock through the same engine path a chain tend uses, and that is the POINT of
        // widening this entry: watering done by hand finally lands instead of leaving a gap
        // the pacing math reads as neglect. A manual HARVEST is the emptying witness; a manual
        // PLANT a new planting at this instant (own hands are never fenced).
        var rebases = new List<RebaseEvent>();
        var bed = Plugin.Garden.Census.OnReceipt(receipt, rebases);
        Plugin.Garden.Save();
        ReportRebases(rebases);

        // The receipt log (the outbox) and, for a plant or harvest, the garden history - one
        // seam (GardenService.AppendOwn). SaveReceipts refreshes config's memory of the counter.
        Plugin.Garden.AppendOwn(
            kind, SyncSource.Chat, now, estate, isPot,
            patchOrdinal: patchOrdinal, bedSlot: bedSlot, patchId: patchId, potKey: potKey,
            data: new SyncData(Species: species == 0 ? null : species, Yields: yields),
            houseId: EstateSensor.CurrentHouseId());
        Plugin.Garden.SaveReceipts();

        var where = isPot
            ? $"pot (key {potKey})"
            : $"patch {patchOrdinal + 1} bed {bedSlot + 1}";
        Plugin.Log.Information(
            $"[Census] {kind} receipted from chat: {where} at {estate.DisplayLabel()}"
            + $" - {(species == 0 ? "species unknown" : Plugin.Tables.SpeciesName(species))}"
            + (bed is null ? " (not claimed: patch not identified yet)" : ""));
    }

    /// <summary>The two vocabularies, joined in one place. The census verb and the wire kind
    /// mirror each other 1:1 by ruling (PotWater is its own kind on both sides), so this is a
    /// translation and never a judgement - and keeping it here means the lifecycle's open and
    /// close paths cannot drift into disagreeing about what an act was.</summary>
    private static ReceiptVerb VerbFor(SyncKind kind) => kind switch
    {
        SyncKind.Fertilize => ReceiptVerb.Fertilize,
        SyncKind.Tend => ReceiptVerb.Tend,
        SyncKind.PotWater => ReceiptVerb.PotWater,
        SyncKind.Plant => ReceiptVerb.Plant,
        SyncKind.Harvest => ReceiptVerb.Harvest,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an action kind"),
    };

    /// <summary>The reverse direction, for the acts chains report. The observation kinds
    /// (Stage/Ripe/PotWilt) have no verb because they are not acts - a sighting is something we
    /// noticed, and only acts open pendings.</summary>
    private static SyncKind KindFor(ReceiptVerb verb) => verb switch
    {
        ReceiptVerb.Fertilize => SyncKind.Fertilize,
        ReceiptVerb.Tend => SyncKind.Tend,
        ReceiptVerb.PotWater => SyncKind.PotWater,
        ReceiptVerb.Plant => SyncKind.Plant,
        ReceiptVerb.Harvest => SyncKind.Harvest,
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "Not an action verb"),
    };

    /// <summary>The instant a chain receipt carries. For a PLANT it is the act's own moment -
    /// the pending opened when the menu committed (pass-2 #15) - so the sowing anchors where it
    /// happened, before the delivery's own map read, and that read survives as the first
    /// sighting of the new plant. Guarded to the same kind and container (pass-3 nit: the pot
    /// diff-bind route can see a stale pending). Every other verb is stamped at delivery, as
    /// before. KNOWN RACE, pre-existing and unverified: a map that still reads the bed EMPTY on
    /// the delivery sight after a sow would ReadsEmpty-rebase the fresh anchor - named in the
    /// design's blind spots, awaiting the bed sow-line capture.</summary>
    private static DateTimeOffset ActInstant(ReceiptVerb verb, bool isPot)
        => verb == ReceiptVerb.Plant && openPending is { Kind: SyncKind.Plant } p && p.IsPot == isPot
            ? p.OpenedAt
            : DateTimeOffset.UtcNow;

    // ---- THE PENDING DESK ----------------------------------------------------------------
    // The two-phase receipt (ratified 2026-08-23, amended the same night by rulings 4-6). A
    // chain act OPENS a pending here AT THE MOMENT IT COMMITS; the game's confirming chat line
    // CLOSES it and the history is appended as Chain. Nothing closes it, and the sweep decides
    // between an honest ChainUnconfirmed line and a silent cancel (ruling 6).
    //
    // RULING 4 - THE PENDING OPENS AT THE ACT. It used to open at the DELIVERY, which for the
    // pot paths runs after a map settle: at the 17:28 field lap the game's line beat the
    // delivery there, found nothing open, ran the manual branch, and then the delivery opened a
    // pending that timed out. One act, two history entries. The chains' closure-build points
    // are the true commit moments and identity is already in hand at them, so open-before-close
    // is restored for the whole class. The pot's diff-bind fallback is the one exception it
    // cannot cover - the key does not exist until the map has changed - and it degrades
    // honestly to a late open marked delivered, which times out as chain-unconfirmed.
    //
    // ONLY THE HISTORY WAITS. Ledger state, feed strings and binding all land immediately when
    // the chain acts, exactly as before - the desk never sits between a chain and its census.
    // The receipt LOG waits because it is append-only: the source word has to be right the
    // first time, and it is not known until the window closes.
    //
    // Session-only, single-slot, and deliberately not persisted: a pending is a question about
    // the last six seconds, and a restart is an answer of "no".

    private static PendingReceipt? openPending;

    /// <summary>The last close a chat line actually made, for the reverse race
    /// (<see cref="ReceiptLifecycle.DeliveryAlreadyClosed"/>). One slot, session-only, never
    /// persisted - same reasoning as the pending itself.</summary>
    private static ReceiptLifecycle.RecentClose? lastClose;

    /// <summary>The kind of act currently waiting on its confirming line, or null when none is.
    /// Read by <see cref="ChatReceipts"/>'s unmatched-line logger, which is only interesting
    /// while something is open.</summary>
    internal static SyncKind? OpenPendingKind => openPending?.Kind;

    /// <summary>Opens the wait for a BED act's confirming line, at the moment the chain commits
    /// to it (ruling 4). Called from the chains' own act steps, BEFORE the census delivery - the
    /// menu entry is already selected, so this is genuinely the act, and the pending has to be
    /// standing before the game's line can land or the manual branch will claim it.
    ///
    /// <para>Everything here is BEST EFFORT on purpose. The delivery does the full parse, the
    /// fresh sight and the bind, and it overwrites all of this through
    /// <see cref="MarkDelivered"/> - so the act only has to bank enough identity to be a real
    /// receipt if the delivery never arrives, and a stage of 0 (honest unknown) is a fair price
    /// for not running a map read inside a dialogue step.</para>
    ///
    /// <para>NO ESTATE, NO PENDING. Both gates the delivery paths refuse on are checked here
    /// too: opening a pending on ground the census does not keep would promise a history line
    /// that every write path below would then refuse to make.</para></summary>
    internal static void OpenAtAct(
        ReceiptVerb verb, string bedHeader, string plantName, BedGimmick actBed)
    {
        if (ActGround(verb) is not { } estate)
            return;

        if (ReceiptParser.ParseBedHeader(bedHeader) is not { } parsed)
        {
            Plugin.Log.Debug(
                $"[Census] {verb} act on unparseable bed header '{bedHeader}' - no pending opened");
            return;
        }

        // The map read we already have, never a fresh one: a sight inside a dialogue step would
        // cost the run its tempo, and the delivery is a beat away with a real one.
        var stage = Plugin.Garden.Census.BoundKey(estate, parsed.PatchOrdinal) is { } key
            && LastOutdoor.TryGetValue(key, out var beds)
            && parsed.BedSlot < beds.Count
                ? beds[parsed.BedSlot].Stage : (byte)0;

        OpenPending(
            verb, estate, isPot: false,
            patchOrdinal: parsed.PatchOrdinal, bedSlot: parsed.BedSlot,
            patchId: ActPatchId(actBed, parsed.PatchOrdinal), potKey: -1,
            species: Plugin.Tables.SpeciesIndexByName(plantName) ?? 0, stage: stage);
    }

    /// <summary>The patch id for a bed receipt: the acted object's own gimmick bytes - the
    /// identity the chain held in hand at the interact, the same decode the manual chat path
    /// binds by - taken when it agrees with the header the receipt is filed under. The
    /// proximity scan is only the fallback for a disagreement, because a gimmick whose ordinal
    /// contradicts the menu header belongs to some OTHER patch and its patch id would be a
    /// wrong answer with a confident face. Disagreement is a finding (it would mean the menu
    /// header and the interacted object can diverge), so it is warned with both numbers.</summary>
    private static ushort ActPatchId(BedGimmick actBed, int patchOrdinal)
    {
        if (actBed.PatchOrdinal == patchOrdinal)
            return actBed.PatchId;

        Plugin.Log.Warning(
            $"[Census] BED IDENTITY MISMATCH: menu header says patch {patchOrdinal + 1}, the "
            + $"acted object's gimmick says patch {actBed.PatchOrdinal + 1} (id {actBed.PatchId})"
            + " - filed under the header, patch id from the nearest-object scan.");
        return NearestPatchId(patchOrdinal);
    }

    /// <summary>The pot form of <see cref="OpenAtAct(ReceiptVerb, string, string)"/>, for the
    /// acts whose pot is already named by the furniture read. The diff-bind route has no key at
    /// its commit moment and deliberately does NOT call this - see the note at that site.</summary>
    internal static void OpenAtAct(ReceiptVerb verb, int potKey, string plantName)
    {
        if (ActGround(verb) is not { } estate)
            return;

        // The name-variant gap (a ripe pot's Talk says "Red Sunflowers") loses a species here
        // exactly as it does at the delivery, and the cure is the same: ask the map what is
        // actually growing in the pot we already know the key of.
        var species = Plugin.Tables.SpeciesIndexByName(plantName) ?? 0;
        PotReading? sighted = LastIndoor.TryGetValue(potKey, out var pot) ? pot : null;
        if (species == 0 && sighted is not null)
            species = sighted.SpeciesIndex;

        OpenPending(
            verb, estate, isPot: true,
            patchOrdinal: 0, bedSlot: 0, patchId: 0, potKey: potKey,
            species: species, stage: sighted?.Stage ?? (byte)0);
    }

    /// <summary>The two gates every act-time open shares: the estate underfoot, and whether the
    /// census keeps it. Silent-ish by design - a debug line, not a warning - because standing
    /// somewhere unrostered is a normal thing to do and the delivery will say so out loud in the
    /// run feed when it refuses.</summary>
    private static EstateKey? ActGround(ReceiptVerb verb)
    {
        var estate = EstateSensor.Current();
        if (estate is null)
        {
            Plugin.Log.Debug($"[Census] {verb} act with no estate identity - no pending opened");
            return null;
        }

        if (!CoveredHere)
        {
            Plugin.Log.Debug($"[Census] {verb} act here: {NotCovered} - no pending opened");
            return null;
        }

        return estate;
    }

    /// <summary>Opens the wait for a chain act's confirming line, with the identity the act had
    /// in hand - the chain knows exactly which bed or pot it touched, and that knowledge has to
    /// be banked now because the closing line credits no object at all.
    ///
    /// <para>ONE IN FLIGHT, EVER. Chains are sequential, so a second open means the first act's
    /// line never came: the old pending is retired before the new one opens. That is what makes
    /// overlap impossible even at the pacing boundary, where a slow line and the next act would
    /// otherwise both be live and the wrong one could be closed. What retiring MEANS is ruling
    /// 6's question, not this one's - a delivered act writes its chain-unconfirmed line, an
    /// undelivered one cancels.</para></summary>
    private static void OpenPending(
        ReceiptVerb verb, EstateKey estate, bool isPot,
        int patchOrdinal, int bedSlot, ushort patchId, int potKey, ushort species, byte stage,
        bool delivered = false)
    {
        if (openPending is { } stale)
        {
            openPending = null;
            Retire(stale,
                $"[Census] {stale.Kind} receipt never confirmed by chat before the next act - "
                + "recording as chain-unconfirmed");
        }

        // The raw HouseId banks at the ACT like every other identity here (option A, ruled
        // 2026-08-29): the wire canonical needs the game's own 8 bytes, and by close time -
        // or a chain-unconfirmed timeout - the player may be standing somewhere else.
        openPending = new PendingReceipt(
            KindFor(verb), isPot, DateTimeOffset.UtcNow, estate,
            patchOrdinal, bedSlot, patchId, potKey, species, stage, delivered,
            HouseId: EstateSensor.CurrentHouseId());
    }

    /// <summary>The chain's own receipt reporting in: this act really did complete, and here is
    /// the identity the full parse and the fresh sight resolved it to.
    ///
    /// <para>THE DELIVERY IS THE BETTER WITNESS. The act opened the pending from whatever it had
    /// to hand mid-dialogue; the delivery has just re-read the map, run the parse and possibly
    /// bound the patch. So its identity overwrites, rather than being discarded as a duplicate
    /// of something already banked.</para>
    ///
    /// <para>NO PENDING OPEN is two different situations and they must not be confused. If a
    /// matching line closed one moments ago, this act is ALREADY IN HISTORY and there is nothing
    /// to do - the reverse race, ruled at
    /// <see cref="ReceiptLifecycle.DeliveryAlreadyClosed"/>. Otherwise this is a late-identity
    /// path (the pot diff-bind, or an act whose pending the sweep already retired), and the
    /// pending opens HERE, already delivered: it will wait for a line that has most likely
    /// already been dropped, and time out honestly as chain-unconfirmed.</para></summary>
    private static void MarkDelivered(
        ReceiptVerb verb, bool isPot, EstateKey estate,
        int patchOrdinal, int bedSlot, ushort patchId, int potKey, ushort species, byte stage)
    {
        var kind = KindFor(verb);

        if (openPending is { } pending && pending.Kind == kind && pending.IsPot == isPot)
        {
            openPending = pending with
            {
                Delivered = true,
                Estate = estate,
                PatchOrdinal = patchOrdinal,
                BedSlot = bedSlot,
                PatchId = patchId,
                PotKey = potKey,
                Species = species,
                Stage = stage,
            };
            return;
        }

        if (ReceiptLifecycle.DeliveryAlreadyClosed(kind, isPot, lastClose, DateTimeOffset.UtcNow))
        {
            Plugin.Log.Debug(
                $"[Census] {kind} delivery arrived after its own confirming line closed the act - "
                + "already in history, no second pending opened");
            return;
        }

        Plugin.Log.Debug(
            $"[Census] {kind} delivery with no pending open - opening late (identity was not "
            + "knowable at the act)");
        OpenPending(
            verb, estate, isPot, patchOrdinal, bedSlot, patchId, potKey, species, stage,
            delivered: true);
    }

    /// <summary>Offers a classified chat line to the open act. True means the line was that
    /// act's confirmation and the caller is done with it; false means nothing of ours was
    /// waiting on it, so it is somebody's own hands and belongs on the manual branch.
    ///
    /// <para>A non-match NEVER clears the pending: our act is still waiting for its own line,
    /// and a stranger's fertilize landing in earshot must not bury it.</para>
    ///
    /// <para><paramref name="itemSpecies"/> is the obtain lane's extra witness (capture
    /// 2026-08-26): what the line's ItemPayload resolved to, 0 for unknown and for every
    /// 57-catalog verdict. <see cref="ReceiptLifecycle.Matches"/> owns the rule - a resolvable
    /// contradiction refuses the close and the pending times out honestly.</para></summary>
    /// <param name="yield">The obtain line's item and count (Q3, ruled 2026-10-14), banked on
    /// the pending at close so the history line carries it; null for every other verdict.</param>
    internal static bool TryCloseChain(ChatVerdict verdict, ushort itemSpecies = 0, SyncYield? yield = null)
    {
        if (openPending is not { } pending)
            return false;
        if (!ReceiptLifecycle.Matches(pending, verdict, DateTimeOffset.UtcNow, itemSpecies))
            return false;

        openPending = null;
        if (yield is { } y)
            pending = pending with { Yields = [y] };
        // Banked for the reverse race: a delivery still on its way to this same act must be
        // able to tell "already in history" from "never opened" (ruling 4's honest degrade).
        lastClose = new ReceiptLifecycle.RecentClose(pending.Kind, pending.IsPot, DateTimeOffset.UtcNow);
        AppendHistory(pending, SyncSource.Chain);
        Plugin.Log.Information($"[Census] {pending.Kind} receipt confirmed by chat");
        return true;
    }

    /// <summary>The timeout sweep. Runs on every tick ABOVE the census gate and without asking
    /// where we are: a pending is a promise to ANSWER for an act, and standing somewhere the
    /// census does not cover - or nowhere at all - is not a reason to drop it.
    ///
    /// <para>The answer itself is <see cref="Retire"/>'s to give, because since ruling 4 a
    /// pending is no longer proof the act happened - it is proof we tried.</para></summary>
    private static void SweepPending()
    {
        if (openPending is not { } pending || !ReceiptLifecycle.Expired(pending, DateTimeOffset.UtcNow))
            return;

        openPending = null;
        Retire(pending,
            $"[Census] {pending.Kind} receipt timed out with no confirming chat line after "
            + $"{ReceiptLifecycle.CloseWindow.TotalSeconds:0}s - recording as chain-unconfirmed");
    }

    /// <summary>What becomes of a pending nobody closed - the timeout and the force-close at the
    /// next act both end here, because they are the same question asked at different moments.
    /// Ruling 6 answers it: <paramref name="warning"/> is only ever spoken for an act that
    /// really happened.
    ///
    /// <para>LOUDLY for a delivered act, by the original ruling: every such timeout is logged at
    /// warning so a systematic miss - a line we have not learned to read, a verb whose words
    /// changed in a patch - surfaces as a pile in the log instead of hiding inside an
    /// honest-looking source word. A CANCEL is not that: nothing was missed, the act simply did
    /// not happen, so it goes in at information and writes nothing at all.</para></summary>
    private static void Retire(PendingReceipt pending, string warning)
    {
        if (ReceiptLifecycle.OnTimeout(pending) == ReceiptLifecycle.TimeoutOutcome.Cancel)
        {
            Plugin.Log.Information(
                $"[Census] {pending.Kind} pending cancelled - never delivered and no confirming "
                + "line (act presumed refused or failed)");
            return;
        }

        Plugin.Log.Warning(warning);
        AppendHistory(pending, SyncSource.ChainUnconfirmed);
    }

    /// <summary>The deferred half: the pending becomes one line of append-only history under
    /// the source word the verdict earned it.
    ///
    /// <para>Guarded because the callers are a chat handler and the frame tick, and neither may
    /// be the thing that breaks the plugin - an append can refuse (no era minted yet, ruled at
    /// <see cref="ReceiptLog.Append"/>), and a lost history line is a smaller harm than a dead
    /// tick.</para></summary>
    private static void AppendHistory(PendingReceipt pending, SyncSource source)
    {
        if (SafeAppend(
                pending.Kind, source, pending.OpenedAt, pending.Estate, pending.IsPot,
                patchOrdinal: pending.PatchOrdinal, bedSlot: pending.BedSlot,
                patchId: pending.PatchId, potKey: pending.PotKey,
                data: new SyncData(
                    Species: pending.Species == 0 ? null : pending.Species,
                    Stage: pending.Stage == 0 ? null : pending.Stage,
                    Yields: pending.Yields),
                houseId: pending.HouseId))
            Plugin.Garden.SaveReceipts();
    }

    /// <summary>FALLBACK ONLY (ruled 2026-08-23): the primary patch id is the acted object's
    /// own gimmick via <see cref="ActPatchId"/> - the chain holds it at the interact - and
    /// this proximity scan answers only when that gimmick contradicts the header it is filed
    /// under. Nearest object for the ordinal, from the same shortlist source the binder
    /// proposes with (the 40y sweep sees the neighbours' patches too).
    ///
    /// <para>0 when no patch object was seen - HONEST UNKNOWN, not a real id, exactly as
    /// <see cref="LoggedReceipt.PatchId"/> already carries it. The wire entity name is minted
    /// FROM the patch id, so the send path refuses any entry still carrying 0 (fail closed,
    /// one guard, no resolution machinery) - with the gimmick as the primary source that case
    /// should never arise on new receipts.</para></summary>
    private static ushort NearestPatchId(int patchOrdinal)
        => ObjectSensor.Patches()
            .Where(p => p.Ordinal == patchOrdinal)
            .OrderBy(p => p.Distance)
            .Select(p => p.PatchId)
            .FirstOrDefault();

    internal static string OnRipeSkip(string bedHeader, string plantName)
    {
        // A ripe bed offers no tend - the skip itself is a stage-4 sighting (spec).
        var estate = EstateSensor.Current();
        if (estate is null || ReceiptParser.ParseBedHeader(bedHeader) is not { } parsed)
            return $"{bedHeader}: skipped (ripe?) - not recorded";

        if (!CoveredHere)
            return NotCovered;

        SightNow();   // the sighting may have just created the row this skip records against

        var species = Plugin.Tables.SpeciesIndexByName(plantName) ?? 0;
        var bed = Plugin.Garden.Census.LedgerBeds.FirstOrDefault(b =>
            b.Estate == estate && b.PatchOrdinal == parsed.PatchOrdinal
            && b.BedSlot == parsed.BedSlot && !b.IsPot);
        if (bed is null)
            return $"{bedHeader}: ripe - patch not identified yet (tend a growing bed here once and the whole patch joins)";

        bed.Sight(
            DateTimeOffset.UtcNow,
            species != 0 ? species : bed.Latest?.SpeciesIndex ?? 0,
            GardenStage.Ripe, ObservationSource.RipeSkip);
        Plugin.Garden.Save();
        return $"{bedHeader}: {DisplayPlant(plantName)} - ripe, skipped (recorded)";
    }

    private static string Deliver(ReceiptEvent receipt, string label)
    {
        if (Plugin.Configuration.TrailEnabled)
            Plugin.Garden.Trail.Append(receipt);

        var rebases = new List<RebaseEvent>();
        var bed = Plugin.Garden.Census.OnReceipt(receipt, rebases);
        Plugin.Garden.Save();
        ReportRebases(rebases);
        return bed is null
            ? $"{label} - done (not recorded: patch not identified yet)"
            : $"{label} - done";
    }

    private static string DisplayPlant(string plantName)
        => plantName.Length > 0 ? plantName : "?";
}
