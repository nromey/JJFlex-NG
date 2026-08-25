using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using JJPortaudio;
using JJTrace;
using Radios;
using Radios.Fixer;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// The five fixes the transmit stage set's audio-setup findings can offer,
/// as host-supplied <see cref="FixerFixAction"/> delegates for
/// <c>TransmitStageSet.Hosts</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every action here changes the operator's live configuration, so every
/// action verifies before it reports.</b> The pattern is the same five times:
/// change exactly the one thing the finding described, read the changed thing
/// back from where the analyzer will read it — the settings file on disk, or
/// the radio session — and build <see cref="FixerFixOutcome.WhatItBecame"/>
/// from the read-back, never from the intent. "I called the setter" and "the
/// setting changed" are different claims, and only the second one goes in the
/// record.
/// </para>
/// <para>
/// <b>Never throws.</b> A failed fix is an outcome; the run engine records it
/// and the operator reads why. <see cref="Guarded"/> wraps every body, and a
/// failure path says plainly that nothing (or exactly what) was changed.
/// </para>
/// <para>
/// <b>Safe to press twice.</b> A state that already holds is reported as
/// already holding — "nothing needed changing" — never claimed as a change.
/// </para>
/// <para>
/// <b>Nothing here transmits.</b> These touch the audio configuration, the
/// PC-audio switch and the microphone-profile selection; none of them keys a
/// radio, and none goes anywhere near the transmit gate.
/// </para>
/// <para>
/// The choices with more than one honest answer — which endpoint counts as a
/// device's twin on another host API, what an already-set state is called,
/// what an unconfirmed radio write is called — live in
/// <see cref="FixerFixDecisions"/>, pure, so each is testable without a sound
/// card or a radio.
/// </para>
/// </remarks>
internal static class FixerFixActions
{
    // ================================================================
    // The factories the coordinator wires into TransmitStageSet.Hosts
    // ================================================================

    /// <summary>
    /// Fix for <c>AudioSetupCheck.MmeInUse</c>: move the audio system to
    /// WASAPI, carrying the saved devices onto their WASAPI endpoints.
    /// </summary>
    /// <remarks>
    /// The analyzer reads the host API from where the SAVED DEVICES resolve
    /// (<see cref="Devices.FindLive"/>), not from the machine-scope selector
    /// alone — so flipping the selector while the microphone entry still
    /// binds under MME would report success and change nothing the analyzer
    /// can see. The move is therefore atomic around the microphone: if the
    /// saved input device has no usable WASAPI endpoint, nothing at all is
    /// changed and the outcome says to pick a WASAPI microphone instead. The
    /// playback device is best-effort — audio can honestly run WASAPI-input
    /// with the playback left where it was, and the record says so when it is.
    /// </remarks>
    public static FixerFixAction SwitchToWasapi()
        => Guarded(AudioSetupCheck.FixSwitchToWasapi, DoSwitchToWasapi);

    /// <summary>
    /// Fix for <c>AudioSetupCheck.NoInputSelected</c>: choose and save the
    /// input this host nominates when none is chosen.
    /// </summary>
    /// <remarks>
    /// Built on <see cref="Devices.AdoptSystemDefault"/> deliberately — the
    /// same pick loop the analyzer's suggestion mirrored read-only, and here
    /// its write-through is exactly what was asked for: a pressed fix button
    /// is the one place "looking" is supposed to change things. The saved
    /// choice is then read back from disk before it is reported. The pick is
    /// re-made at press time, so if devices changed since the finding was
    /// shown, the record names what was actually chosen.
    /// </remarks>
    public static FixerFixAction UseSuggestedInput()
        => Guarded(AudioSetupCheck.FixUseSuggestedInput, DoUseSuggestedInput);

    /// <summary>
    /// Fix for <c>AudioSetupCheck.PcAudioOff</c>: turn the computer-audio
    /// path to the radio on.
    /// </summary>
    public static FixerFixAction EnablePcAudio(Func<FlexBase?> radio)
        => Guarded(AudioSetupCheck.FixEnablePcAudio, () => DoEnablePcAudio(radio));

