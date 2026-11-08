using System.Collections.Generic;
using System.Linq;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;
using BalambGarden.Game;

namespace BalambGarden.Chains;

/// <summary>
/// Every verb a press starts, in one place: which chain runs, on what, and with which plan.
/// A screen draws a button and calls one method here; nothing here draws. A verb either
/// starts its chain and tells the run log (<see cref="Plugin.Launched"/>), or starts nothing
/// and hands back the refusal sentence for the screen to show beside the button that was
/// pressed - a refusal never starts a run, so it has no chain report to live in.
///
/// <para>The rules for what a button offers live here too. The sweep lists
/// (<see cref="SweepBeds"/>, <see cref="WaterablePots"/>): a button reads one to say what it
/// will claim, and the press acts on exactly that list, so the label and the run can never
/// disagree. The Replant ripe gates (<see cref="AnyRipeInPatch"/>,
/// <see cref="AnyRipePotInReach"/>): whether the button exists at all, from the ledger's last
/// reading; the press re-reads the map before it plans.</para>
/// </summary>
internal sealed class GardenVerbs(Plugin plugin)
{
    // ------------------------------------------------------------------ beds

    /// <summary>The beds of this patch a SWEEP should visit - <see cref="TendSweep"/> against
    /// the live map: confirmed ripe/empty beds are left out (the game refuses those tends;
    /// the pot Water All precedent, ruled onto beds 2026-08-28), and a bed the map has not
    /// answered for is INCLUDED - skipping on ignorance could leave a thirsty crop dry, so
    /// the game's own menu keeps the last word exactly as before the filter existed. A bed the
    /// game will not let us target is left out too (2026-11-07): interacting with it opens no
    /// dialogue, and the sweep would stall on it instead of moving on.</summary>
    internal static List<BedObject> SweepBeds(EstateKey estate, PatchGroup patch)
    {
        IReadOnlyList<BedReading>? readings =
            Plugin.Garden.Census.BoundKey(estate, patch.Ordinal) is { } key
            && CensusPump.LastOutdoor.TryGetValue(key, out var read) ? read : null;

        return patch.Beds.Where(b =>
        {
            if (!b.Targetable)
                return false;
            var r = readings?.FirstOrDefault(x => x.Slot == b.Gimmick.BedIndex);
            return TendSweep.Include(r is not null, r?.Occupied ?? false, r?.Stage ?? 0);
        }).ToList();
    }

    /// <summary>A tend sweep (Tend All, Water Patch) over beds the caller already filtered
    /// through <see cref="SweepBeds"/>, where the count was claimed.</summary>
    internal void TendMany(List<BedObject> beds)
    {
        plugin.TendChain.TendMany(beds);
        plugin.Launched(plugin.TendChain);
    }

    /// <summary>A deliberate single press: never filtered.</summary>
    internal void TendOne(BedObject bed)
    {
        plugin.TendChain.TendOne(bed);
        plugin.Launched(plugin.TendChain);
    }

    /// <summary>Whether Replant ripe exists for this patch: the ledger last saw a bed in it
    /// ripe. The press itself re-reads the map first (<see cref="ReplantPatch"/>), so this only
    /// decides whether the button is offered.</summary>
    internal static bool AnyRipeInPatch(EstateKey estate, int patchOrdinal)
        => Plugin.Garden.Census.LedgerBeds.Any(b =>
            b.Estate == estate && !b.IsPot && b.PatchOrdinal == patchOrdinal
            && b.Latest?.Stage == 4);

    /// <summary>The patch twin of the pot sweep, one press: same-as-harvested plan off
    /// the ledger, ripe-or-empty beds only (a bed mid-growth is left out, exactly as the
    /// pot sweep leaves unripe pots alone - never a refusal), topsoil from the bags under
    /// the same "the bag is the plan" rule. Everything else is the existing patch cycle:
    /// CycleChain's own pre-flight still guards the run, so a shortage refuses in the
    /// player's terms before the first click. Returns the refusal, or null when the run
    /// started.</summary>
    internal string? ReplantPatch(EstateKey estate, PatchGroup patch)
    {
        // The freshest possible read: ripeness decides which beds are in, same as pots.
        CensusPump.SightNow();

        var plan = ReplantPlan.DefaultFor(estate, patch.Ordinal);
        if (Plugin.Garden.Census.BoundKey(estate, patch.Ordinal) is { } mapKey
            && CensusPump.LastOutdoor.TryGetValue(mapKey, out var readings))
        {
            foreach (var slot in plan.Seeds.Keys.ToList())
            {
                if (readings.FirstOrDefault(r => r.Slot == slot) is { Occupied: true, Stage: < 4 })
                    plan.Seeds.Remove(slot);
            }
        }

        // DefaultFor pre-fills FIRST topsoil found - right for a panel a human confirms,
        // not for a press nobody reviews. The one-press path holds the stricter rule.
        var soils = Plugin.Tables.Soils
            .Select(s => (s.ItemId, s.Name, Count: BagInventory.Count(s.ItemId)))
            .Where(s => s.Count > 0)
            .ToList();
        if (soils.Count == 0)
            return "no topsoil in bags - nothing to replant with";

        if (soils.Count > 1)
            return "more than one topsoil in bags ("
                + string.Join(", ", soils.Select(s => s.Name)) + ") - use Cycle... to pick one";

        plan.SoilItemId = soils[0].ItemId;
        if (CycleChain.PreflightError(patch, plan) is { } refusal)
            return refusal;

        plugin.CycleChain.Run(patch, plan);
        plugin.Launched(plugin.CycleChain);
        return null;
    }

