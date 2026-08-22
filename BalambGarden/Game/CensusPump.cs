using System;
using System.Collections.Generic;
using System.Linq;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
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

        // The front door no longer changes the estate key (08-15: one plot, one record), so
        // the inside/outside flip is its own sighting trigger - the two DataMaps are read by
        // different code paths and neither refreshes the other.
        var inside = EstateSensor.IsInside();
        if (announcedEstate == estate)
        {
            if (announcedInside != inside)
            {
                announcedInside = inside;
                SightNow();
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

    /// <summary>Refreshes the UI-facing map reads WITHOUT posting observations. The
    /// post-run settle poll needs display truth a dozen times in a few seconds, and a
    /// full sight each poll would flush every bed's eight-slot observation ring and
    /// take the run's own receipt provenance with it (the PotChain snapshot learned
    /// this first). Census state is untouched: the receipts already carry the truth.</summary>
    internal static void RefreshDisplayOnly()
    {
        if (EstateSensor.Current() is null)
            return;
        if (EstateSensor.IsInside())
            LastIndoor = MapSensor.ReadIndoor();
        else
            LastOutdoor = MapSensor.ReadOutdoor();
    }

    internal static void SightNow()
    {
        var estate = EstateSensor.Current();
        if (estate is null)
            return;

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

            foreach (var (key, pot) in LastIndoor)
            {
                landed += Plugin.Garden.Census.OnMapSighting(estate, key,
                    [new BedReading(0, pot.SpeciesIndex, pot.Stage, pot.Extra, pot.Occupied)], now,
                    isPot: true, mayRecord: CoveredHere);
            }

            // A harvested pot's entry VANISHES from the map (08-15), so the loop above
            // never hears about it - absence from a SETTLED read is the emptying receipt
            // (2026-08-18: Sam's harvested pots kept showing their old contents). The
            // settled-object guard is the same one the drift row trusts: an unsettled
            // world answers nothing, never "empty".
            if (CoveredHere && ObjectSensor.SawHousingObjects)
            {
                var emptied = Plugin.Garden.Census.ReconcileAbsentPots(estate, LastIndoor.Keys.ToList());
                if (emptied > 0)
                    Plugin.Log.Information(
                        $"[Census] {emptied} pot(s) read absent (harvested?) - contents cleared at {estate.DisplayLabel()}");
                landed += emptied;
            }

            if (CoveredHere && (landed > 0 || pruned > 0))
                Plugin.Garden.Save();
        }
        else
        {
            LastOutdoor = MapSensor.ReadOutdoor();
            foreach (var (key, beds) in LastOutdoor)
                landed += Plugin.Garden.Census.OnMapSighting(estate, key, beds, now, mayRecord: CoveredHere);
            if (CoveredHere && landed > 0)
                Plugin.Garden.Save();
        }
    }

    internal static string OnBedReceipt(
        ReceiptVerb verb, string bedHeader, string plantName, byte? stageOverride = null)
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
            DateTimeOffset.UtcNow);

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

        return Deliver(receipt, $"{bedHeader}: {DisplayPlant(plantName)}");
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

        foreach (var receipt in held)
            Plugin.Garden.Census.OnReceipt(receipt);
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
            estate, key, 0, verb, species, stage, DateTimeOffset.UtcNow, IsPot: true);
        return Deliver(receipt, $"pot (key {key}): {DisplayPlant(plantName)}");
    }

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

        bed.Observe(new Observation(
            DateTimeOffset.UtcNow,
            species != 0 ? species : bed.Latest?.SpeciesIndex ?? 0,
            4, ObservationSource.RipeSkip));
        Plugin.Garden.Save();
        return $"{bedHeader}: {DisplayPlant(plantName)} - ripe, skipped (recorded)";
    }

    private static string Deliver(ReceiptEvent receipt, string label)
    {
        if (Plugin.Configuration.TrailEnabled)
            Plugin.Garden.Trail.Append(receipt);

        var bed = Plugin.Garden.Census.OnReceipt(receipt);
        Plugin.Garden.Save();
        return bed is null
            ? $"{label} - done (not recorded: patch not identified yet)"
            : $"{label} - done";
    }

    private static string DisplayPlant(string plantName)
        => plantName.Length > 0 ? plantName : "?";
}