    /// <summary>
    /// Fix for <c>AudioSetupCheck.MicProfileEmptyFinding</c>: select a mic
    /// profile the radio already has, so the transmit-audio DSP chain exists.
    /// </summary>
    /// <remarks>
    /// Uses <c>FlexBase.SelectMicProfileIfPresent</c> — the deliberately
    /// NON-creating variant — because filling an empty selection and creating
    /// a profile on somebody's radio are different acts and only the first
    /// was offered. The selection travels through the radio session's command
    /// queue, so this waits, bounded, until the session reports a selection
    /// rather than trusting that the call took.
    /// </remarks>
    public static FixerFixAction FillMicProfile(Func<FlexBase?> radio)
        => Guarded(AudioSetupCheck.FixFillMicProfile, () => DoFillMicProfile(radio));

    /// <summary>
    /// Fix for <c>AudioSetupCheck.ConfigOpenMismatch</c>: bring what is in
    /// use back to what the configuration says, and reopen the running audio
    /// path so the two agree.
    /// </summary>
    /// <remarks>
    /// The configuration is the authority here — that is what the finding's
    /// button promised ("Reopen audio with the configured device"). The saved
    /// entries are re-aligned onto the configured audio system, verified from
    /// disk, and then, when the radio's PC-audio path is open, it is closed
    /// and reopened so the running streams re-read the file the way they do
    /// at every start (<c>remoteAudioProc</c> resolves its devices fresh each
    /// time it comes up). When the configured device genuinely cannot open as
    /// configured — unplugged, or gone from that host API — nothing is
    /// changed and the outcome says exactly which half refused.
    /// </remarks>
    public static FixerFixAction ReopenConfiguredAudio(Func<FlexBase?> radio)
        => Guarded(AudioSetupCheck.FixReopenConfiguredAudio, () => DoReopenConfiguredAudio(radio));

    // ================================================================
    // switch-to-wasapi
    // ================================================================

