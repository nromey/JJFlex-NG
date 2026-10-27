using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Stage one of transmit audio, as controls: the radio's Mic Gain when its own
/// jack feeds the transmitter, and this computer's Windows input level when the
/// computer does. Only one of the two is ever visible, because only one of them
/// does anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation, hosted in two windows (#835).</b> The Audio Workshop's
/// Microphone section and the Check your audio dialog both carry these, and both
/// are the SAME settings: Mic Gain is the radio's <c>MicGain</c>, and the Windows
/// level is the endpoint volume Windows Sound settings moves. Nothing here keeps
/// a copy of either; each window binds its own controls to the one backing
/// value, so a nudge in one shows up in the other on its next poll. Until #835
/// this code lived inside the Workshop, and the gain sat on Hear Yourself
/// because that was where the check was (it moved there on 2026-08-25 so the
/// adjust-and-listen loop never crossed a category).
/// </para>
/// <para>
/// <b>The PC level is matched through Core Audio only.</b> The device is named by
/// the SAVED selection in audioDevices.xml and matched by
/// <see cref="WindowsMicLevel.TryFindByName"/>. Enumerating PortAudio from a
/// dialog while a radio connection may be live risks disturbing the streams that
/// connection depends on, so nothing here runs a Pa_Initialize/Pa_Terminate cycle.
/// </para>
/// <para>
/// <b>The honest-failure shape.</b> When no confident match exists, the level
/// control is collapsed (out of the tab order) and the note takes its place in
/// the ring carrying the reason. Silently moving some other microphone's level
/// would be far worse than offering nothing: the operator cannot glance at the
/// screen to catch it.
/// </para>
/// </remarks>
internal sealed class TransmitGainControls
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<FlexBase?> _rig;
    private readonly Func<bool> _polling;
    private readonly Func<string?> _audioDevicesPath;
    private WindowsMicLevel? _pcMicLevel;

    /// <summary>When audioDevices.xml was last written, as of the last bind.</summary>
    private DateTime _boundFileStamp;

    private DateTime DeviceFileStamp()
    {
        try
        {
            string? path = _audioDevicesPath();
            return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path)
                ? System.IO.File.GetLastWriteTimeUtc(path) : default;
        }
        catch (Exception) { return default; }
    }

    /// <summary>The radio's Mic Gain. Collapsed while this computer is the source.</summary>
    public ValueFieldControl MicGain { get; }

    /// <summary>The Windows input level for this computer's chosen microphone. Shown only while the computer is the source and a confident match exists.</summary>
    public ValueFieldControl PcLevel { get; }

    /// <summary>
    /// The always-current sentence under the PC level: which Windows device the
    /// control actually moves, with mute leading when it applies. On the failure
    /// paths it takes the control's place in the tab ring and carries the reason.
    /// </summary>
    public TextBox PcLevelNote { get; }

    /// <summary>True while the radio's transmit source is this computer and the Windows level is standing in for Mic Gain.</summary>
    public bool PcSourceActive { get; private set; }

    /// <param name="dispatcher">The host window's dispatcher; Core Audio calls back on a worker thread.</param>
    /// <param name="rig">The host's current radio, read at the moment of each write.</param>
    /// <param name="polling">The host's "I am writing controls from radio state" flag, so a poll never writes back to the radio.</param>
    /// <param name="audioDevicesPath">Where audioDevices.xml lives, read at each bind.</param>
    public TransmitGainControls(Dispatcher dispatcher, Func<FlexBase?> rig, Func<bool> polling,
        Func<string?> audioDevicesPath)
    {
        _dispatcher = dispatcher;
        _rig = rig;
        _polling = polling;
        _audioDevicesPath = audioDevicesPath;
        lock (Live) Live.Add(this);

        MicGain = new ValueFieldControl();
        MicGain.Setup("Mic Gain", 0, 100, 1);
        MicGain.ValueChanged += (s, v) =>
        {
            var r = _rig();
            if (r != null && !_polling())
            {
                r.MicGain = v;
                ScreenReaderOutput.Speak(Lexicon.Get("audio.tx.mic_gain", ("value", v)), VerbosityLevel.Terse);
            }
        };
        JJFlexHelp.SetText(MicGain,
            "How hard the radio listens to its microphone jack. Run a mic "
            + "check, speak the way you actually operate, and nudge this "
            + "until the verdict says Good. Hot or Clipping means come down; "
            + "Quiet means come up. Small steps — a few points at a time.");

        // The PC-source stand-in for Mic Gain (Track PC Gain, 2026-08-13). Noel:
        // "why not still have computer mic adjustment available where mic gain
        // is when PC audio is selected." Stage one lives on the computer when
        // the computer is the source.
        PcLevel = new ValueFieldControl();
        PcLevel.Setup("Windows Input Level", 0, 100, 1);
        PcLevel.Visibility = Visibility.Collapsed;
        PcLevel.IsEnabled = false;
        PcLevel.ValueChanged += (s, v) =>
        {
            var level = _pcMicLevel;
            if (level == null) return;
            try { level.Percent = v; }
            catch (Exception ex) { PcLevelFailed(ex); }
            // No app speech here, deliberately: the control announces its
            // own value on every adjustment, and repeating it would be the
            // same double-speak the Audio Devices sliders were built without.
        };
        JJFlexHelp.SetText(PcLevel,
            "Stage one of your transmit audio when this computer's mic is the "
            + "source: Windows' own capture level for that microphone. Set it "
            + "with the mic check the same way as Mic Gain — capture cleanly "
            + "here first, then let the radio's Processing controls shape the "
            + "result.");

        // Read-only EDIT rather than a label: focusable, review-readable, and the
        // screen reader's own read-current-control command speaks it.
        PcLevelNote = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2),
            MinWidth = 300,
            Visibility = Visibility.Collapsed
        };
    }

    /// <summary>
    /// Refresh from the radio. Call inside the host's polling guard. Returns true
    /// when the source flipped between the jack and this computer, so the host
    /// can follow with whatever else hangs on the source (the Workshop hides Mic
    /// Boost and Mic Bias with Mic Gain).
    /// </summary>
    /// <param name="fallbackFocus">Where focus goes when the gain it stood on vanishes and nothing better is visible.</param>
    public bool Poll(FlexBase rig, Action? fallbackFocus)
    {
        MicGain.Value = rig.MicGain;

        bool pcSource = string.Equals(rig.MicSource, "PC", StringComparison.OrdinalIgnoreCase);
        if (pcSource == PcSourceActive)
        {
            // The microphone can also be changed where neither window hears
            // about it (Audio Devices from the Audio menu). The saved choice is
            // a file, so its write time says when to rebind: one stat per poll.
            if (pcSource && DeviceFileStamp() != _boundFileStamp) BindPcLevel();
            return false;
        }
        PcSourceActive = pcSource;

        // Where focus stands is asked BEFORE anything hides. WPF pulls keyboard
        // focus off a collapsing element on a later dispatcher pass.
        bool focusWasOnJackGain = MicGain.IsKeyboardFocusWithin;
        bool focusWasOnPcLevel = PcLevel.IsKeyboardFocusWithin || PcLevelNote.IsKeyboardFocusWithin;

        MicGain.Visibility = pcSource ? Visibility.Collapsed : Visibility.Visible;
        if (pcSource)
        {
            BindPcLevel();
            // Never leave a hidden control holding focus — and the operator
            // standing on Mic Gain was adjusting their transmit gain, so hand
            // them the gain that now applies.
            if (focusWasOnJackGain)
            {
                if (PcLevel.IsEnabled && PcLevel.Visibility == Visibility.Visible) PcLevel.Focus();
                else if (PcLevelNote.Visibility == Visibility.Visible) PcLevelNote.Focus();
                else fallbackFocus?.Invoke();
            }
        }
        else
        {
            HidePcLevel();
            if (focusWasOnPcLevel && !MicGain.Focus()) fallbackFocus?.Invoke();
        }
        return true;
    }

    /// <summary>
    /// The Audio Devices picker may just have changed which microphone is chosen.
    /// If the Windows level is standing in right now, follow the choice rather
    /// than keep moving the previous device's level.
    /// </summary>
    public void DeviceSelectionMayHaveChanged()
    {
        DeviceChangesSeen++;
        if (PcSourceActive) BindPcLevel();
    }

    /// <summary>How many device-change notices this binding has had. For the test that proves a notice reaches every live binding.</summary>
    internal int DeviceChangesSeen { get; private set; }

    /// <summary>
    /// Every live binding, so a microphone change reaches all of them (fix
    /// round 1). The two windows share this implementation but each holds its
    /// own endpoint binding, and until this a change made in the Workshop told
    /// only the Workshop's, leaving Check your audio's level moving the
    /// previous microphone.
    /// </summary>
    private static readonly System.Collections.Generic.List<TransmitGainControls> Live = new();

    /// <summary>
    /// The microphone chosen on this computer may have changed: tell every
    /// live binding. <paramref name="origin"/>, the binding in the window where
    /// the change was made, is told first and at once; the others on their own
    /// dispatchers.
    /// </summary>
    internal static void MicrophoneChoiceMayHaveChanged(TransmitGainControls? origin)
    {
        TransmitGainControls[] all;
        lock (Live) all = Live.ToArray();
        origin?.DeviceSelectionMayHaveChanged();
        foreach (var binding in all)
        {
            if (ReferenceEquals(binding, origin)) continue;
            if (binding._dispatcher.CheckAccess()) binding.DeviceSelectionMayHaveChanged();
            else binding._dispatcher.BeginInvoke(new Action(binding.DeviceSelectionMayHaveChanged));
        }
    }

    /// <summary>
    /// The window is closing: let go of the endpoint and stop hearing about
    /// microphone changes. Both hosts call this from their Closed handler.
    /// </summary>
    public void Dispose()
    {
        lock (Live) Live.Remove(this);
        Release();
    }

    /// <summary>
    /// Bind the Windows input level to the computer's chosen microphone, or show
    /// the focusable note with the reason no confident match exists.
    /// </summary>
    private void BindPcLevel()
    {
        Release();
        _boundFileStamp = DeviceFileStamp();

        string? name = null;
        int savedHostApiTypeId = -1;
        try
        {
            string? path = _audioDevicesPath();
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                var devices = new JJPortaudio.Devices(path);
                devices.LoadSavedSelection();
                name = devices.InputDevice?.Name;
                savedHostApiTypeId = devices.InputDevice?.hostApiTypeId ?? -1;
            }
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine(
                "Transmit gain: could not read the chosen input device for the level control — "
                + ex.Message, System.Diagnostics.TraceLevel.Warning);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            // DRAFT PROSE (#835): awaiting Noel. "Above" was true only in the
            // Workshop; this sentence now also sits in Check your audio.
            ShowPcLevelUnavailable(
                "No microphone has been chosen on this computer yet. Pick one in Audio "
                + "Devices, on the Audio menu, and its Windows input level will appear here.");
            return;
        }

        var level = WindowsMicLevel.TryFindByName(name, savedHostApiTypeId, out string whyNot);
        if (level == null)
        {
            ShowPcLevelUnavailable(whyNot);
            return;
        }

        _pcMicLevel = level;
        level.VolumeChanged += OnPcLevelVolumeChanged;

        try
        {
            PcLevel.SuppressEvents = true;
            try { PcLevel.Value = (int)Math.Round(level.Percent); }
            finally { PcLevel.SuppressEvents = false; }
            PcLevel.IsEnabled = true;
            PcLevel.Visibility = Visibility.Visible;
            RefreshPcLevelNote();
        }
        catch (Exception ex)
        {
            // Matched and then gone — a device can vanish between the match and
            // the first read. The failure shape is the same honest one.
            PcLevelFailed(ex);
        }
    }

    private void ShowPcLevelUnavailable(string reason)
    {
        Release();
        bool hadFocus = PcLevel.IsKeyboardFocusWithin;
        PcLevel.IsEnabled = false;
        PcLevel.Visibility = Visibility.Collapsed;
        SetPcLevelNoteText(reason);
        PcLevelNote.Visibility = Visibility.Visible;
        // A mid-use failure lands the operator on the explanation, not in a void.
        if (hadFocus) PcLevelNote.Focus();
    }

    private void HidePcLevel()
    {
        Release();
        PcLevel.IsEnabled = false;
        PcLevel.Visibility = Visibility.Collapsed;
        PcLevelNote.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Let go of the Core Audio endpoint. Safe in any state; every host calls it
    /// on close, because a COM endpoint and its volume callback must never
    /// outlive the window that subscribed them.
    /// </summary>
    public void Release()
    {
        var level = _pcMicLevel;
        _pcMicLevel = null;
        if (level == null) return;
        level.VolumeChanged -= OnPcLevelVolumeChanged;
        level.Dispose();
    }

    /// <summary>
    /// Core Audio raises volume notifications on a COM worker thread, for
    /// external changes and for our own writes echoing back alike. Hop to the
    /// UI thread and re-read; the echo moves nothing.
    /// </summary>
    private void OnPcLevelVolumeChanged()
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            var level = _pcMicLevel;
            if (level == null) return;
            try
            {
                PcLevel.SuppressEvents = true;
                try { PcLevel.Value = (int)Math.Round(level.Percent); }
                finally { PcLevel.SuppressEvents = false; }
                RefreshPcLevelNote();
            }
            catch (Exception ex)
            {
                PcLevelFailed(ex);
            }
        }));
    }

    private void RefreshPcLevelNote()
    {
        var level = _pcMicLevel;
        if (level == null) return;

        bool muted = level.Muted;
        float boost = level.HasBoost ? level.BoostDb : 0f;

        string text;
        if (muted)
        {
            // DRAFT PROSE (#835): awaiting Noel. Same reason: no "above".
            text = level.FriendlyName + " is muted in Windows — a mute wins over every level "
                + "slider. Unmute it in Audio Devices, on the Audio menu, or in Windows Sound settings.";
        }
        else
        {
            text = level.FollowsWindowsDefault
                ? "This device follows your Windows default microphone. Right now that is "
                  + level.FriendlyName + ", and the level above moves its Windows input level."
                : "The level above moves the Windows input level for " + level.FriendlyName
                  + " — the same level as Windows Sound settings.";
            if (level.HasBoost && boost > 0f)
            {
                // A boost left at +30 dB is the classic cause of a pinned reading
                // nothing visible explains, and the modern Settings app does not
                // show boost at all.
                // DRAFT PROSE (#835): awaiting Noel.
                text += $" Microphone Boost is turned up, plus {boost:F0} dB — if you are "
                    + "coming in hot, lower the boost in Audio Devices first.";
            }
        }
        SetPcLevelNoteText(text);
        PcLevelNote.Visibility = Visibility.Visible;
    }

    private void SetPcLevelNoteText(string text)
    {
        // Assign only on change: volume notifications arrive in bursts, and
        // rewriting identical text would reset a review position for nothing.
        if (PcLevelNote.Text == text) return;
        PcLevelNote.Text = text;
        AutomationProperties.SetName(PcLevelNote, text);
    }

    private void PcLevelFailed(Exception ex)
    {
        JJTrace.Tracing.TraceLine("Transmit gain: Windows input level failed — " + ex.Message,
            System.Diagnostics.TraceLevel.Error);
        // DRAFT PROSE (#835): awaiting Noel.
        ShowPcLevelUnavailable(
            "Windows stopped answering for this microphone — it may have been unplugged. "
            + "Choose a device in Audio Devices, on the Audio menu, or adjust the level in "
            + "Windows Sound settings.");
    }
}
