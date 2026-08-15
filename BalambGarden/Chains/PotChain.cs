using System;
using BalambGarden.Engine.Census;
using BalambGarden.Game;
using Dalamud.Game.ClientState.Objects.Types;

namespace BalambGarden.Chains;

/// <summary>
/// The three pot verbs, one pot at a time. Same menus as a garden bed (the game even calls
/// a flowerpot a "bed" in the sow prompt - capture F3), so the driving is the same; what
/// differs is that a pot is its own one-bed patch and binds by species uniqueness.
///
/// <para>Watering a pot is PIGMENT ONLY. Flowerpots do not wilt (08-15 finding: every
/// flowerpot seed is 1-day grow with no wilt time, corroborated by our own unwatered
/// sunflower running seed-to-ripe), so nothing here - label, tooltip, or run-log line -
/// may suggest a thirsty plant.</para>
/// </summary>
internal sealed unsafe class PotChain : ChainBase
{
    private const int HarvestWaitMS = 20_000;
    private const int HumanStepLimitMS = PlantFlow.HumanFillTimeoutMS + 15_000;

    private string _plant = "";
    private DateTime _armedAt;
    private DateTime _waitUntil;
    private bool _waitAnnounced;
    private Func<string>? _pendingReceipt;

    /// <summary>Applies pigment. Not a drink - see the class note.</summary>
    internal void Water(PotObject pot)
    {
        if (!BeginRun(1, "tending pot (pigment)..."))
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
        Close("pot harvested");
    }

    /// <summary>Hybrid, exactly as beds: the chain opens the picker and waits while the
    /// player fills soil and seed, then checks the confirmation before answering.
    /// <paramref name="expectedSeedId"/> may be 0 - flowerpot flowers are absent from the
    /// crop table entirely, so "whatever you put in" is a legitimate plan, and the prompt
    /// is then reported rather than judged.</summary>
    internal void Plant(PotObject pot, uint expectedSeedId)
    {
        if (!BeginRun(1, "planting pot..."))
            return;

        Open(pot);
        TaskManager.Enqueue(SelectPlant, "plant");
        TaskManager.Enqueue(() => AwaitSow(expectedSeedId), HumanStepLimitMS, "sow");
        Close("pot planted");
    }

    private void Open(PotObject pot)
    {
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
        _pendingReceipt = null;
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
            var plant = _plant;
            _pendingReceipt = () => CensusPump.OnPotReceipt(ReceiptVerb.PotWater, plant);
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

    private bool? AwaitHarvest()
    {
        if (!ObtainWatch.FiredSince(_armedAt))
            return false;

        RecordOutcome(CensusPump.OnPotReceipt(ReceiptVerb.Harvest, _plant)
            + $" (obtained {ObtainWatch.LastItem})");
        return true;
    }

    private bool? SelectPlant()
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
        Acted();
        return true;
    }

    private bool? AwaitSow(uint expectedSeedId)
    {
        if (PlantFlow.SowPromptReady(out var prompt))
            return ConfirmSow(prompt, expectedSeedId);

        if (!_waitAnnounced && PlantFlow.GardeningOpen())
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

        PlantFlow.AnswerSow(true);
        Acted();
        RecordOutcome(CensusPump.OnPotReceipt(ReceiptVerb.Plant, crop?.Name ?? check.Parts!.Seed));
        return true;
    }

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