    private static FixerFixOutcome DoSwitchToWasapi()
    {
        int preApi = Devices.SelectedHostApiTypeId;
        Devices? devices = OpenSavedSelection(out string whyNot);
        if (devices == null)
            return NothingChanged(preApi, "Nothing was changed: " + whyNot + ".");

        if (!HostApiPresent(Devices.WasapiTypeId))
            return NothingChanged(preApi, "Nothing was changed: WASAPI is not available on this "
                + "computer, so there is nothing to switch to.");

        Devices.DeviceInfo? liveIn = Devices.FindLive(devices.InputDevice);
        Devices.DeviceInfo? liveOut = Devices.FindLive(devices.OutputDevice);

        if (FixerFixDecisions.AlreadyOnApi(devices.SavedHostApiTypeId,
                                           liveIn?.HostApiTypeId, liveOut?.HostApiTypeId,
                                           Devices.WasapiTypeId))
        {
            // The file's selector already says WASAPI, so the selection the
            // load applied is the one that was in force. Nothing to restore.
            return FixerFixOutcome.Done("Windows WASAPI already"
                + (liveIn != null ? " — your microphone, \"" + liveIn.Name + "\", runs under it" : "")
                + ". Nothing needed changing.");
        }

        // Decide every move BEFORE writing anything, so a device with no
        // WASAPI endpoint refuses the whole switch instead of leaving a
        // half-moved configuration behind.
        Devices.DeviceInfo? inTwin = FixerFixDecisions.OnApi(liveIn, Devices.WasapiTypeId);
        Devices.DeviceInfo? outTwin = FixerFixDecisions.OnApi(liveOut, Devices.WasapiTypeId);

        if (liveIn != null && inTwin == null)
            return NothingChanged(preApi, "Nothing was changed: your microphone, \"" + liveIn.Name
                + "\", is not available under WASAPI on this computer, and switching would leave "
                + "it behind. Choose a WASAPI microphone in the full device picker instead.");

        int applied = Devices.ApplyHostApiSelection(Devices.WasapiTypeId);
        if (applied != Devices.WasapiTypeId)
            return NothingChanged(preApi, "Nothing was changed: the audio system would not switch "
                + "to WASAPI — it offered " + Devices.NameOfHostApi(applied) + " instead.");

        // Rewrite a saved entry only when its recorded host API is not
        // already WASAPI; SetConfiguredDevice also stamps the selector.
        if (inTwin != null && devices.InputDevice != null
            && devices.InputDevice.hostApiTypeId != Devices.WasapiTypeId)
            devices.SetConfiguredDevice(Devices.DeviceTypes.input, inTwin);
        if (outTwin != null && devices.OutputDevice != null
            && devices.OutputDevice.hostApiTypeId != Devices.WasapiTypeId)
            devices.SetConfiguredDevice(Devices.DeviceTypes.output, outTwin);
        devices.SaveHostApiSelection();   // covers the no-device-rewritten path

        // ---- verify from disk, where the analyzer will read ----
        var check = new Devices(AudioDevicesFile);
        if (!check.LoadSavedSelection() || check.SavedHostApiTypeId != Devices.WasapiTypeId)
            return FixerFixOutcome.Failed("The switch was written but did not read back — the "
                + "settings file still does not say WASAPI, so treat the audio system as unchanged.");

        Devices.DeviceInfo? vIn = Devices.FindLive(check.InputDevice);
        if (check.InputDevice != null
            && (vIn == null || vIn.HostApiTypeId != Devices.WasapiTypeId))
            return FixerFixOutcome.Failed("The audio system reads back as WASAPI, but your "
                + "microphone, \"" + check.InputDevice.Name + "\", does not resolve under it, so "
                + "the switch is not complete. Run this stage again to see where things stand.");

        Devices.DeviceInfo? vOut = Devices.FindLive(check.OutputDevice);
        var sb = new StringBuilder("Windows WASAPI.");
        if (vIn != null)
            sb.Append(" Your microphone, \"").Append(vIn.Name).Append("\", is under it.");
        if (check.OutputDevice != null)
        {
            if (vOut != null && vOut.HostApiTypeId == Devices.WasapiTypeId)
                sb.Append(" So is your playback device, \"").Append(vOut.Name).Append("\".");
            else
                sb.Append(" Your playback device, \"").Append(check.OutputDevice.Name)
                  .Append("\", is not available under WASAPI and was left as it was.");
        }
        if (check.InputDevice == null && check.OutputDevice == null)
            sb.Append(" No devices were saved, so none needed moving.");
        return FixerFixOutcome.Done(sb.ToString());
    }

    // ================================================================
    // use-suggested-input
    // ================================================================

