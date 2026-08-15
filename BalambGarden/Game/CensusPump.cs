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
    private static DateTime nextTickUtc = DateTime.MinValue;
    private static EstateKey? announcedEstate;

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
        if (DateTime.UtcNow < nextTickUtc)
            return;
        nextTickUtc = DateTime.UtcNow.AddSeconds(2);

        var estate = EstateSensor.Current();
        if (estate is null)
        {
            announcedEstate = null;
            return;
        }

        // First tick at a new estate: visit + sight + (maybe) the one chat line.
        if (announcedEstate != estate)
        {
            SightNow();
            // The map can populate a beat after zone-in; an empty read means try
            // again next tick rather than announcing a garden we haven't seen.
            if (LastOutdoor.Count == 0 && LastIndoor.Count == 0
                && Plugin.Garden.Ledger.Beds.Any(b => b.Estate == estate))
                return;

            announcedEstate = estate;
            // A new visit starts with no proposal evidence: a garden can be replanted
            // between visits, and stale species would argue against the truth. Held
            // receipts go with it - a receipt that outlived its visit has no identity
            // to resolve to.
            joinEvidence.Clear();
            pendingReceipts.Clear();
            Plugin.Garden.Ledger.UpsertEstate(estate, DateTimeOffset.UtcNow);
            Plugin.Garden.Save();

            if (Plugin.Configuration.NudgeEnabled)
            {
                var rollups = Rollups.ForEstate(
                    estate, Plugin.Garden.Census.LedgerBeds, Plugin.Tables,
                    Plugin.Garden.Wilt, DateTimeOffset.UtcNow);
                if (Rollups.ArrivalNudge(estate, rollups) is { } line)
                    Svc.Chat.Print(line);
            }
        }
    }

    internal static void SightNow()
    {
        var estate = EstateSensor.Current();
        if (estate is null)
            return;

        var now = DateTimeOffset.UtcNow;
        if (EstateSensor.IsInside())
        {
            LastIndoor = MapSensor.ReadIndoor();
            foreach (var (key, pot) in LastIndoor)
            {
                Plugin.Garden.Census.OnMapSighting(estate, key,
                    [new BedReading(0, pot.SpeciesIndex, pot.Stage, pot.Extra, pot.Occupied)], now);
            }
        }
        else
        {
            LastOutdoor = MapSensor.ReadOutdoor();
            foreach (var (key, beds) in LastOutdoor)
                Plugin.Garden.Census.OnMapSighting(estate, key, beds, now);
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
                    $"[Census] receipt bound {estate.DisplayWardPlot()} on {evidence.Count} "
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

    internal static string OnPotReceipt(ReceiptVerb verb, string plantName)
    {
        var estate = EstateSensor.Current();
        if (estate is null)
            return "no estate identity - not recorded";

        SightNow();
        var species = Plugin.Tables.SpeciesIndexByName(plantName) ?? 0;
        if (species == 0)
            return $"pot plant '{plantName}' unknown - cannot bind, not recorded";

        var key = PotBind.UniqueSpeciesKey(species, LastIndoor);
        if (key is null)
            return $"pot with {plantName} is ambiguous (several or none in map) - unbound";

        // A pot is its own one-bed patch: ordinal = map key, slot 0.
        Plugin.Garden.Census.Bind(estate, key.Value, key.Value);
        var stage = LastIndoor.TryGetValue(key.Value, out var pot) ? pot.Stage : (byte)0;
        var receipt = new ReceiptEvent(
            estate, key.Value, 0, verb, species, stage, DateTimeOffset.UtcNow, IsPot: true);
        return Deliver(receipt, $"pot (key {key}): {DisplayPlant(plantName)}");
    }

    internal static string OnRipeSkip(string bedHeader, string plantName)
    {
        // A ripe bed offers no tend - the skip itself is a stage-4 sighting (spec).
        var estate = EstateSensor.Current();
        if (estate is null || ReceiptParser.ParseBedHeader(bedHeader) is not { } parsed)
            return $"{bedHeader}: skipped (ripe?) - not recorded";

        var species = Plugin.Tables.SpeciesIndexByName(plantName) ?? 0;
        var bed = Plugin.Garden.Census.LedgerBeds.FirstOrDefault(b =>
            b.Estate == estate && b.PatchOrdinal == parsed.PatchOrdinal
            && b.BedSlot == parsed.BedSlot && !b.IsPot);
        if (bed is null)
            return $"{bedHeader}: skipped (ripe, unclaimed - tend won't claim a bed it can't touch)";

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
            ? $"{label} - done (not claimed: {(Plugin.Configuration.ClaimOnAction ? "patch unbound" : "claim-as-I-go off")})"
            : $"{label} - done";
    }

    private static string DisplayPlant(string plantName)
        => plantName.Length > 0 ? plantName : "?";
}
