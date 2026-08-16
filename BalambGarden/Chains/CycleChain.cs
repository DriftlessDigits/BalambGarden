using System;
using System.Collections.Generic;
using System.Linq;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sensing;
using BalambGarden.Game;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BalambGarden.Chains;

/// <summary>What a cycle intends to put back in the ground. Mutable: the dashboard edits
/// it in place before launch, and the pre-flight line re-reads it every frame.</summary>
internal sealed class ReplantPlan
{
    /// <summary>0 = nothing chosen yet; the pre-flight says so rather than picking one.</summary>
    internal uint SoilItemId { get; set; }

    /// <summary>bed slot (0-based) -> seed item id. A bed with no entry is not in the cycle.</summary>
    internal Dictionary<int, uint> Seeds { get; } = [];

    /// <summary>The gold-standard pass: a full tend round after the cycle, so every
    /// replanted bed carries an anchored plant AND tend receipt.</summary>
    internal bool AnchorTendRound { get; set; }

    /// <summary>Same-as-harvested, straight off the ledger: each recorded bed's latest
    /// species becomes its own seed again. Beds the ledger has no species for get no entry
    /// and are simply not part of the cycle - the plan never guesses at a bed's history.</summary>
    internal static ReplantPlan DefaultFor(EstateKey estate, int patchOrdinal)
    {
        var plan = new ReplantPlan();
        foreach (var bed in Plugin.Garden.Census.LedgerBeds)
        {
            if (bed.Estate != estate || bed.PatchOrdinal != patchOrdinal || bed.IsPot)
                continue;
            if (bed.Latest is not { SpeciesIndex: not 0 } latest)
                continue;
            if (Plugin.Tables.SeedIdBySpeciesIndex(latest.SpeciesIndex) is not { } seedId)
                continue;
            plan.Seeds[bed.BedSlot] = seedId;
        }

        plan.SoilItemId = CycleChain.FirstSoilInBag();
        return plan;
    }
}

/// <summary>
/// The harvest -> replant cycle. Its shape IS the invariant: per bed, harvest then replant
/// before moving on, so a run that stops halfway leaves whole beds behind rather than a
/// patch of empty holes. Batch order (harvest everything, then plant everything) is not
/// expressible here on purpose.
///
/// <para>The plant step fills the picker itself when it can (see <see cref="GardeningFill"/>):
/// the soil and seed columns above are the order form, and the chain clicks the two slots,
/// picks those two items and presses Confirm. When any of that does not look like the picker
/// it was written against, it stops clicking and the step is the HYBRID one it has always
/// been (Sam's ruling, 2026-08-15) - picker open, "waiting" in the feed, the player fills it.
/// Either way the chain then reads the confirmation prompt the game builds from the filled
/// slots, checks it against the plan, and answers Yes only when it matches. That check is now
/// guarding our own fill as well as a human's, which is exactly why it stays.</para>
/// </summary>
internal sealed unsafe class CycleChain : ChainBase
{
    /// <summary>How long to wait for the chat obtain line after selecting Harvest. The
    /// harvest itself fires on the selection (capture F4); this budget covers animation and
    /// chat latency, nothing more.</summary>
    private const int HarvestWaitMS = 20_000;

    /// <summary>Slack above <see cref="PlantFlow.HumanFillTimeoutMS"/> so the chain's own
    /// honest timeout always fires before the task manager's blunt one.</summary>
    private const int HumanStepLimitMS = PlantFlow.HumanFillTimeoutMS + 15_000;

    private string _plant = "";
    private string _header = "";
    private DateTime _armedAt;
    private DateTime _waitUntil;
    private bool _waitAnnounced;
    private Func<string>? _pendingReceipt;

    /// <summary>This bed's picker driver, made when the plant option is selected. Null once
    /// the fill is over one way or the other.</summary>
    private GardeningFill? _fill;

