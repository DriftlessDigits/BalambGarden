using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace BalambGarden.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("Balamb Garden Settings###BalambGardenConfig")
    {
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;

        Size = new Vector2(400, 540);
        SizeCondition = ImGuiCond.Always;

        configuration = Plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        // Flags must be added or removed before Draw() is being called, or they won't apply
        if (configuration.IsConfigWindowMovable)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }
    }

    public override void Draw()
    {
        DrawBehaviour();
        ImGui.Separator();
        DrawSync();
        ImGui.Separator();
        DrawSensing();
        ImGui.Separator();
        ImGui.Text("Tend pacing");

        var pace = configuration.TendPaceMS;
        if (ImGui.SliderInt("Pace (ms)", ref pace, 250, 3000))
        {
            configuration.TendPaceMS = pace;
            configuration.Save();
        }

        var postTend = configuration.PostTendDelayMS;
        if (ImGui.SliderInt("Between beds (ms)", ref postTend, 3000, 15000))
        {
            configuration.PostTendDelayMS = postTend;
            configuration.Save();
        }

        var postTendJitter = configuration.PostTendJitterMS;
        if (ImGui.SliderInt("Between-beds jitter (+/- ms)", ref postTendJitter, 0, 3000))
        {
            configuration.PostTendJitterMS = postTendJitter;
            configuration.Save();
        }

        var jitter = configuration.JitterMS;
        if (ImGui.SliderInt("Jitter (+/- ms)", ref jitter, 0, 1500))
        {
            configuration.JitterMS = jitter;
            configuration.Save();
        }
    }

    // Fingerprint memo: HKDF twice per frame would be pure waste, and the value only
    // changes when the phrase does. Keyed by the phrase itself, never displayed with it.
    private string fingerprintFor = "";
    private string fingerprint = "";

    // Session-only: the eye never persists - a fresh window always opens masked.
    private bool revealPhrase;

    /// <summary>Household sync (design ruling 16; admin/user split ruled 2026-08-28). The
    /// phrase field is the on-switch - empty is OFF, and the phrase never leaves this config.
    /// The fingerprint line is the admin's half: exactly the AUTH_TOKEN_HASH the relay
    /// expects, safe to display and copy because it is one-way twice from the phrase.</summary>
    private void DrawSync()
    {
        ImGui.Text("Household sync");

        var phrase = configuration.SyncPassphrase;
        ImGui.SetNextItemWidth(260f);
        ImGui.InputText("Household phrase", ref phrase, 256,
            revealPhrase ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            // Trimmed to match the relay's setup flow - a copy-pasted phrase with a stray
            // trailing space would otherwise derive a whole different identity.
            var trimmed = phrase.Trim();
            if (trimmed != configuration.SyncPassphrase)
            {
                configuration.SyncPassphrase = trimmed;
                configuration.Save();

                // A new phrase is a NEW HOUSEHOLD - the ledger owns what that means
                // (LedgerStore.ResetHousehold, desk-tested); this window only reports the
                // edit. One estate lap re-mints; cursor 0 replays the new stream's window.
                Plugin.Garden.Ledger.ResetHousehold();
                Plugin.Garden.Save();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "One phrase for the whole household - each install types the same one.\n"
                + "It stays in this config file and is never sent anywhere.\n"
                + "Leave it empty to keep sync off entirely.");

        // The unmask eye: typos in a masked field derive a whole different household, and
        // the phrase is a garden password, not a bank one - letting the typist SEE it is
        // cheaper than the wrong-household debugging session.
        ImGui.SameLine();
        using (ImRaii.PushFont(Dalamud.Interface.UiBuilder.IconFont))
        {
            var icon = revealPhrase
                ? Dalamud.Interface.FontAwesomeIcon.EyeSlash
                : Dalamud.Interface.FontAwesomeIcon.Eye;
            if (ImGui.SmallButton(icon.ToIconString()))
                revealPhrase = !revealPhrase;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(revealPhrase ? "Hide the phrase" : "Show the phrase");

        if (configuration.SyncPassphrase.Length == 0)
        {
            ImGui.TextDisabled("Sync is off - enter the household phrase to turn it on.");
            return;
        }

        if (fingerprintFor != configuration.SyncPassphrase)
        {
            fingerprintFor = configuration.SyncPassphrase;
            fingerprint = BalambGarden.Engine.Sync.HouseholdKeys.RelayFingerprint(fingerprintFor);
        }

        // Open-household ruling 2026-08-29: no admin step exists - typing the phrase IS the
        // whole setup. This id is the namespace the relay files this household's rows under;
        // it earns its pixels the day someone is debugging rows in the database.
        ImGui.TextDisabled("Household id (how the relay files your rows):");
        ImGui.TextWrapped(fingerprint);
        if (ImGui.Button("Copy household id"))
            ImGui.SetClipboardText(fingerprint);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Debug identity only - nothing to configure anywhere.\n"
                + "It cannot be turned back into the phrase.");
    }

    /// <summary>How far the dashboard's patch sweep reaches. Wider finds a far corner of a
    /// big plot; too wide starts finding the neighbour's beds (their twin ordinal sat at
    /// 37.9y on 08-14), and a patch you cannot claim is worse than one you have to walk to.
    /// Recon's own 40y sweep is fixed and untouched by this - the instrument is supposed to
    /// see more than the app.</summary>
    private void DrawSensing()
    {
        ImGui.Text("Sensing");

        var radius = configuration.PatchScanRadius;
        if (ImGui.SliderFloat("Patch scan radius (y)", ref radius, 5f, 40f, "%.0f"))
        {
            configuration.PatchScanRadius = radius;
            configuration.Save();
        }
    }

    /// <summary>The switches that decide when the plugin speaks, what it writes down, and
    /// how much of the planting picker it fills. What gets recorded is not among them
    /// (08-15): the game-granted roster decides that, not a checkbox.</summary>
    private void DrawBehaviour()
    {
        var nudge = configuration.NudgeEnabled;
        if (ImGui.Checkbox("Arrival nudge - one chat line when a garden needs you", ref nudge))
        {
            configuration.NudgeEnabled = nudge;
            configuration.Save();
        }

        // The nudge is the plugin's only unprompted line, so the name it announces itself
        // under belongs to the player, not to a string literal in the derivation.
        using (ImRaii.Disabled(!configuration.NudgeEnabled))
        {
            var label = configuration.NudgeLabel;
            ImGui.SetNextItemWidth(140f);
            ImGui.InputText("Nudge prefix", ref label, 24);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                configuration.NudgeLabel = label.Trim();
                configuration.Save();
            }
        }

        var twelveHour = configuration.TwelveHourClock;
        if (ImGui.Checkbox("12-hour clock - ripe windows read 8:45am, not 08:45", ref twelveHour))
        {
            configuration.TwelveHourClock = twelveHour;
            configuration.Save();
            BalambGarden.Engine.Derivations.WindowFormat.TwelveHourClock = twelveHour;
        }

        var trail = configuration.TrailEnabled;
        if (ImGui.Checkbox("Debug trail - append receipts to trail.jsonl", ref trail))
        {
            configuration.TrailEnabled = trail;
            configuration.Save();
        }

        var autoFill = configuration.AutoFillPicker;
        if (ImGui.Checkbox("Fill the planting picker for me - soil, seed, Confirm", ref autoFill))
        {
            configuration.AutoFillPicker = autoFill;
            configuration.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "The chain clicks the two slots and picks the soil and seed you chose.\n"
                + "If anything about the picker isn't what it expects, it stops clicking and\n"
                + "waits for you exactly like it used to - the run keeps going either way.\n"
                + "The sow confirmation is still read and checked before anything is planted.");
    }
}