    private static FixerFixOutcome DoUseSuggestedInput()
    {
        int preApi = Devices.SelectedHostApiTypeId;
        Devices? devices = OpenSavedSelection(out string whyNot);
        if (devices == null)
            return NothingChanged(preApi, "Nothing was changed: " + whyNot + ".");

        // Already chosen and present: say so, change nothing.
        Devices.Device? existing = devices.GetConfiguredDevice(Devices.DeviceTypes.input);
        if (existing != null)
        {
            Devices.DeviceInfo? row = Devices.FindLive(existing);
            return FixerFixOutcome.Done("A microphone was already chosen: \"" + existing.Name
                + "\"" + OnApiClause(row?.HostApiName) + ". Nothing needed changing.");
        }

        // Chosen but unplugged is a DIFFERENT finding (the operator's), and
        // overwriting their saved microphone would fix more than this one
        // described.
        if (devices.IsSavedDeviceMissing(Devices.DeviceTypes.input, out string savedName))
            return NothingChanged(preApi, "Nothing was changed: a microphone is already chosen — \""
                + savedName + "\" — it is just not connected right now. Check its cable, or pick "
                + "another in the full device picker.");

        // A process that has never applied a host API would let the adoption
        // wander across APIs; resolve to the default the same way loading a
        // config file does.
        if (Devices.SelectedHostApiTypeId < 0)
            Devices.ApplyHostApiSelection(Devices.DefaultHostApiTypeId);

        Devices.Device? adopted = devices.AdoptSystemDefault(Devices.DeviceTypes.input);
        if (adopted == null)
            return NothingChanged(preApi, "Nothing was changed: no usable microphone was found on "
                + "this computer, so there was nothing to choose.");

        // ---- verify from disk ----
        var check = new Devices(AudioDevicesFile);
        if (!check.LoadSavedSelection() || check.InputDevice == null
            || !string.Equals(check.InputDevice.Name, adopted.Name, StringComparison.Ordinal))
            return FixerFixOutcome.Failed("The choice was written but did not read back from the "
                + "settings file, so treat no microphone as chosen. Pick one in the full device "
                + "picker.");

        Devices.DeviceInfo? vRow = Devices.FindLive(check.InputDevice);
        string onApi = !string.IsNullOrEmpty(vRow?.HostApiName) ? vRow!.HostApiName
                     : (adopted.hostApiName ?? "");
        return FixerFixOutcome.Done("Your microphone is now \"" + adopted.Name + "\""
            + OnApiClause(onApi) + ".");
    }

    // ================================================================
    // enable-pc-audio
    // ================================================================

    private static FixerFixOutcome DoEnablePcAudio(Func<FlexBase?>? radio)
    {
        FlexBase? rig = SafeRig(radio);
        if (rig == null)
            return FixerFixOutcome.Failed("No radio session is available right now, so PC audio "
                + "could not be turned on.");

        bool wasOn = rig.PCAudio;
        if (!wasOn) rig.PCAudio = true;
        bool nowOn = rig.PCAudio;   // read back, never assumed
        return FixerFixDecisions.PcAudioOutcome(wasOn, nowOn);
    }

    // ================================================================
    // fill-mic-profile
    // ================================================================

    /// <summary>
    /// How long to wait for the radio session to report a mic-profile
    /// selection after asking for one. The ask travels through the session's
    /// command queue, so the answer is usually one queue cycle away; the
    /// window matches the settle windows the connect-time silent-transmit
    /// check uses.
    /// </summary>
    private const int MicProfileSettleMs = 3000;

