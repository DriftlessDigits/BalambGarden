using System;
using System.Collections.Generic;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sensing;
using BalambGarden.Game;
using Dalamud.Game.ClientState.Objects.Types;

namespace BalambGarden.Chains;

/// <summary>
/// The three pot verbs, one pot at a time. Same menus as a garden bed (the game even calls
/// a flowerpot a "bed" in the sow prompt - capture F3), so the driving is the same; what
/// differs is that a pot is its own one-bed patch and has to be identified before a receipt
/// about it means anything.
///
/// <para>Watering a pot changes the flower's colour (receipted). Whether a pot ALSO needs
/// water to live is unverified: no pot has ever been seen to wilt, but the evidence base
/// is flower seeds only - the dry-vs-watered twins labs are running to decide whether
/// that is a pot mechanic or a flower oddity. Until they report, nothing here asserts
/// either way.</para>
///
/// <para>Which pot: the chain knows before it acts. A pot object carries its own DataMap
/// key (HousingObject.HousingFurnitureIndex, the 08-16 receipt - via
/// <see cref="Game.ObjectSensor.AllPots"/>), so identity is a read, not an inference -
/// which is what lets WATERING bind at all. Watering writes nothing to the map (the 08-15 receipt has a freshly watered
/// melon and its dry twin byte-identical across all 48 bytes), so it could never have been
/// identified by evidence it does not produce.</para>
///
/// <para>The map-changing verbs still bracket themselves with a before/after read, and that
/// diff is now CORROBORATION rather than identity: one action on one known pot changes
/// exactly one entry, and if that entry is not the one the furniture vector named, the
/// correspondence has an exception and this chain says so loudly.</para>
/// </summary>
internal sealed unsafe class PotChain : ChainBase
{
    private const int HarvestWaitMS = 20_000;
    private const int HumanStepLimitMS = PlantFlow.HumanFillTimeoutMS + 15_000;

    /// <summary>How long the map gets to catch up with the action before the diff gives up.
    /// The plant receipt fires the instant we press Yes and the entry can appear a beat
    /// later, so the step polls instead of reading once - but a budget that ran forever
    /// would just be a slower way to guess.</summary>
    /// <summary>What the log calls the instrument when a receipt was bound by the pot's
    /// furniture entry rather than by a map diff.</summary>
    private const string FurnitureSource = "furniture index";

    private const int MapSettleMS = 4_000;
    private const int MapPollMS = 250;
    private const int BindStepLimitMS = MapSettleMS + 6_000;

    private string _plant = "";
    private string _obtained = "";
    // Which pot this run is driving, captured when the run opens it: the sensor already
    // resolved the object to its map key, so the identity is in hand before the first
    // click. Null means the furniture read could not name it - the run still tends the pot,
    // it just has nothing honest to record the receipt against.
    private int? _potKey;
    private DateTime _armedAt;
    private DateTime _waitUntil;
    private bool _waitAnnounced;
    private Func<string>? _pendingReceipt;

    /// <summary>This planting's picker driver, and what it managed to fill. Both belong to
    /// one pot's picker and are cleared when the run opens another.</summary>
    private GardeningFill? _fill;
    private string _filledBy = "";

    // The before half of the diff join, plus the settle budget for the after half.
    // _settleUntil == MinValue means "the bind step has not started its clock yet".
    private Dictionary<int, PotReading> _mapBefore = [];
    private DateTime _settleUntil;
    private DateTime _nextPollAt;

    /// <summary>Waters one pot. Colour change is the receipted effect; whether it is also
    /// a drink is the twins labs' question - see the class note.</summary>
    internal void Water(PotObject pot)
    {
        if (!BeginRun(1, "watering pot..."))
            return;

        Open(pot);
        TaskManager.Enqueue(TendOrQuit, "tend");
        TaskManager.Enqueue(FinishDialogue, "finish");
        Close("pot tended");
    }

    internal void Harvest(PotObject pot)
    {
        if (!BeginRun(1, "harvesting pot..."))
            return;

        Open(pot);
        TaskManager.Enqueue(SelectHarvest, "harvest");
        TaskManager.Enqueue(AwaitHarvest, HarvestWaitMS, "yield");
        TaskManager.Enqueue(() => AwaitPotBind(ReceiptVerb.Harvest, "harvested"),
            BindStepLimitMS, "identify");
        Close("pot harvested");
    }