    /// <summary>What actually filled the picker, for the run log's plant line: the driver's
    /// "soil + seed" when it did the fill, empty when the player did.</summary>
    private string _filledBy = "";

    /// <summary>Freshest map read for planning, throttled: the dashboard's live pre-flight
    /// line asks every frame, and a map read every frame is a sensor, not a UI.</summary>
    private static DateTime nextPlanSightUtc = DateTime.MinValue;

    internal static void RefreshForPlanning()
    {
        if (DateTime.UtcNow < nextPlanSightUtc)
            return;
        nextPlanSightUtc = DateTime.UtcNow.AddSeconds(2);
        CensusPump.SightNow();
    }

    /// <summary>First topsoil the bag actually holds, as a starting suggestion. Zero when
    /// there is none - the pre-flight then says exactly that.</summary>
    internal static uint FirstSoilInBag()
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return 0;
        foreach (var soil in Plugin.Tables.Soils)
        {
            if (inventory->GetInventoryItemCount(soil.ItemId) > 0)
                return soil.ItemId;
        }

        return 0;
    }

    // ---------------------------------------------------------------- pre-flight

    /// <summary>Fail-closed: null means go. Every refusal names the shortfall in the
    /// player's own terms, because a cycle that half-runs costs seeds and a growth cycle.</summary>
    internal static string? PreflightError(PatchGroup patch, ReplantPlan plan)
    {
        if (EstateSensor.Current() is not { } estate)
            return "not at an estate";
        if (!patch.InReach)
            return $"walk closer to patch {patch.Ordinal + 1} ({patch.Distance:F1}y away)";
        if (plan.Seeds.Count == 0)
            return "nothing planned: no recorded species here to replant - stand by the patch a moment, or tend it once";
        if (Plugin.Garden.Census.BoundKey(estate, patch.Ordinal) is not { } mapKey)
            return $"patch {patch.Ordinal + 1} isn't bound yet - tend it once so the ledger knows which patch this is";
        if (!CensusPump.LastOutdoor.TryGetValue(mapKey, out var readings))
            return "no map read for this patch yet - stand still for a moment";

        var toHarvest = 0;
        foreach (var (slot, _) in plan.Seeds.OrderBy(kv => kv.Key))
        {
            var recorded = Plugin.Garden.Census.LedgerBeds.FirstOrDefault(b =>
                b.Estate == estate && b.PatchOrdinal == patch.Ordinal && b.BedSlot == slot && !b.IsPot);
            if (recorded is null)
                return $"bed {slot + 1} has no census record yet - stand by the patch a moment";

            var bedObject = patch.Beds.FirstOrDefault(b => b.Gimmick.BedIndex == slot);
            if (bedObject.Object is null || !bedObject.InReach)
                return $"bed {slot + 1} isn't in reach";

            var reading = readings.FirstOrDefault(r => r.Slot == slot);
            if (reading is null)
                return $"bed {slot + 1} isn't in the map read - stand still for a moment";

            // Half-cycled guard: a bed part-way through a growth is not something to
            // harvest or plant over, and a previous abort leaves exactly that behind.
            if (reading.Occupied && reading.Stage < 4)
                return $"bed {slot + 1} is still growing (stage {reading.Stage}) - a cycle wants ripe or empty beds";
            if (reading.Occupied)
                toHarvest++;
        }

        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return "inventory unavailable";

        var free = inventory->GetEmptySlotsInBag();
        if (free < toHarvest)
            return $"need {toHarvest} free bag slots for yields, have {free}";

        if (plan.SoilItemId == 0)
            return "no soil chosen";

        var soilName = Plugin.Tables.SoilByItemId(plan.SoilItemId)?.Name ?? "soil";
        var soil = inventory->GetInventoryItemCount(plan.SoilItemId);
        if (soil < plan.Seeds.Count)
            return $"need {plan.Seeds.Count}x {soilName}, have {soil}";

        foreach (var group in plan.Seeds.GroupBy(kv => kv.Value))
        {
            var have = inventory->GetInventoryItemCount(group.Key);
            var name = Plugin.Tables.CropBySeedId(group.Key)?.SeedName ?? $"seed {group.Key}";
            if (have < group.Count())
                return $"need {group.Count()}x {name}, have {have}";
        }

        return null;
    }

    // ---------------------------------------------------------------- the run

    internal void Run(PatchGroup patch, ReplantPlan plan)
    {
        // The freshest possible read: pre-flight decides which beds get harvested, and a
        // stale stage would build the wrong queue.
        CensusPump.SightNow();

        if (PreflightError(patch, plan) is { } refusal)
        {
            LastOutcome = $"refused: {refusal}";
            return;
        }

        var estate = EstateSensor.Current()!;
        var mapKey = Plugin.Garden.Census.BoundKey(estate, patch.Ordinal)!.Value;
        var readings = CensusPump.LastOutdoor[mapKey];

        var beds = plan.Seeds
            .OrderBy(kv => kv.Key)
            .Select(kv => (
                Slot: kv.Key,
                SeedId: kv.Value,
                Object: patch.Beds.First(b => b.Gimmick.BedIndex == kv.Key),
                Ripe: readings.FirstOrDefault(r => r.Slot == kv.Key) is { Occupied: true }))
            .ToList();

        // Units are report-worthy completions, not beds: a ripe bed yields a harvest line
        // and a plant line, an empty one only a plant line.
        var units = beds.Count(b => b.Ripe) + beds.Count
            + (plan.AnchorTendRound ? beds.Count : 0);
        if (!BeginRun(units, $"cycling {beds.Count} beds..."))
            return;

        for (var i = 0; i < beds.Count; i++)
        {
            var (slot, seedId, bedObject, ripe) = beds[i];
            var label = $"bed {slot + 1}";

            TaskManager.DelayNext(i == 0
                ? ApplyJitter(Plugin.Configuration.TendPaceMS)
                : ApplyJitter(Plugin.Configuration.PostTendDelayMS, Plugin.Configuration.PostTendJitterMS));

            // The one place a user stop may land: between beds, never between a bed's
            // harvest and its replant.
            TaskManager.Enqueue(() => CheckStop(label), $"gate {slot}");

            if (ripe)
            {
                TaskManager.Enqueue(() => Interact(bedObject.Object), $"interact-h {slot}");
                TaskManager.Enqueue(AdvanceToMenu, $"advance-h {slot}");
                TaskManager.Enqueue(() => SelectHarvest(label), $"harvest {slot}");
                TaskManager.Enqueue(AwaitHarvest, HarvestWaitMS, $"yield {slot}");
            }

            TaskManager.Enqueue(() => Interact(bedObject.Object), $"interact-p {slot}");
            TaskManager.Enqueue(AdvanceToMenu, $"advance-p {slot}");
            TaskManager.Enqueue(() => SelectPlant(label, plan.SoilItemId, seedId), $"plant {slot}");
            TaskManager.Enqueue(
                () => AwaitSow(label, plan.SoilItemId, seedId), HumanStepLimitMS, $"sow {slot}");
        }

        if (plan.AnchorTendRound)
        {
            foreach (var (slot, _, bedObject, _) in beds)
            {
                TaskManager.DelayNext(
                    ApplyJitter(Plugin.Configuration.PostTendDelayMS, Plugin.Configuration.PostTendJitterMS));
                TaskManager.Enqueue(() => CheckStop($"anchor tend, bed {slot + 1}"), $"gate-t {slot}");
                TaskManager.Enqueue(() => Interact(bedObject.Object), $"interact-t {slot}");
                TaskManager.Enqueue(AdvanceToMenu, $"advance-t {slot}");
                TaskManager.Enqueue(SelectTend, $"tend {slot}");
                TaskManager.Enqueue(FinishDialogue, $"finish-t {slot}");
            }
        }

        var total = beds.Count;
        TaskManager.Enqueue(() =>
        {
            LastOutcome = $"done: {total} beds cycled";
            foreach (var line in Report)
                Plugin.Log.Information($"[CycleChain] report: {line}");
            return true;
        }, "report");
    }

    // ---------------------------------------------------------------- steps

    private bool? Interact(IGameObject bed)
    {
        if (!bed.IsValid())
        {
            RecordOutcome("(bed vanished): skipped");
            Abort("bed list went stale (zone change?)");
            return true;
        }

        var targets = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
        if (targets == null)
            return false;

        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)bed.Address;
        if (native == null)
            return false;

        _plant = "";
        _header = "";
        // A receipt never survives into the next step: an unfired one belongs to a
        // conversation that did not finish, and dropping it is the honest outcome. The
        // picker driver goes the same way - it belongs to one bed's picker.
        _pendingReceipt = null;
        _fill = null;
        _filledBy = "";

        targets->Target = native;
        targets->InteractWithObject(native, false);
        return true;
    }

    /// <summary>Clicks through the status Talk (which names the plant) until the menu is up.</summary>
    private bool? AdvanceToMenu()
    {
        if (PlantFlow.MenuReady(out _))
            return true;

        if (PaceReady() && PlantFlow.TalkReady(out var talk))
        {
            if (PlantFlow.TalkHeadline(talk) is { Length: > 0 } headline
                && !headline.Contains("flowerpot", StringComparison.OrdinalIgnoreCase))
                _plant = headline;
            PlantFlow.ClickTalk(talk);
            Acted();
        }

        return false;
    }

    private bool? SelectHarvest(string label)
    {
        if (!PaceReady() || !PlantFlow.MenuReady(out var menu))
            return false;

        _header = PlantFlow.MenuHeader(menu);
        if (!PlantFlow.SelectOption(menu, PlantFlow.HarvestOption))
        {
            RecordOutcome($"{_header}: no harvest on offer - stopped");
            Abort($"{label} offered no '{PlantFlow.HarvestOption}'");
            return true;
        }

        // Arm BEFORE the yield can possibly land: the obtain line is the completion
        // signal and a line that arrives between selection and arming would be missed.
        _armedAt = ObtainWatch.Arm();
        Acted();
        return true;
    }

    /// <summary>The harvest's confirmation is the chat obtain line - there is no closing
    /// dialogue at all (capture F4). No line, no receipt: the ledger never hears about a
    /// yield nobody saw arrive.</summary>
    private bool? AwaitHarvest()
    {
        if (!ObtainWatch.FiredSince(_armedAt))
            return false;

        RecordOutcome(CensusPump.OnBedReceipt(ReceiptVerb.Harvest, _header, _plant)
            + $" (obtained {ObtainWatch.LastItem})");
        return true;
    }

    private bool? SelectPlant(string label, uint soilId, uint seedId)
    {
        if (!PaceReady() || !PlantFlow.MenuReady(out var menu))
            return false;

        _header = PlantFlow.MenuHeader(menu);
        if (!PlantFlow.SelectOption(menu, PlantFlow.PlantOption))
        {
            RecordOutcome($"{_header}: no '{PlantFlow.PlantOption}' on offer - stopped");
            Abort($"{label} offered no planting");
            return true;
        }

        _waitUntil = DateTime.UtcNow.AddMilliseconds(PlantFlow.HumanFillTimeoutMS);
        _waitAnnounced = false;
        _filledBy = "";
        _fill = new GardeningFill(soilId, seedId);
        Acted();
        return true;
    }

    /// <summary>The sow step. The driver fills the picker when it can and the player fills
    /// it when the driver cannot - either way this step ends at the same confirmation, read
    /// and checked the same way. The waiting line is only spoken once the fill has actually
    /// stood down, so the run log never asks for hands that were not needed.</summary>
    private bool? AwaitSow(string label, uint soilId, uint seedId)
    {
        if (PlantFlow.SowPromptReady(out var prompt))
            return ConfirmSow(label, prompt, soilId, seedId);

        // Waiting on the player is a safe stop point (08-16 stop-does-nothing finding).
        if (StopRequested)
        {
            RecordOutcome($"{label}: stopped while waiting - picker is yours");
            Abort("stopped by user during the wait");
            return true;
        }

        if (_fill is { } fill)
        {
            fill.Tick();
            if (fill.Filled)
            {
                _filledBy = fill.What;
                Note($"{label}: filled {fill.What}");
                _fill = null;
            }
            else if (fill.GaveUp is not null)
            {
                _fill = null;
            }
        }

        if (!_waitAnnounced && _fill is null && _filledBy.Length == 0 && PlantFlow.GardeningOpen())
        {
            _waitAnnounced = true;
            var seedName = Plugin.Tables.CropBySeedId(seedId)?.SeedName ?? $"seed {seedId}";
            var soilName = Plugin.Tables.SoilByItemId(soilId)?.Name ?? "soil";
            Note($"{label}: fill {soilName} + {seedName}, then Confirm - waiting");
        }

        if (DateTime.UtcNow <= _waitUntil)
            return false;

        var seconds = PlantFlow.HumanFillTimeoutMS / 1000;
        RecordOutcome($"{label}: nothing planted - no confirm within {seconds}s");
        Abort($"no confirm within {seconds}s");
        return true;
    }

    /// <summary>The prompt is the only surface that names what is about to go in the
    /// ground. Match the plan and it gets a Yes; anything else gets a No, and the run stops
    /// at this bed boundary rather than spending the wrong seed.</summary>
    private bool ConfirmSow(string label, string prompt, uint soilId, uint seedId)
    {
        var expectedSoil = Plugin.Tables.SoilByItemId(soilId)?.Name;
        var crop = Plugin.Tables.CropBySeedId(seedId);
        var check = SowPrompt.Check(prompt, expectedSoil, crop?.SeedName);

        if (!check.Ok)
        {
            PlantFlow.AnswerSow(false);
            RecordOutcome($"{label}: refused - {check.Reason}");
            Abort(check.Reason ?? "sow prompt did not match the plan");
            return true;
        }

        PlantFlow.AnswerSow(true);
        Acted();

        // Planting completes silently after Yes - the capture saw no further addon event -
        // so this IS the completion, not a fire we are receipting early.
        var plantName = crop?.Name ?? check.Parts!.Seed;
        RecordOutcome(CensusPump.OnBedReceipt(ReceiptVerb.Plant, _header, plantName, stageOverride: 1));
        return true;
    }

    private bool? SelectTend()
    {
        if (!PaceReady() || !PlantFlow.MenuReady(out var menu))
            return false;

        _header = PlantFlow.MenuHeader(menu);
        if (!PlantFlow.SelectOption(menu, PlantFlow.TendOption))
        {
            // Not fatal: a freshly sown bed that will not take water is odd, not broken.
            PlantFlow.SelectOption(menu, PlantFlow.QuitOption);
            RecordOutcome($"{_header}: no tend on offer - skipped");
            Acted();
            return true;
        }

        Acted();
        var plant = _plant;
        var header = _header;
        _pendingReceipt = () => CensusPump.OnBedReceipt(ReceiptVerb.Tend, header, plant);
        return true;
    }

    /// <summary>Clicks through whatever dialogue follows; done when quiet. Quiet is the
    /// confirmation - the held receipt routes here, never at selection.</summary>
    private bool? FinishDialogue()
    {
        if (PlantFlow.TalkReady(out var talk))
        {
            if (PaceReady())
            {
                PlantFlow.ClickTalk(talk);
                Acted();
            }

            return false;
        }

        if (_pendingReceipt is { } receipt)
        {
            _pendingReceipt = null;
            RecordOutcome(receipt());
        }

        return true;
    }
}