    private static FixerFixOutcome DoFillMicProfile(Func<FlexBase?>? radio)
    {
        FlexBase? rig = SafeRig(radio);
        if (rig == null)
            return FixerFixOutcome.Failed("No radio session is available right now, so the "
                + "microphone profile could not be set.");
        if (!rig.IsConnected)
            return FixerFixOutcome.Failed("The radio is not connected right now, so the "
                + "microphone profile could not be set.");

        string current = rig.CurrentMicProfileName;
        if (!string.IsNullOrEmpty(current))
            return FixerFixOutcome.Done("A microphone profile was already selected: \"" + current
                + "\". Nothing needed changing.");

        string candidate = rig.SuggestedMicProfileName;
        if (candidate.Length == 0)
            return FixerFixOutcome.Failed("The radio is not listing any microphone profiles to "
                + "choose from, so nothing was changed.");

        if (!rig.SelectMicProfileIfPresent(candidate))
            return FixerFixOutcome.Failed("The radio stopped listing \"" + candidate + "\" before "
                + "it could be selected, so nothing was changed.");

        // Bounded wait for the session to report a selection. Blocking the
        // calling thread briefly is the Fixer's deliberate shape — the stage
        // runner does the same — and three seconds bounds it.
        string seen = "";
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < MicProfileSettleMs)
        {
            seen = rig.CurrentMicProfileName;
            if (!string.IsNullOrEmpty(seen)) break;
            Thread.Sleep(100);
        }
        return FixerFixDecisions.MicProfileOutcome(candidate, seen);
    }

    // ================================================================
    // reopen-configured-audio
    // ================================================================

    private static FixerFixOutcome DoReopenConfiguredAudio(Func<FlexBase?>? radio)
    {
        int preApi = Devices.SelectedHostApiTypeId;
        Devices? devices = OpenSavedSelection(out string whyNot);
        if (devices == null)
            return NothingChanged(preApi, "Nothing was changed: " + whyNot + ".");

        Devices.Device? savedIn = devices.InputDevice;
        Devices.Device? savedOut = devices.OutputDevice;
        if (savedIn == null && savedOut == null)
            return NothingChanged(preApi, "Nothing was changed: no audio devices are saved to "
                + "reopen with. Choose them in the full device picker first.");

        // The configured audio system: the file's machine-scope selector when
        // it has one, else the entries' own recorded API — the same rule the
        // analyzer's "configured" half reads by.
        int targetApi = devices.SavedHostApiTypeId >= 0
            ? devices.SavedHostApiTypeId
            : (savedIn?.hostApiTypeId ?? savedOut?.hostApiTypeId ?? -1);

        if (targetApi >= 0)
        {
            int applied = Devices.ApplyHostApiSelection(targetApi);
            if (applied != targetApi)
                return NothingChanged(preApi, "Nothing was changed: the configured audio system, "
                    + Devices.NameOfHostApi(targetApi) + ", is not available on this computer "
                    + "right now.");
        }

        Devices.DeviceInfo? liveIn = Devices.FindLive(savedIn);
        Devices.DeviceInfo? liveOut = Devices.FindLive(savedOut);

        if (savedIn != null && liveIn == null)
            return NothingChanged(preApi, "Nothing was changed: the configured microphone, \""
                + savedIn.Name + "\", is not connected right now, so audio cannot be reopened "
                + "with it.");

        string outputNote = "";
        if (targetApi >= 0)
        {
            if (savedIn != null)
            {
                Devices.DeviceInfo? inOn = FixerFixDecisions.OnApi(liveIn, targetApi);
                if (inOn == null)
                    return NothingChanged(preApi, "Nothing was changed: the configured microphone, \""
                        + savedIn.Name + "\", is not available under "
                        + Devices.NameOfHostApi(targetApi)
                        + " right now, so audio cannot be reopened as configured.");
                if (!ReferenceEquals(inOn, liveIn))
                    devices.SetConfiguredDevice(Devices.DeviceTypes.input, inOn);
            }

            // Playback is best-effort: input is what the transmit stages
            // serve, and the record says when playback stayed put.
            if (savedOut != null && liveOut != null)
            {
                Devices.DeviceInfo? outOn = FixerFixDecisions.OnApi(liveOut, targetApi);
                if (outOn == null)
                    outputNote = " The playback device, \"" + savedOut.Name + "\", is not "
                        + "available under " + Devices.NameOfHostApi(targetApi)
                        + " and was left as it was.";
                else if (!ReferenceEquals(outOn, liveOut))
                    devices.SetConfiguredDevice(Devices.DeviceTypes.output, outOn);
            }
            else if (savedOut != null)
            {
                outputNote = " The playback device, \"" + savedOut.Name + "\", is not connected "
                    + "right now.";
            }
        }

        // ---- verify the configuration half from disk ----
        var check = new Devices(AudioDevicesFile);
        Devices.DeviceInfo? vIn = check.LoadSavedSelection()
            ? Devices.FindLive(check.InputDevice) : null;
        if (savedIn != null
            && (vIn == null || (targetApi >= 0 && vIn.HostApiTypeId != targetApi)))
            return FixerFixOutcome.Failed("The settings were re-aligned but did not read back as "
                + "configured; run the audio setup stage again to see where things stand.");

        // ---- reopen the running path, when there is one ----
        // The path resolves its devices fresh from audioDevices.xml every
        // time it starts, so closing and reopening it is exactly how the
        // running streams pick up the configuration.
        FlexBase? rig = SafeRig(radio);
        bool pathWasOn = false;
        try { pathWasOn = rig != null && rig.PCAudio; }
        catch { /* a torn-down session reads as no path */ }

        string configured = vIn != null
            ? "microphone \"" + vIn.Name + "\" on " + vIn.HostApiName
            : "the configured devices";

        if (rig != null && pathWasOn)
        {
            try { rig.PCAudio = false; }
            finally { rig.PCAudio = true; }
            if (!rig.PCAudio)
                return FixerFixOutcome.Failed("The settings now agree — " + configured + " — but "
                    + "the radio audio path did not come back after closing. Turn PC audio on to "
                    + "reopen it.");
            return FixerFixOutcome.Done("Audio was closed and reopened as configured: " + configured
                + "." + outputNote);
        }

        return FixerFixOutcome.Done("Audio now opens as configured: " + configured + "."
            + outputNote + " No radio audio path was open, so there was nothing running to "
            + "reopen; the next one to open uses this.");
    }

    // ================================================================
    // plumbing
    // ================================================================

    /// <summary>
    /// The one wrapper every action runs under: trace the attempt, never let
    /// an exception out, trace what the outcome was.
    /// </summary>
    private static FixerFixAction Guarded(string fixId, Func<FixerFixOutcome> body)
        => () =>
        {
            Tracing.TraceLine("FixerFixActions: attempting '" + fixId + "'", TraceLevel.Info);
            FixerFixOutcome outcome;
            try
            {
                outcome = body();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("FixerFixActions: '" + fixId + "' threw — " + ex,
                                  TraceLevel.Error);
                outcome = FixerFixOutcome.Failed("the fix failed part-way: " + ex.Message
                    + " Run the audio setup stage again to see where things stand.");
            }
            Tracing.TraceLine("FixerFixActions: '" + fixId + "' "
                + (outcome.Succeeded ? "done — " : "FAILED — ") + outcome.WhatItBecame,
                outcome.Succeeded ? TraceLevel.Info : TraceLevel.Warning);
            return outcome;
        };

    /// <summary>Same settings root every other audio store resolves through,
    /// so a run under JJFLEX_CONFIG_DIR stays inside its isolation.</summary>
    private static string AudioDevicesFile
        => Path.Combine(RadioConfig.AppDataRoot, "audioDevices.xml");

    /// <summary>
    /// Enumerate the system and load the saved selection for a fix that
    /// intends to write it. Null, with a reason, when either half failed —
    /// and an unreadable file is a refusal, never a blank slate: writing over
    /// a file that exists but would not parse would destroy the very settings
    /// a fix exists to repair.
    /// </summary>
    private static Devices? OpenSavedSelection(out string whyNot)
    {
        whyNot = "";
        Devices.EnumerationStatus status = Devices.Enumerate(out string message);
        if (status != Devices.EnumerationStatus.Ok)
        {
            whyNot = "the computer's audio devices could not be listed"
                + (string.IsNullOrEmpty(message) ? "" : " — " + message);
            return null;
        }

        var devices = new Devices(AudioDevicesFile);
        if (!devices.LoadSavedSelection())
        {
            whyNot = "the saved audio settings file could not be read, so it was left untouched";
            return null;
        }
        return devices;
    }

    /// <summary>
    /// A "nothing was changed" refusal that makes itself true: loading the
    /// file applies its host-API selection process-wide, so a fix that then
    /// declines must put the live selection back where it found it.
    /// </summary>
    private static FixerFixOutcome NothingChanged(int preApi, string why)
    {
        if (preApi >= 0 && Devices.SelectedHostApiTypeId != preApi)
            Devices.ApplyHostApiSelection(preApi);
        return FixerFixOutcome.Failed(why);
    }

    private static bool HostApiPresent(int typeId)
    {
        foreach (Devices.HostApi api in Devices.HostApis)
            if (api.TypeId == typeId) return true;
        return false;
    }

    private static FlexBase? SafeRig(Func<FlexBase?>? radio)
    {
        if (radio == null) return null;
        try { return radio(); }
        catch { return null; }
    }

    private static string OnApiClause(string? hostApiName)
        => string.IsNullOrEmpty(hostApiName) ? "" : ", on " + hostApiName;
}