    /// <summary>Exactly as beds: the chain fills the picker when it has been told what to
    /// fill it with, and waits for the player when it has not, then checks the confirmation
    /// before answering. Either id may be 0 - flowerpot flowers are absent from the crop
    /// table entirely and pot soils are absent from the topsoil table, so "whatever you put
    /// in" is a legitimate plan; it simply cannot be auto-filled, and the prompt is then
    /// reported rather than judged.</summary>
    internal void Plant(PotObject pot, uint soilItemId, uint expectedSeedId)
    {
        if (!BeginRun(1, "planting pot..."))
            return;

        Open(pot);
        TaskManager.Enqueue(() => SelectPlant(soilItemId, expectedSeedId), "plant");
        TaskManager.Enqueue(() => AwaitSow(expectedSeedId), HumanStepLimitMS, "sow");
        TaskManager.Enqueue(() => AwaitPotBind(ReceiptVerb.Plant, "planted"),
            BindStepLimitMS, "identify");
        Close("pot planted");
    }

    private void Open(PotObject pot)
    {
        _potKey = pot.MapKey;
        TaskManager.DelayNext(ApplyJitter(Plugin.Configuration.TendPaceMS));
        TaskManager.Enqueue(() => CheckStop("the pot"), "gate");
        TaskManager.Enqueue(() => Interact(pot.Object), "interact");
        TaskManager.Enqueue(AdvanceToMenu, "advance");
    }

    private void Close(string done)
        => TaskManager.Enqueue(() =>
        {
            LastOutcome = UnitsDone > 0 ? $"done: {done}" : LastOutcome;
            foreach (var line in Report)
                Plugin.Log.Information($"[PotChain] report: {line}");
            return true;
        }, "report");

    private bool? Interact(IGameObject pot)
    {
        if (!pot.IsValid())
        {
            RecordOutcome("(pot vanished): skipped");
            Abort("the pot went away (zone change?)");
            return true;
        }

        var targets = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
        if (targets == null)
            return false;

        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)pot.Address;
        if (native == null)
            return false;