    /// <summary>The patch cycle as the player set it up in the Cycle panel.</summary>
    internal void RunCycle(PatchGroup patch, ReplantPlan plan)
    {
        plugin.CycleChain.Run(patch, plan);
        plugin.Launched(plugin.CycleChain);
    }

    // ------------------------------------------------------------------ pots

    /// <summary>The pot Tend All's membership: every in-reach pot the live map says holds a
    /// plant that can still drink - ripe (stage 4) offers no Tend, so counting it would make the
    /// button claim waters it cannot pour (08-22 field report: "Water All (4 pots)" over 2 ripe
    /// cosmos). An unkeyed pot stays a per-row press - with no map key there is nothing honest
    /// to claim the water against, and a sweep should not queue guesses.</summary>
    internal static List<PotObject> WaterablePots(List<PotObject> pots)
        => pots
            .Where(p => p.InReach && p.MapKey is { } key
                && CensusPump.LastIndoor.TryGetValue(key, out var reading)
                && reading.Occupied && reading.Stage != 4)
            .ToList();

    /// <summary>Water All, over the list <see cref="WaterablePots"/> returned.</summary>
    internal void WaterPots(List<PotObject> pots)
    {
        plugin.PotChain.WaterMany(pots);
        plugin.Launched(plugin.PotChain);
    }

    internal void WaterPot(PotObject pot)
    {
        plugin.PotChain.Water(pot);
        plugin.Launched(plugin.PotChain);
    }

    internal void HarvestPot(PotObject pot)
    {
        plugin.PotChain.Harvest(pot);
        plugin.Launched(plugin.PotChain);
    }

    /// <summary>Plant one empty pot. 0 for soil or seed = "whatever I pick in game": the
    /// picker stays the player's and the chain only verifies.</summary>
    internal void PlantPot(PotObject pot, uint soilItemId, uint seedItemId)
    {
        plugin.PotChain.Plant(pot, soilItemId, seedItemId);
        plugin.Launched(plugin.PotChain);
    }

    /// <summary>Harvest one pot, then plant it with the soil and seed the player picked
    /// (0 = picked at the game's menu).</summary>
    internal void CyclePot(PotObject pot, uint soilItemId, uint seedItemId)
    {
        plugin.PotChain.Cycle(pot, soilItemId, seedItemId);
        plugin.Launched(plugin.PotChain);
    }

    /// <summary>Whether Replant ripe exists for the pots: some recorded pot the ledger last saw
    /// ripe is in reach. The press itself re-reads the map first (<see cref="ReplantPots"/>), so
    /// this only decides whether the button is offered.</summary>
    internal static bool AnyRipePotInReach(List<ClaimedBed> records, List<PotObject> pots)
        => records.Any(r =>
            r.IsPot && r.Latest?.Stage == 4
            && pots.FindIndex(p => p.MapKey == r.MapKey && p.InReach) >= 0);

    /// <summary>Replant ripe, the pots: harvest + replant every ripe pot with what it already
    /// grows, one press. Returns the refusal (or the first skip when nothing could be planned),
    /// or null when the run started.</summary>
    internal string? ReplantPots(List<ClaimedBed> records)
    {
        // The freshest possible read: ripeness decides who is in the sweep, and a stale
        // stage would build the wrong queue (same rule as the patch cycle).
        CensusPump.SightNow();

        var pots = ObjectSensor.AllPots();
        var candidates = records
            .Where(r => r.IsPot && r.Latest?.Stage == 4)
            .Select(r =>
            {
                var found = pots.FindIndex(p => p.MapKey == r.MapKey);
                return new PotCycleCandidate(r.MapKey, r.Latest?.SpeciesIndex,
                    found >= 0 && pots[found].InReach);
            })
            .ToList();

        var plan = PlanPotCycles(candidates);
        if (plan.Refusal is { } refusal)
            return refusal;

        if (plan.Jobs.Count == 0)
            return plan.Skips.Count > 0 ? plan.Skips[0] : "nothing ripe to cycle";

        var jobs = plan.Jobs
            .Select(j => (Pot: pots[pots.FindIndex(p => p.MapKey == j.Key)], j.SeedId))
            .ToList();
        plugin.PotChain.CycleMany(jobs, plan.SoilItemId, plan.Skips);
        plugin.Launched(plugin.PotChain);
        return null;
    }

    /// <summary>One pot's one-click cycle-with-itself: the sweep's rules applied to a
    /// single candidate. Returns the reason it could not be derived (the screen opens the
    /// Cycle picker beside it - never a guess, never a dead click), or null when the run
    /// started.</summary>
    internal string? ReplantPot(ClaimedBed record, PotObject pot)
    {
        var plan = PlanPotCycles(
            [new PotCycleCandidate(record.MapKey, record.Latest?.SpeciesIndex, pot.InReach)]);

        if (plan.Jobs.Count == 1)
        {
            plugin.PotChain.Cycle(pot, plan.SoilItemId, plan.Jobs[0].SeedId);
            plugin.Launched(plugin.PotChain);
            return null;
        }

        return plan.Refusal
            ?? (plan.Skips.Count > 0 ? plan.Skips[0] : "could not derive a replant");
    }

    /// <summary>One planner call for sweep and single-pot Replant alike - same rules,
    /// same words, whichever button was pressed.</summary>
    private static PotCyclePlan PlanPotCycles(List<PotCycleCandidate> candidates)
    {
        var soils = BagInventory.SoilsInBag().Select(s => new BagSoil(s.ItemId, s.Name, s.Count)).ToList();
        return PotCyclePlanner.Plan(candidates, soils, new BagInventory(), BagInventory.FreeSlots(), Plugin.Tables);
    }
}