/// <summary>
/// The fix actions' decisions, pure — no sound card, no radio, no file — so
/// every branch an operator can be shown is testable on its own.
/// </summary>
public static class FixerFixDecisions
{
    /// <summary>
    /// The endpoint of this device on the wanted host API, or null when it
    /// has none the engine can open.
    /// </summary>
    /// <remarks>
    /// A device that is already on the wanted API is returned as itself — a
    /// fix never moves a device that is already where it should be. Otherwise
    /// the answer comes from the device's enumeration group (the same
    /// hardware's endpoints across host APIs, MME's truncated names already
    /// folded in by <c>Devices.BuildGroups</c>), and only an endpoint the
    /// engine can actually open qualifies: offering an unopenable twin would
    /// trade a misreporting microphone for a dead one.
    /// </remarks>
    public static Devices.DeviceInfo? OnApi(Devices.DeviceInfo? resolved, int hostApiTypeId)
    {
        if (resolved == null) return null;
        if (Suits(resolved, hostApiTypeId)) return resolved;

        Devices.DeviceInfo owner = resolved.GroupOwner ?? resolved;
        if (Suits(owner, hostApiTypeId)) return owner;
        if (owner.Alternates != null)
        {
            foreach (Devices.DeviceInfo alt in owner.Alternates)
                if (Suits(alt, hostApiTypeId)) return alt;
        }
        return null;

        static bool Suits(Devices.DeviceInfo d, int api)
            => d.HostApiTypeId == api && !d.IsMissingSaved && d.UsableForRadioAudio;
    }