        _plant = "";
        _obtained = "";
        _pendingReceipt = null;
        _fill = null;
        _filledBy = "";
        targets->Target = native;
        targets->InteractWithObject(native, false);
        return true;
    }

    /// <summary>Clicks through the pot's status Talk until its menu is up. A ripe pot's
    /// Talk names the flower ("Red Sunflowers\nThese flowers are in bloom."); an empty
    /// one says "There is nothing in this flowerpot." and names nothing - so that line is
    /// never mistaken for a plant.</summary>
    private bool? AdvanceToMenu()
    {
        if (PlantFlow.MenuReady(out _))
            return true;

        if (PaceReady() && PlantFlow.TalkReady(out var talk))
        {
            var headline = PlantFlow.TalkHeadline(talk);
            if (headline.Length > 0
                && !headline.Contains("flowerpot", StringComparison.OrdinalIgnoreCase))
                _plant = headline;
            PlantFlow.ClickTalk(talk);
            Acted();
        }

        return false;
    }

    private bool? TendOrQuit()
    {
        if (!PaceReady() || !PlantFlow.MenuReady(out var menu))
            return false;

        if (PlantFlow.SelectOption(menu, PlantFlow.TendOption))
        {
            Acted();
            // A water leaves no trace in the map, so this receipt used to have nothing but
            // species uniqueness to go on and gave up on twins. The furniture read names
            // the pot outright, so a water now claims like any other verb.
            var plant = _plant;
            var key = _potKey;
            _pendingReceipt = () => CensusPump.OnPotReceipt(
                ReceiptVerb.PotWater, plant, key, FurnitureSource);
            return true;
        }

        PlantFlow.SelectOption(menu, PlantFlow.QuitOption);
        Acted();
        RecordOutcome("pot: nothing to tend here");
        return true;
    }

    private bool? SelectHarvest()
    {
        if (!PaceReady() || !PlantFlow.MenuReady(out var menu))
            return false;

        // The before half, read while the pot is still full. Harvesting clears THIS pot's
        // entry and nothing else's (08-15 morning capture: the sunflower's entry went to
        // "nothing in this flowerpot"), so the one entry that changes across the action is
        // the pot we are standing at.
        SnapshotPots();

        if (!PlantFlow.SelectOption(menu, PlantFlow.HarvestOption))
        {
            PlantFlow.SelectOption(menu, PlantFlow.QuitOption);
            RecordOutcome("pot: nothing ripe to harvest");
            Abort($"the pot offered no '{PlantFlow.HarvestOption}'");
            return true;
        }

        _armedAt = ObtainWatch.Arm();
        Acted();
        return true;
    }

    /// <summary>Waits for the yield to actually land in the bag. The obtained item's text
    /// is copied here rather than read later: ObtainWatch is a permanent subscriber and any
    /// loot line in the meantime would overwrite it.</summary>
    private bool? AwaitHarvest()
    {
        if (!ObtainWatch.FiredSince(_armedAt))
            return false;

        _obtained = ObtainWatch.LastItem;
        return true;
    }

    private bool? SelectPlant(uint soilItemId, uint expectedSeedId)
    {
        if (!PaceReady() || !PlantFlow.MenuReady(out var menu))
            return false;

        if (!PlantFlow.SelectOption(menu, PlantFlow.PlantOption))
        {
            PlantFlow.SelectOption(menu, PlantFlow.QuitOption);
            RecordOutcome($"pot: no '{PlantFlow.PlantOption}' on offer (something already planted?)");
            Abort("the pot offered no planting");
            return true;
        }

        _waitUntil = DateTime.UtcNow.AddMilliseconds(PlantFlow.HumanFillTimeoutMS);
        _waitAnnounced = false;
        _filledBy = "";
        _fill = new GardeningFill(soilItemId, expectedSeedId);
        Acted();
        return true;
    }

    private bool? AwaitSow(uint expectedSeedId)
    {
        if (PlantFlow.SowPromptReady(out var prompt))
            return ConfirmSow(prompt, expectedSeedId);

        // Waiting on the player is a safe stop point (08-16: a one-unit pot run has no
        // next boundary, so Stop sat armed for the whole wait doing nothing visible).
        if (StopRequested)
        {
            RecordOutcome("pot: stopped while waiting - picker is yours");
            Abort("stopped by user during the wait");
            return true;
        }

        if (_fill is { } fill)
        {
            fill.Tick();
            if (fill.Filled)
            {
                _filledBy = fill.What;
                Note($"pot: filled {fill.What}");
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
            var seedName = Plugin.Tables.CropBySeedId(expectedSeedId)?.SeedName ?? "your seed";
            Note($"pot: fill soil + {seedName}, then Confirm - waiting");
        }

        if (DateTime.UtcNow <= _waitUntil)
            return false;

        var seconds = PlantFlow.HumanFillTimeoutMS / 1000;
        RecordOutcome($"pot: nothing planted - no confirm within {seconds}s");
        Abort($"no confirm within {seconds}s");
        return true;
    }

    private bool ConfirmSow(string prompt, uint expectedSeedId)
    {
        var crop = Plugin.Tables.CropBySeedId(expectedSeedId);
        // No soil expectation for pots: the capture's own prompt named "potting soil",
        // which is nothing in the topsoil table, and the player picks it anyway.
        var check = SowPrompt.Check(prompt, expectedSoil: null, expectedSeed: crop?.SeedName);

        if (!check.Ok)
        {
            PlantFlow.AnswerSow(false);
            RecordOutcome($"pot: refused - {check.Reason}");
            Abort(check.Reason ?? "sow prompt did not match");
            return true;
        }

        // The before half, read in the same breath as the Yes. It has to be this late: the
        // human fill step can sit open for minutes, and a snapshot taken back then would
        // have another pot's stage tick in it and read as two changes instead of one.
        _plant = crop?.Name ?? check.Parts!.Seed;
        SnapshotPots();

        PlantFlow.AnswerSow(true);
        Acted();
        return true;
    }

    /// <summary>The before half of the diff: a fresh indoor read taken just before a
    /// map-changing verb. Deliberately MapSensor rather than CensusPump.SightNow - a sight
    /// posts an observation to every claimed pot, and this read happens a dozen times
    /// during the settle poll, which would flush the eight-slot observation ring and take
    /// the receipt provenance with it.</summary>
    private void SnapshotPots()
    {
        _mapBefore = MapSensor.ReadIndoor();
        _settleUntil = DateTime.MinValue;
        _nextPollAt = DateTime.MinValue;
    }

    /// <summary>
    /// The after half: the receipt, and the audit of the thing it was bound by.
    ///
    /// <para>When the furniture read named this pot, that is the identity and the diff is
    /// only asked whether it agrees. Agreement is the ordinary case and worth one word in
    /// the log; DISAGREEMENT is a finding - it would mean the furniture index is not always
    /// the map key - so it is warned about loudly with both numbers, and the receipt still
    /// goes in on the read rather than the inference (the read has receipts at two estates;
    /// a diff has whatever else moved in the room that second).</para>
    ///
    /// <para>Without a furniture key it is the old rule exactly: exactly one changed entry
    /// binds, anything else says which of the two failures happened and records nothing.
    /// The map can lag the action by a beat (the plant receipt fires at Yes, the entry
    /// appears afterwards), so zero changes keeps polling until the settle budget runs
    /// out - and then it is a refusal, never a guess.</para>
    /// </summary>
    private bool? AwaitPotBind(ReceiptVerb verb, string done)
    {
        if (_settleUntil == DateTime.MinValue)
            _settleUntil = DateTime.UtcNow.AddMilliseconds(MapSettleMS);

        if (DateTime.UtcNow < _nextPollAt)
            return false;
        _nextPollAt = DateTime.UtcNow.AddMilliseconds(MapPollMS);

        var changed = PotDiff.ChangedKeys(_mapBefore, MapSensor.ReadIndoor());
        var settled = DateTime.UtcNow > _settleUntil;

        // Keep polling while the map still has time to catch up - the corroboration is
        // worth the same beat the old identity path needed.
        if (changed.Count == 0 && !settled)
            return false;

        if (_potKey is { } key)
        {
            if (changed.Count == 1 && changed[0] != key)
            {
                // The finding this audit exists for. Both numbers, out loud, every time.
                Plugin.Log.Warning(
                    $"[PotChain] POT IDENTITY MISMATCH: furniture index says key {key}, "
                    + $"the map diff says key {changed[0]} - recorded against {key}. "
                    + "The furniture-index-is-the-map-key correspondence has an exception.");
                Note($"pot: identity check disagreed (furniture {key} vs diff {changed[0]})");
            }
            else if (changed.Count == 1)
            {
                Plugin.Log.Information($"[PotChain] pot identity corroborated: key {key}");
            }
            else
            {
                // Not a refusal any more: the diff was never the identity here. It simply
                // has nothing to say, which is what a laggy map or a busy room looks like.
                Note(changed.Count == 0
                    ? "pot: identity uncorroborated - the map never changed"
                    : $"pot: identity uncorroborated - the map changed in {changed.Count} places");
            }

            RecordOutcome(
                CensusPump.OnPotReceipt(verb, _plant, key, FurnitureSource) + Obtained());
            return true;
        }

        if (changed.Count == 1)
        {
            RecordOutcome(
                CensusPump.OnPotReceipt(verb, _plant, changed[0], "map diff") + Obtained());
            return true;
        }

        if (changed.Count == 0)
        {
            RecordOutcome(
                $"pot {done}: the map never changed - cannot tell which pot this is, "
                + $"not recorded{Obtained()}");
            return true;
        }

        RecordOutcome(
            $"pot {done}: map changed in {changed.Count} places - cannot tell which pot "
            + $"this is, not recorded{Obtained()}");
        return true;
    }

    /// <summary>What actually came out of the pot, for the harvest lines only.</summary>
    private string Obtained() => _obtained.Length > 0 ? $" (obtained {_obtained})" : "";

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