    /// <summary>
    /// Is the whole selection already on the wanted host API — the saved
    /// selector and every resolved device? Null device APIs mean "no such
    /// device saved", which cannot argue against.
    /// </summary>
    public static bool AlreadyOnApi(int savedSelectorApi, int? inputApi, int? outputApi,
                                    int targetApi)
        => savedSelectorApi == targetApi
        && (inputApi == null || inputApi == targetApi)
        && (outputApi == null || outputApi == targetApi);

    /// <summary>
    /// What the PC-audio fix reports, from the state before and the state
    /// read back after. Already-on is a success that claims no change;
    /// a read-back that stayed off is a failure, never silence.
    /// </summary>
    public static FixerFixOutcome PcAudioOutcome(bool wasOn, bool nowOn)
    {
        if (wasOn)
            return FixerFixOutcome.Done("PC audio was already on. Nothing needed changing.");
        if (nowOn)
            return FixerFixOutcome.Done("PC audio is now on — the network audio path between "
                + "this computer and the radio is starting.");
        return FixerFixOutcome.Failed("PC audio did not turn on — the radio session did not "
            + "accept the change, so audio from this computer still does not reach the radio.");
    }

    /// <summary>
    /// What the mic-profile fix reports, from what was asked for and what the
    /// radio session reported selected once the wait ended. An empty
    /// selection after the wait is a failure — a fix that cannot be read back
    /// did not succeed — and a different profile than the one asked for is
    /// reported as exactly what it is.
    /// </summary>
    public static FixerFixOutcome MicProfileOutcome(string askedFor, string nowSelected)
    {
        if (string.IsNullOrEmpty(nowSelected))
            return FixerFixOutcome.Failed("The radio was asked to load \"" + askedFor + "\" and "
                + "has not confirmed it — the selection still reads empty, so treat transmit "
                + "audio as still not set up.");
        if (string.Equals(nowSelected, askedFor, StringComparison.Ordinal))
            return FixerFixOutcome.Done("The microphone profile is now \"" + askedFor + "\".");
        return FixerFixOutcome.Done("The microphone profile is now \"" + nowSelected + "\" — not "
            + "the \"" + askedFor + "\" that was asked for, but the selection is no longer empty.");
    }
}
