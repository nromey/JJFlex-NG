using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Flex.Smoothlake.FlexLib;
using HamBands;
using JJFlexUpdater;
using JJTrace;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// Native Win32 HMENU menu bar for ShellForm.
/// Uses P/Invoke to create real Win32 menus that screen readers (JAWS/NVDA)
/// navigate correctly — ROLE_SYSTEM_MENUBAR / ROLE_SYSTEM_MENUITEM with no
/// collapse/expand noise. Replaces the managed MenuStrip which announced
/// "collapsed"/"expanded" identically to the old WPF Menu.
///
/// Sprint 13C/13D: Shared handlers for DSP toggles, value adjustments, and
/// filter controls. Used by both Classic (ScreenFields/Operations) and
/// Modern (Slice/Filter/Audio) menu bars.
///
/// Ampersand carve-out (class-wide, #40): explicit &amp; mnemonics are FINE
/// in this file. The "no ampersands in menu labels" accessibility guideline
/// was written for WinForms MenuStrip, where screen readers read the literal
/// character; native Win32 menus render &amp; as an underlined access key and
/// NVDA reads the label cleanly. Give sibling items UNIQUE mnemonics or none
/// at all — one item with a mnemonic among bare siblings is the inconsistency
/// to avoid, not the mnemonic itself.
/// </summary>
public class NativeMenuBar : IDisposable
{
    #region Win32 P/Invoke

    [DllImport("user32.dll")]
    private static extern IntPtr CreateMenu();

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool SetMenu(IntPtr hWnd, IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool DrawMenuBar(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

    [DllImport("user32.dll")]
    private static extern uint CheckMenuItem(IntPtr hMenu, uint uIDCheckItem, uint uCheck);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ModifyMenuW(IntPtr hMenu, uint uPosition, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    private const uint MF_STRING = 0x0000;
    private const uint MF_POPUP = 0x0010;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_GRAYED = 0x0001;
    private const uint MF_BYCOMMAND = 0x0000;
    private const uint MF_CHECKED = 0x0008;
    private const uint MF_UNCHECKED = 0x0000;

    public const int WM_COMMAND = 0x0111;
    public const int WM_INITMENUPOPUP = 0x0117;

    #endregion

    private readonly MainWindow _window;
    private IntPtr _hwnd;
    private IntPtr _currentMenuBar;
    private readonly Dictionary<int, Action> _handlers = new();

    /// <summary>
    /// What each command id is CALLED, so the trace can name what ran.
    /// </summary>
    /// <remarks>
    /// Added 2026-08-25. Until now only the not-yet-implemented stubs traced
    /// anything, so when a burst of commands fired unbidden on the operator's
    /// screen the trace recorded the menu being REBUILT eight times and not one
    /// command being RUN. We could watch the machinery and not the actions.
    /// Cleared and rebuilt with <see cref="_handlers"/> so a name can never
    /// belong to a different build's id.
    /// </remarks>
    private readonly Dictionary<int, string> _itemNames = new();
    // Items with dynamic checkmarks: menu item ID → (parent HMENU, state getter)
    private readonly List<(IntPtr popup, int id, Func<bool> stateGetter, string baseText, bool enabled)> _checkItems = new();
    /// <summary>
    /// Ids of check items that belong to a MUTUALLY EXCLUSIVE group — one of
    /// N is on, the rest are off — rather than being independent toggles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These get the checkmark and NOT the ": On" / ": Off" text suffix that
    /// <see cref="HandleInitMenuPopup"/> writes onto a toggle. On a toggle the
    /// suffix is the whole state; in a group of ten modes it is nine "Off"s
    /// the operator arrows through to reach one "On", which is exactly the
    /// noise that teaches somebody to stop listening. The checkmark itself is
    /// what a screen reader reports as "checked", and in a radio group that is
    /// the complete answer (#311).
    /// </para>
    /// <para>
    /// It also cannot be written safely on these items: a mode row's text is
    /// <c>"USB\tAlt+U"</c>, and appending to that puts the state inside the
    /// accelerator column.
    /// </para>
    /// </remarks>
    private readonly HashSet<int> _radioGroupItems = new();
    // Top-level popup handle → menu name (for screen reader announcement on open)
    private readonly Dictionary<IntPtr, string> _popupNames = new();
    /// <summary>
    /// Next menu command id. Starts at 1000 and KEEPS CLIMBING across rebuilds
    /// — it is deliberately not reset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It used to be reset to 1000 on every rebuild, which looks like tidiness
    /// and is a trap. The menu is rebuilt on slice and connection events, from
    /// a dispatcher post, WHILE the operator is using it — eight times in one
    /// session on 2026-08-25, with a different item count every time. A
    /// WM_COMMAND already in the Windows queue when a rebuild happens carries
    /// an id from the OLD menu; with the counter reset, that id is a perfectly
    /// valid key in the NEW dictionary and invokes whatever command now owns
    /// the number.
    /// </para>
    /// <para>
    /// Letting it climb makes a stale id MISS the dictionary, so the command
    /// does nothing — a harmless failure instead of a harmful one. The reset
    /// was what converted "does nothing" into "does something else".
    /// </para>
    /// </remarks>
    private int _nextId = FirstCommandId;

    /// <summary>Below the Windows range menus reserve, above nothing.</summary>
    private const int FirstCommandId = 1000;

    /// <summary>
    /// WM_COMMAND ids are masked to 16 bits, so the counter cannot climb
    /// forever. At roughly 200 items per rebuild this is some hundreds of
    /// rebuilds away — but a wrap would silently reintroduce the collision
    /// above, so it happens deliberately and says so rather than arriving by
    /// arithmetic.
    /// </summary>
    private const int LastCommandId = 60000;

    // Feature gate state (persisted across rebuilds)
    private bool _diversityAvailable;
    private bool _escAvailable;

    // Slice event subscription tracking (to trigger menu rebuild on slice add/remove)
    private FlexBase? _subscribedRig;

    // Teardown guard (QB Track A, 2026-08-07, belt-and-suspenders from the
    // 8/05 ActiveSlice sweep): slice/connection events queue RebuildCurrentMenu
    // through the dispatcher, so a rebuild can land AFTER Dispose has
    // destroyed the menu bar — recreating Win32 menus against a dying window.
    private bool _disposed;

    public NativeMenuBar(MainWindow window)
    {
        _window = window;
    }

    private FlexBase? Rig => _window.RigControl;

    /// <summary>
    /// Filter presets for current operator. Set by ApplicationEvents.vb during radio connect.
    /// </summary>
    public FilterPresets? FilterPresets { get; set; }

    /// <summary>
    /// Attach to the form's HWND and apply the initial menu using the current UI mode.
    /// Call from ShellForm.HandleCreated.
    /// </summary>
    public void AttachTo(IntPtr hwnd)
    {
        _hwnd = hwnd;
        ApplyUIMode(_window.ActiveUIMode);
    }

    /// <summary>
    /// Rebuild the menu bar for the specified UI mode.
    /// Destroys the old menu bar and creates a fresh one with only that mode's menus.
    /// </summary>
    public void ApplyUIMode(MainWindow.UIMode mode)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;

        Tracing.TraceLine($"NativeMenuBar.ApplyUIMode: {mode}", TraceLevel.Info);

        // Destroy old menu bar (cascades to all submenus)
        var oldMenu = _currentMenuBar;

        // Reset handler tracking for fresh build
        _handlers.Clear();
        _itemNames.Clear();
        _checkItems.Clear();
        _radioGroupItems.Clear();
        _popupNames.Clear();

        // NOT reset — see the remarks on _nextId. Wrapped only at the 16-bit
        // ceiling, loudly, because a wrap brings the stale-id collision back
        // for one rebuild and that should never be a silent event.
        if (_nextId > LastCommandId)
        {
            Tracing.TraceLine("NativeMenuBar: command ids wrapped to "
                + FirstCommandId + " after " + _nextId
                + " — a command posted before this rebuild could now reach the "
                + "wrong handler", TraceLevel.Warning);
            _nextId = FirstCommandId;
        }

        // Build new menu bar for this mode
        _currentMenuBar = mode switch
        {
            MainWindow.UIMode.Logging => BuildLoggingMenuBar(),
            _ => BuildMenuBar()
        };

        // Swap: set new menu first, then destroy old
        SetMenu(_hwnd, _currentMenuBar);
        DrawMenuBar(_hwnd);

        if (oldMenu != IntPtr.Zero)
            DestroyMenu(oldMenu);

        // Subscribe to slice count changes so menu label stays accurate
        EnsureSliceEventSubscription();

        Tracing.TraceLine($"NativeMenuBar.ApplyUIMode: {mode} complete, {_handlers.Count} items", TraceLevel.Info);
    }

    private void EnsureSliceEventSubscription()
    {
        var rig = Rig;
        if (rig == _subscribedRig) return;
        if (_subscribedRig != null)
        {
            _subscribedRig.SliceCountChanged -= OnSliceCountChanged;
            _subscribedRig.ConnectionStateChanged -= OnConnectionStateChanged;
        }
        if (rig != null)
        {
            rig.SliceCountChanged += OnSliceCountChanged;
            rig.ConnectionStateChanged += OnConnectionStateChanged;
        }
        _subscribedRig = rig;
    }

    private void OnSliceCountChanged()
    {
        QueueMenuRebuild("slice count changed");
    }

    private void OnConnectionStateChanged(bool connected)
    {
        QueueMenuRebuild(connected ? "connected" : "disconnected");
    }

    /// <summary>
    /// Rebuild the current mode's menu bar (e.g., after radio connects and DSP is available).
    /// Called from MainWindow.SetupOperationsMenu(). Coalesced — see
    /// <see cref="QueueMenuRebuild"/>.
    /// </summary>
    public void RebuildCurrentMenu()
    {
        QueueMenuRebuild("explicit rebuild request");
    }

    /// <summary>True while a rebuild is posted and not yet run.</summary>
    private bool _rebuildQueued;

    /// <summary>
    /// Coalesce menu rebuilds into one Background-priority dispatcher pass.
    ///
    /// Sprint 42 Track D (#395). A connect fires this from several directions
    /// inside one second: PowerNowOn's explicit SetupOperationsMenu, a
    /// SliceCountChanged per arriving slice, and ConnectionStateChanged — the
    /// menu was measured being rebuilt eight times in one session on
    /// 2026-08-25, with the operator standing in the UI. Every one of those
    /// rebuilds produced an identical result to the last one in the burst,
    /// because each rebuilds from CURRENT state. So: first request posts one
    /// rebuild at Background priority (after pending input and focus events
    /// drain), and every further request before it runs rides along for free.
    /// The rebuild itself is unchanged — same ApplyUIMode, same trace lines,
    /// same 228 items — only how many times it runs per burst.
    /// </summary>
    private void QueueMenuRebuild(string reason)
    {
        if (_disposed) return;
        Tracing.TraceLine(
            $"NativeMenuBar: rebuild queued ({reason})" + (_rebuildQueued ? " - coalesced" : ""),
            TraceLevel.Verbose);
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        _window.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                _rebuildQueued = false;
                ApplyUIMode(_window.ActiveUIMode);
            }));
    }

    /// <summary>
    /// Handle WM_COMMAND from ShellForm.WndProc. Returns true if the command was handled.
    /// </summary>
    public bool HandleWmCommand(IntPtr wParam)
    {
        int id = wParam.ToInt32() & 0xFFFF;
        if (_handlers.TryGetValue(id, out var handler))
        {
            // EVERY command invocation is traced, not just the unwired ones.
            // When a burst of commands fired unbidden on 2026-08-25 the trace
            // showed the menu being rebuilt and nothing being run, so an hour
            // went into working out what had happened. One line would have
            // answered it.
            Tracing.TraceLine("Menu command: "
                + (_itemNames.TryGetValue(id, out var name) ? name : "id " + id),
                TraceLevel.Info);

            handler();
            // Return focus to WPF content after menu action — but only when
            // it actually needs returning. This used to be an unconditional
            // _window.Focus(), which set keyboard focus on the UserControl
            // ROOT: the reader announced "JJ Flexible Radio Access Main
            // Window" after every command, and after the Connect command it
            // did so in the middle of the connect narration (#395). The
            // reclaim leaves focus alone when it survived inside the content,
            // stands down when another of our windows owns the operator, and
            // lands on Home only when focus genuinely escaped.
            _window.ReclaimFocusAfterMenuCommand();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Handle WM_INITMENUPOPUP — update checkmarks before the menu is shown.
    /// Call from ShellForm.WndProc.
    /// </summary>
    public void HandleInitMenuPopup(IntPtr wParam)
    {
        IntPtr popup = wParam;

        // Only update checkmarks that belong to this specific popup —
        // updating ALL checkmarks on every popup caused NVDA to stutter.
        foreach (var (itemPopup, id, stateGetter, baseText, enabled) in _checkItems)
        {
            if (itemPopup != popup) continue;

            // A greyed toggle has no state to report, and rewriting it here
            // would ENABLE it: ModifyMenuW replaces an item's flags outright, so
            // passing MF_STRING without MF_GRAYED quietly hands back a command
            // that cannot work. Leave the greyed label exactly as it was
            // appended — it already carries its own reason (#214).
            if (!enabled) continue;

            try
            {
                bool isOn = stateGetter();
                CheckMenuItem(itemPopup, (uint)id, MF_BYCOMMAND | (isOn ? MF_CHECKED : MF_UNCHECKED));

                // One of N, not on-or-off: the mark IS the state, and a row
                // that also said ": Off" would spend nine announcements
                // telling the operator what they did not pick. See the remarks
                // on _radioGroupItems (#311).
                if (_radioGroupItems.Contains(id)) continue;

                // Update text with state suffix so screen readers always announce on/off
                string stateText = isOn ? "On" : "Off";
                ModifyMenuW(itemPopup, (uint)id, MF_BYCOMMAND | MF_STRING, (UIntPtr)id, $"{baseText}: {stateText}");
            }
            catch { /* don't let state read errors block menu display */ }
        }
    }

    /// <summary>
    /// Update feature gate state. Applied during next Classic mode rebuild.
    /// </summary>
    public void UpdateFeatureGates(bool diversityAvailable, bool escAvailable)
    {
        _diversityAvailable = diversityAvailable;
        _escAvailable = escAvailable;
    }

    public void Dispose()
    {
        _disposed = true;
        if (_subscribedRig != null)
        {
            _subscribedRig.SliceCountChanged -= OnSliceCountChanged;
            // ConnectionStateChanged was subscribed alongside SliceCountChanged
            // but never unhooked here — the leaked handler was another way a
            // rebuild could fire against a disposed menu bar.
            _subscribedRig.ConnectionStateChanged -= OnConnectionStateChanged;
            _subscribedRig = null;
        }
        if (_currentMenuBar != IntPtr.Zero)
        {
            if (_hwnd != IntPtr.Zero)
                SetMenu(_hwnd, IntPtr.Zero);
            DestroyMenu(_currentMenuBar);
            _currentMenuBar = IntPtr.Zero;
        }
        _handlers.Clear();
    }

    #region Shared DSP/Control Handlers — Sprint 13C

    /// <summary>
    /// Get mode-specific filter bounds for boundary detection.
    /// Matches Slice.UpdateFilter() clamping logic.
    /// </summary>
    private (int lowMin, int highMax) GetFilterBounds()
    {
        if (Rig == null) return (0, 12000);
        string mode = Rig.Mode?.ToUpperInvariant() ?? "USB";
        return mode switch
        {
            "LSB" or "DIGL" => (-12000, 0),
            "CW" => (-12000, 12000),
            "USB" or "DIGU" or "FDV" => (0, 12000),
            _ => (-12000, 12000)
        };
    }

    /// <summary>
    /// Toggle an OffOnValues property, sound it, and speak the result.
    /// Used by NR, NB, ANF, APF, VOX, Squelch, mic boost, compander and the
    /// rest of the on-radio DSP family.
    /// </summary>
    /// <remarks>
    /// Sprint 32 Track E, #128. Every one of these settings is reachable three
    /// ways — a Home panel checkbox, a Ctrl+J leader chord, and this menu — and
    /// only two of the three made a sound. An operator learns from the Home
    /// panel that a toggle answers back, reaches the same setting from the menu
    /// and gets nothing, and that reads as the command having failed rather
    /// than as the menu being quieter. One tone here covers every item that
    /// funnels through this method, which is the argument for the funnel.
    /// </remarks>
    private void ToggleDSP(string label, Func<FlexBase.OffOnValues> getter, Action<FlexBase.OffOnValues> setter)
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        var current = getter();
        var newVal = Rig.ToggleOffOn(current);
        setter(newVal);
        EarconPlayer.ToggleTone(newVal == FlexBase.OffOnValues.on);
        SpeakAfterMenuClose(newVal == FlexBase.OffOnValues.on
            ? Radios.Lexicon.Get("audio.dsp.toggled_on", ("label", label))
            : Radios.Lexicon.Get("audio.dsp.toggled_off", ("label", label)));
    }

    /// <summary>
    /// Adjust an integer value by a step and speak the result. The optional
    /// unit rides the speech ("PC volume 12 dB") so the operator always hears
    /// which scale a value is on — plain numbers stay plain.
    /// </summary>
    private void AdjustValue(string label, Func<int> getter, Action<int> setter,
        int step, int min, int max, string unit = "")
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        int current = getter();
        int newVal = Math.Clamp(current + step, min, max);
        setter(newVal);
        string suffix = string.IsNullOrEmpty(unit) ? "" : " " + unit;
        SpeakAfterMenuClose(Radios.Lexicon.Get("audio.value.adjusted",
            ("label", label), ("value", newVal), ("suffix", suffix)));
    }

    /// <summary>
    /// How far one TX-filter menu press moves an edge — the SAME
    /// width-adaptive ladder the filter layer and the bracket chords walk by
    /// (#527). This menu hard-coded 50 Hz, which made three step rules for
    /// filter edges where at most two could be right, and meant a menu press
    /// and an arrow press inside the layer moved the same edge by different
    /// amounts.
    /// </summary>
    /// <remarks>
    /// Evaluated at CLICK time, inside each item's handler, so it follows the
    /// filter's current width. With no radio it answers the ladder's own
    /// mid-rung value and <see cref="AdjustValue"/> refuses a moment later,
    /// which is where the no-radio sentence belongs.
    /// </remarks>
    private int TxFilterStep() => Rig == null ? 50 : FreqOutHandlers.TxFilterStep(Rig);

    private void SpeakNoRadio()
    {
        Tracing.TraceLine("NativeMenuBar: no-radio guard fired", TraceLevel.Info);
        SpeakAfterMenuClose(Radios.Lexicon.Get("connect.command_needs_radio"));
    }

    /// <summary>
    /// Record the operator's answer to "what may JJ Flexible do to this
    /// radio's profiles?" (#450, #451, #499, #501). Takes effect at the next
    /// connect — the profiles that would have been touched at THIS one already
    /// were not, and acting now would be acting on an answer given after the
    /// fact.
    /// </summary>
    private void SetGuestProfileIntent(Radios.ProfileGuestIntent intent)
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        Rig.RecordProfileIntent(intent);
        EarconPlayer.ToggleTone(intent != Radios.ProfileGuestIntent.LeaveAlone);

        switch (intent)
        {
            case Radios.ProfileGuestIntent.UseMyTransmitAudio:
                var chosen = Rig.LocalTransmitAudioProfileChoice;
                if (string.IsNullOrEmpty(chosen))
                {
                    // No profile chosen yet: send them straight to the picker
                    // rather than leaving a dead intent.
                    SpeakAfterMenuClose(Radios.Lexicon.Get(
                        Rig.LocalTransmitAudioProfileNames().Count == 0
                            ? "settings.profile_guest.no_presets"
                            : "settings.profile_guest.why.no_local_profile"));
                }
                else
                {
                    SpeakAfterMenuClose(Radios.Lexicon.Get(
                        "settings.profile_guest.live_audio_chosen", ("preset", chosen)));
                }
                break;
            case Radios.ProfileGuestIntent.LoadMineAndPutBack:
                SpeakAfterMenuClose(Radios.Lexicon.Get(
                    Rig.RadioIsMine
                        ? "settings.profile_guest.opted_in_mine"
                        : "settings.profile_guest.opted_in"));
                break;
            default:
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.profile_guest.opted_out"));
                break;
        }
        RebuildCurrentMenu();
    }

    /// <summary>
    /// Record which of the operator's transmit-audio profiles to apply live on
    /// this radio, and switch the intent to "use my transmit audio" so the
    /// choice is not left inert. Takes effect at the next connect.
    /// </summary>
    private void SetGuestTransmitAudioProfile(string presetName)
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        Rig.SetLocalTransmitAudioChoice(presetName);
        EarconPlayer.ToggleTone(true);
        SpeakAfterMenuClose(Radios.Lexicon.Get(
            "settings.profile_guest.live_audio_chosen", ("preset", presetName)));
        RebuildCurrentMenu();
    }

    /// <summary>
    /// Build ScreenFields DSP submenu (shared between Classic and Modern DSP menus).
    /// </summary>
    private void BuildDSPItems(IntPtr parent)
    {
        if (Rig == null) return;

        // === Noise Reduction submenu ===
        var nrSub = AddSubmenu(parent, "Noise Reduction");
        // NRF, NRS, RNN all require 8000-series/Aurora DSP hardware, and a
        // subscription on top of it. Sprint 30 Track A: when either gate is
        // shut the family used to be simply ABSENT, which reads exactly like a
        // missing feature. Now one item stands in its place and says which
        // gate it is — and, because a feature you cannot have is only useful
        // news alongside the one you can, points at the PC-side equivalent.
        string? nrGate = AdvancedNrGateMessage();
        if (nrGate != null)
        {
            AddWired(nrSub, "Advanced noise reduction unavailable", () =>
                SpeakAfterMenuClose(AdvancedNrGateMessage()
                    ?? Radios.Lexicon.Get("audio.nr.advanced_available_reopen")));
        }
        else
        {
            // "On-Radio" prefix (DSP controls track, 2026-08-11): these run in
            // the radio's own DSP and have PC-side namesakes two submenus
            // down — the names now say which side of the wire each one is.
            AddChecked(nrSub, "On-Radio Neural NR (RNN)\tCtrl+J, R", () =>
                ToggleDSP("On-Radio Neural NR", () => Rig.NeuralNoiseReduction, v => Rig.NeuralNoiseReduction = v),
                () => Rig?.NeuralNoiseReduction == FlexBase.OffOnValues.on);
            AddChecked(nrSub, "On-Radio Spectral NR (NRS)\tCtrl+J, S", () =>
                ToggleDSP("On-Radio Spectral NR", () => Rig.SpectralNoiseReduction, v => Rig.SpectralNoiseReduction = v),
                () => Rig?.SpectralNoiseReduction == FlexBase.OffOnValues.on);
            AddChecked(nrSub, "NR Filter (NRF)\tCtrl+J, Shift+N", () =>
                ToggleDSP("NR Filter", () => Rig.NoiseReductionFilter, v => Rig.NoiseReductionFilter = v),
                () => Rig?.NoiseReductionFilter == FlexBase.OffOnValues.on);
        }
        // Legacy NR always available
        AddChecked(nrSub, "Legacy NR", () =>
            ToggleDSP("Legacy NR", () => Rig.NoiseReductionLegacy, v => Rig.NoiseReductionLegacy = v),
            () => Rig?.NoiseReductionLegacy == FlexBase.OffOnValues.on);
        // QB Track I menu parity — the level under the toggle (matches the
        // ScreenFields NR Level field that appears when Legacy NR is on).
        AddWired(nrSub, "NR Level Up", () =>
            AdjustValue("NR Level", () => Rig.NoiseReductionLegacyLevel, v => Rig.NoiseReductionLegacyLevel = v, 1, 1, 15));
        AddWired(nrSub, "NR Level Down", () =>
            AdjustValue("NR Level", () => Rig.NoiseReductionLegacyLevel, v => Rig.NoiseReductionLegacyLevel = v, -1, 1, 15));

        // === Noise Blankers submenu ===
        var nbSub = AddSubmenu(parent, "Noise Blankers");
        AddChecked(nbSub, "Noise Blanker (NB)\tCtrl+J, B", () =>
            ToggleDSP("Noise Blanker", () => Rig.NoiseBlanker, v => Rig.NoiseBlanker = v),
            () => Rig?.NoiseBlanker == FlexBase.OffOnValues.on);
        AddWired(nbSub, "NB Level Up", () =>
            AdjustValue("NB Level", () => Rig.NoiseBlankerLevel, v => Rig.NoiseBlankerLevel = v, 5, 1, 100));
        AddWired(nbSub, "NB Level Down", () =>
            AdjustValue("NB Level", () => Rig.NoiseBlankerLevel, v => Rig.NoiseBlankerLevel = v, -5, 1, 100));
        AddChecked(nbSub, "Wideband NB (WNB)\tCtrl+J, W", () =>
            ToggleDSP("Wideband NB", () => Rig.WidebandNoiseBlanker, v => Rig.WidebandNoiseBlanker = v),
            () => Rig?.WidebandNoiseBlanker == FlexBase.OffOnValues.on);
        AddWired(nbSub, "WNB Level Up", () =>
            AdjustValue("WNB Level", () => Rig.WidebandNoiseBlankerLevel, v => Rig.WidebandNoiseBlankerLevel = v, 5, 1, 100));
        AddWired(nbSub, "WNB Level Down", () =>
            AdjustValue("WNB Level", () => Rig.WidebandNoiseBlankerLevel, v => Rig.WidebandNoiseBlankerLevel = v, -5, 1, 100));

        // === PC-side noise reduction (runs on the computer, ALL radios) ===
        // QB Track I menu parity — these existed only as ScreenFields
        // checkboxes; the menu is the addressable second door.
        var pcSub = AddSubmenu(parent, "PC Noise Reduction");
        AddChecked(pcSub, "PC Neural NR\tCtrl+J, Shift+R", () =>
        {
            var p = _window.FieldsPanel?.AudioPipeline;
            if (p == null) { SpeakAfterMenuClose(Radios.Lexicon.Get("audio.pc_pipeline.unavailable")); return; }
            p.RnnEnabled = !p.RnnEnabled;
            _window.PersistDspSettings();
            // #128 sweep audit (2026-08-21): this was the one silent road of
            // four — the Home panel, the Noise Profiles dialog and the Ctrl+J
            // chord all toned, and an operator who learned the sound from any
            // of those reads this menu's silence as the command failing.
            EarconPlayer.ToggleTone(p.RnnEnabled);
            SpeakAfterMenuClose(p.RnnEnabled
                ? Radios.Lexicon.Get("audio.pc_nr.neural_on")
                : Radios.Lexicon.Get("audio.pc_nr.neural_off"));
        }, () => _window.FieldsPanel?.AudioPipeline?.RnnEnabled == true);
        AddChecked(pcSub, "PC Spectral NR\tCtrl+J, Shift+S", () =>
        {
            var p = _window.FieldsPanel?.AudioPipeline;
            if (p == null) { SpeakAfterMenuClose(Radios.Lexicon.Get("audio.pc_pipeline.unavailable")); return; }
            p.SpectralEnabled = !p.SpectralEnabled;
            _window.PersistDspSettings();
            // #128 sweep audit (2026-08-21): same as PC Neural NR above — the
            // menu was the one silent road of four into this state.
            EarconPlayer.ToggleTone(p.SpectralEnabled);
            SpeakAfterMenuClose(
                !p.SpectralEnabled ? Radios.Lexicon.Get("audio.pc_nr.spectral_off")
                : p.HasNoiseProfile ? Radios.Lexicon.Get("audio.pc_nr.spectral_on")
                : Radios.Lexicon.Get("audio.pc_nr.spectral_on_no_profile"));
        }, () => _window.FieldsPanel?.AudioPipeline?.SpectralEnabled == true);

        // DSP controls track (2026-08-11) — the capture and the profile
        // room. The capture start is deferred past the menu close so its
        // spoken countdown isn't trampled by NVDA's menu-dismiss chatter
        // (same reasoning as SpeakAfterMenuClose's 500 ms).
        AddWired(pcSub, "Capture Noise Profile\tCtrl+J, Q", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            var p = _window.FieldsPanel?.AudioPipeline;
            if (p == null) { SpeakAfterMenuClose(Radios.Lexicon.Get("audio.pc_pipeline.unavailable")); return; }
            if (NoiseCaptureNarrator.IsRunning) { NoiseCaptureNarrator.Cancel(); return; }
            var rig = Rig;
            _window.Dispatcher.BeginInvoke(async () =>
            {
                await System.Threading.Tasks.Task.Delay(500);
                NoiseCaptureNarrator.Start(rig, p,
                    _window.CurrentAudioConfig?.SpectralSubSampleDuration ?? 3);
            });
        });
        AddWired(pcSub, "Noise Profiles...", () =>
        {
            new Dialogs.NoiseProfilesDialog(Rig, _window.FieldsPanel?.AudioPipeline,
                () => _window.CurrentAudioConfig, () => _window.PersistDspSettings())
                .ShowDialog();
        });
        AddWired(pcSub, "Open Noise Profiles Folder", () =>
        {
            SpeakAfterMenuClose(NoiseProfileStore.OpenFolder()
                ? Radios.Lexicon.Get("audio.noise_profiles.folder_opened")
                : Radios.Lexicon.Get("audio.noise_profiles.folder_open_failed"));
        });

        // === Auto Notch ===
        var anfSub = AddSubmenu(parent, "Auto Notch");
        AddChecked(anfSub, "FFT Auto-Notch\tCtrl+J, Ctrl+A", () =>
            ToggleDSP("FFT Auto-Notch", () => Rig.AutoNotchFFT, v => Rig.AutoNotchFFT = v),
            () => Rig?.AutoNotchFFT == FlexBase.OffOnValues.on);
        AddChecked(anfSub, "Legacy Auto-Notch", () =>
            ToggleDSP("Legacy Auto-Notch", () => Rig.AutoNotchLegacy, v => Rig.AutoNotchLegacy = v),
            () => Rig?.AutoNotchLegacy == FlexBase.OffOnValues.on);

        // === Audio Peak Filter (CW only) ===
        AddChecked(parent, "Audio Peak Filter (APF)\tCtrl+J, P", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            string? mode = Rig.Mode;
            if (mode != null && !mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase))
            {
                SpeakAfterMenuClose(Radios.Lexicon.Get("audio.apf.cw_only"));
                return;
            }
            ToggleDSP("Audio Peak Filter", () => Rig.APF, v => Rig.APF = v);
        }, () => Rig?.APF == FlexBase.OffOnValues.on);

        AddSep(parent);

        // === Meter Tones ===
        // These two were ONE item until Sprint 32 Track B, and the one item was
        // wrong twice over: it claimed Ctrl+Alt+M, which is not a binding this
        // app has, and it showed a checkmark for the tone state while actually
        // opening the panel. Two items now, each doing and naming one thing.
        var meterSub = AddSubmenu(parent, "Meter Tones");
        AddChecked(meterSub, "Meter Tones On/Off\tCtrl+J, T", () =>
        {
            MeterToneEngine.ToggleEnabled();
        }, () => MeterToneEngine.Enabled);

        AddWired(meterSub, "Meters Panel\tCtrl+M", () =>
        {
            _window.ToggleMetersPanel();
        });

        AddWired(meterSub, "Cycle Preset", () =>
        {
            MeterToneEngine.CyclePreset();
            SpeakAfterMenuClose(Radios.Lexicon.Get("audio.meter.preset",
                ("preset", MeterToneEngine.CurrentPreset)));
        });

        AddWired(meterSub, "Speak Meters", () =>
        {
            MeterToneEngine.SpeakMeters();
        });

        AddChecked(meterSub, "Peak Watcher", () =>
        {
            MeterToneEngine.PeakWatcherEnabled = !MeterToneEngine.PeakWatcherEnabled;
            SpeakAfterMenuClose(MeterToneEngine.PeakWatcherEnabled
                ? Radios.Lexicon.Get("audio.meter.peak_watcher_on")
                : Radios.Lexicon.Get("audio.meter.peak_watcher_off"));
        }, () => MeterToneEngine.PeakWatcherEnabled);
    }

    /// <summary>
    /// Build filter control items (shared between Classic ScreenFields and Modern Filter menu).
    /// </summary>
    private void BuildFilterItems(IntPtr parent)
    {
        if (Rig == null) return;

        const int filterStep = 50;

        // All filter operations use SetFilter() to set both edges atomically.
        // Setting FilterLow and FilterHigh separately through the command queue
        // causes a race condition: FlexLib clamps each edge against the other's
        // stale value, creating a death spiral to 0-10 Hz bandwidth.

        AddWired(parent, "Narrow Filter", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            int low = Rig.FilterLow;
            int high = Rig.FilterHigh;
            int newLow = low + filterStep;
            int newHigh = high - filterStep;
            if (newHigh - newLow >= 50)
            {
                Rig.SetFilter(newLow, newHigh);
                SpeakAfterMenuClose(Radios.Lexicon.Get("audio.filter.range",
                    ("low", newLow), ("high", newHigh)));
            }
            else
            {
                SpeakAfterMenuClose(Radios.Lexicon.Get("audio.filter.at_minimum"));
            }
        });
        AddWired(parent, "Widen Filter", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            var (lowMin, highMax) = GetFilterBounds();
            int low = Rig.FilterLow;
            int high = Rig.FilterHigh;
            int newLow = Math.Max(low - filterStep, lowMin);
            int newHigh = Math.Min(high + filterStep, highMax);
            if (newLow == low && newHigh == high)
            {
                SpeakAfterMenuClose(Radios.Lexicon.Get("audio.filter.at_maximum"));
            }
            else
            {
                Rig.SetFilter(newLow, newHigh);
                SpeakAfterMenuClose(Radios.Lexicon.Get("audio.filter.range",
                    ("low", newLow), ("high", newHigh)));
            }
        });
        AddWired(parent, "Shift Low Edge Up", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            int newLow = Rig.FilterLow + filterStep;
            int high = Rig.FilterHigh;
            if (high - newLow >= 10)
            {
                Rig.SetFilter(newLow, high);
                SpeakAfterMenuClose(Radios.Lexicon.Get("audio.filter.low_edge", ("low", newLow)));
            }
            else
            {
                SpeakAfterMenuClose(Radios.Lexicon.Get("audio.filter.at_minimum"));
            }
        });
        AddWired(parent, "Shift Low Edge Down", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            var (lowMin, _) = GetFilterBounds();
            int low = Rig.FilterLow;
            int newLow = Math.Max(low - filterStep, lowMin);
            if (newLow == low)
            {
                SpeakAfterMenuClose(Radios.Lexicon.Get("audio.filter.at_beginning"));
            }
            else
            {
                Rig.SetFilter(newLow, Rig.FilterHigh);
                SpeakAfterMenuClose(Radios.Lexicon.Get("audio.filter.low_edge", ("low", newLow)));
            }
        });
        AddWired(parent, "Shift High Edge Up", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            var (_, highMax) = GetFilterBounds();
            int high = Rig.FilterHigh;
            int newHigh = Math.Min(high + filterStep, highMax);
            if (newHigh == high)
            {
                SpeakAfterMenuClose(Radios.Lexicon.Get("audio.filter.at_end"));
            }
            else
            {
                Rig.SetFilter(Rig.FilterLow, newHigh);
                SpeakAfterMenuClose(Radios.Lexicon.Get("audio.filter.high_edge", ("high", newHigh)));
            }
        });
        AddWired(parent, "Shift High Edge Down", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            int low = Rig.FilterLow;
            int newHigh = Rig.FilterHigh - filterStep;
            if (newHigh - low >= 10)
            {
                Rig.SetFilter(low, newHigh);
                SpeakAfterMenuClose(Radios.Lexicon.Get("audio.filter.high_edge", ("high", newHigh)));
            }
            else
            {
                SpeakAfterMenuClose(Radios.Lexicon.Get("audio.filter.at_minimum"));
            }
        });

        AddWired(parent, "Read Filter", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            SpeakAfterMenuClose(Radios.Lexicon.Get("audio.filter.range",
                ("low", Rig.FilterLow), ("high", Rig.FilterHigh)));
        });

        // Filter presets submenu
        if (FilterPresets != null && Rig != null)
        {
            AddSep(parent);
            string mode = Rig.Mode ?? "USB";
            var presets = FilterPresets.GetPresetsForMode(mode);
            int activeIdx = FilterPresets.FindActivePreset(mode, Rig.FilterLow, Rig.FilterHigh);

            for (int i = 0; i < presets.Count; i++)
            {
                var preset = presets[i];
                string label = $"{preset.Name} ({preset.FormatForSpeech()})";
                if (i == activeIdx)
                    label = $"\u2713 {label}"; // Unicode checkmark prefix
                AddWired(parent, label, () =>
                {
                    if (Rig == null) { SpeakNoRadio(); return; }
                    string currentMode = Rig.Mode ?? "USB";
                    var (mLow, mHigh) = FilterPresets.MirrorForMode(currentMode, preset.Low, preset.High);
                    Rig.SetFilter(mLow, mHigh);
                    SpeakAfterMenuClose(Radios.Lexicon.Get("audio.filter.preset_selected",
                        ("preset", preset.Name), ("width", preset.FormatForSpeech())));
                });
            }
        }

        AddSep(parent);
        AddWired(parent, "Filter Calculator", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            var dialog = new Dialogs.FilterCalculatorDialog();
            if (dialog.ShowDialog() == true && dialog.ResultLow.HasValue && dialog.ResultHigh.HasValue)
            {
                Rig.SetFilter(dialog.ResultLow.Value, dialog.ResultHigh.Value);
            }
        });
    }

    /// <summary>
    /// Build the Audio menu's items, in the one order they ever appear. Shared
    /// by the Classic top-level Audio menu and the Modern Slice, Audio submenu,
    /// so both get the same shape from the same place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The composition lives in <see cref="Radios.AudioMenuLayout"/> and the
    /// handlers live here. That split is the fix for #214. This method used to
    /// wrap seven items in <c>if (Rig != null)</c>, so the menu's SHAPE — what
    /// is first, what is fifth — changed with connection state, and a Windows
    /// menu starts a first-letter search AFTER the highlighted item. Read the
    /// layout for the full mechanism and for what this deliberately does not
    /// fix.
    /// </para>
    /// <para>
    /// Every radio-gated handler still opens with its own no-radio guard. A
    /// greyed item raises no WM_COMMAND, so from this menu those guards should
    /// now be unreachable — they stay because the guard is the cheap half of
    /// the pair, and a handler that assumes a radio because a menu promised one
    /// is exactly the assumption that stops being true.
    /// </para>
    /// </remarks>
    private void BuildAudioItems(IntPtr parent)
    {
        bool connected = Rig != null;

        foreach (var entry in Radios.AudioMenuLayout.Entries)
        {
            if (entry.Kind == Radios.AudioMenuEntryKind.Separator)
            {
                AddSep(parent);
                continue;
            }

            string label = Radios.AudioMenuLayout.LabelFor(entry, connected);
            bool enabled = connected || !entry.NeedsRadio;

            switch (entry.Id)
            {
                case "mute-slice":
                    AddChecked(parent, label, MenuMuteSlice,
                        () => Rig?.SliceMute == true, enabled);
                    break;
                case "mute-all-slices":
                    AddWired(parent, label, MenuMuteAllSlices, enabled);
                    break;
                case "release-extra-slices":
                    AddWired(parent, label, MenuReleaseExtraSlices, enabled);
                    break;
                case "pc-audio":
                    AddChecked(parent, label, MenuTogglePcAudio,
                        () => Rig?.PCAudio == true, enabled);
                    break;
                case "binaural":
                    AddChecked(parent, label, MenuToggleBinaural,
                        () => Rig?.Binaural == FlexBase.OffOnValues.on, enabled);
                    break;
                case "pc-audio-levels":
                    AddWired(parent, label, MenuPcAudioLevels, enabled);
                    break;
                case "on-radio-levels":
                    AddWired(parent, label, MenuOnRadioLevels, enabled);
                    break;
                case "audio-devices":
                    AddWired(parent, label, MenuAudioDevices, enabled);
                    break;
                case "earcon-scratchpad":
                    AddWired(parent, label, MenuEarconScratchpad, enabled);
                    break;
                case "audio-workshop":
                    AddWired(parent, label, MenuAudioWorkshop, enabled);
                    break;
                default:
                    // A layout row nobody wired. Say so rather than dropping it:
                    // a silently missing row moves every position after it, which
                    // is the whole defect this menu was rebuilt to stop.
                    Tracing.TraceLine("NativeMenuBar: Audio menu entry '" + entry.Id
                        + "' has no handler", TraceLevel.Error);
                    AddNotImplemented(parent, entry.Label);
                    break;
            }
        }
    }

    private void MenuMuteSlice()
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        bool newMute = !Rig.SliceMute;
        Rig.SliceMute = newMute;
        // Matches the hotkey road (KeyCommands.MuteSliceHandler), which
        // has toned on newMute since it was written. Mute All directly
        // below this has always toned too, which is what makes the
        // omission here read as an oversight rather than a decision.
        EarconPlayer.ToggleTone(newMute);
        // The slice is NAMED here, through the same two strings the hotkey
        // uses (KeyCommands.MuteSliceHandler). On a multi-slice radio "which
        // one" is the entire question the operator is asking, and a bare
        // "Muted" answers the one they did not ask. The named strings already
        // existed and only this call site reached past them, so the same
        // operation announced itself two different ways depending on which
        // door it came through (#313).
        string letter = Rig.VFOToLetter(Rig.RXVFO);
        SpeakAfterMenuClose(newMute
            ? Radios.Lexicon.Get("audio.mute.slice_muted", ("letter", letter))
            : Radios.Lexicon.Get("audio.mute.slice_unmuted", ("letter", letter)));
    }

    private void MenuMuteAllSlices()
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        bool target = !Rig.AllMySlicesMuted;
        Rig.SetAllMySlicesMute(target);
        if (target) EarconPlayer.MuteAllOnTone();
        else EarconPlayer.MuteAllOffTone();
        SpeakAfterMenuClose(target
            ? Radios.Lexicon.Get("audio.mute.all_slices_muted")
            : Radios.Lexicon.Get("audio.mute.all_slices_unmuted"));
    }

    private void MenuReleaseExtraSlices()
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        int before = Rig.MyNumSlices;
        if (before <= 1) { SpeakAfterMenuClose(Radios.Lexicon.Get("settings.slice.only_one_active")); return; }
        if (Rig.ReleaseAllExtraSlices())
        {
            EarconPlayer.MuteAllOnTone();
            int removed = before - 1;
            string keptLetter = Rig.VFOToLetter(Rig.RXVFO);
            SpeakAfterMenuClose(removed == 1
                ? Radios.Lexicon.Get("settings.slice.released_extras_one",
                    ("removed", removed), ("keptLetter", keptLetter))
                : Radios.Lexicon.Get("settings.slice.released_extras_many",
                    ("removed", removed), ("keptLetter", keptLetter)));
        }
    }

    private void MenuTogglePcAudio()
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        bool wanted = !Rig.PCAudio;
        Rig.PCAudio = wanted;
        // Threads Track (2026-08-12): remember the operator's choice
        // per radio, so remember-last can restore it on the next
        // connect. Intent, not outcome — a toggle that failed tonight
        // is still the wish worth carrying forward.
        RadioConfig.RecordPcAudioUserChoice(Rig.SelectedRadioSerial, wanted);
        // Read the radio back rather than the request. Turning PC audio
        // on can fail — no usable sound device — and the old code
        // announced the wish, not the outcome, so a failed toggle said
        // "PC audio on" while nothing played. QB Track B, 2026-08-07.
        bool actual = Rig.PCAudio;
        // Sound the outcome for the same reason the speech does. PC
        // audio can refuse to come on when no sound device is
        // configured, and a rising tone over a toggle that did not
        // happen is a confident lie.
        EarconPlayer.ToggleTone(actual);
        SpeakAfterMenuClose(
            actual ? Radios.Lexicon.Get("audio.pc_audio.on")
            : wanted ? Radios.Lexicon.Get("audio.pc_audio.could_not_start")
            : Radios.Lexicon.Get("audio.pc_audio.off"));
    }

    /// <summary>
    /// Binaural receive on or off (#537) — the radio's own stereo widening of
    /// what you hear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It announces the WANTED state rather than reading the radio back, which
    /// is the opposite of <see cref="MenuTogglePcAudio"/> directly above and is
    /// deliberate. PC audio can refuse to come on when there is no usable sound
    /// device, so its answer has to be the outcome. Binaural is a queued write
    /// to the radio (<c>FlexBase.Binaural</c> enqueues, like every mute and
    /// level on this menu), so reading it back on the next line would report
    /// the state it was in a moment ago and announce every flip backwards. The
    /// audio layer's Ctrl+B answers the same way for the same reason.
    /// </para>
    /// <para>
    /// The words are <c>audio.binaural.on</c> / <c>audio.binaural.off</c> —
    /// the audio layer's own strings, which the Home audio expander's checkbox
    /// also speaks. One flag, three doors, one sentence.
    /// </para>
    /// </remarks>
    private void MenuToggleBinaural()
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        bool wanted = Rig.Binaural != FlexBase.OffOnValues.on;
        Rig.Binaural = wanted ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off;
        EarconPlayer.ToggleTone(wanted);
        SpeakAfterMenuClose(wanted
            ? Radios.Lexicon.Get("audio.binaural.on")
            : Radios.Lexicon.Get("audio.binaural.off"));
    }

    // === Levels dialogs (Audio Arc Track A-2, 2026-08-11) ===
    // Field feedback from Noel at the radio: a menu is the wrong
    // instrument for riding a value — it dismisses after each
    // activation, so five nudges meant five trips two menus deep.
    // The up/down PAIRS (Track A's PC Audio and On-Radio submenus,
    // plus the pre-Track-A flat Audio Gain and Pan pairs, which
    // duplicated Home's Volume and Pan fields) are retired; each
    // group is now a single door into a dialog that stays open
    // while you ride its levels with Up/Down. Two doors, not one:
    // the two sides of the wire stay two surfaces on purpose.
    // Slice Volume and Pan live on as arrow fields in Home's audio
    // expander (Ctrl+Shift+U) — they are per-slice controls, not
    // levels on either side of the wire. Ctrl+J, V volume mode is
    // the fast route to all of it and is unchanged.
    private void MenuPcAudioLevels()
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        new Dialogs.PcAudioLevelsDialog(Rig, _window.PersistPcOutputVolume).ShowDialog();
    }

    private void MenuOnRadioLevels()
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        new Dialogs.OnRadioLevelsDialog(Rig).ShowDialog();
    }

    // Device setup — always available (no radio required).
    // Renamed 2026-08-07 (QB Track B): this is one dialog covering every
    // sound device JJ Flex uses, not the old two-modals-in-a-row radio-only
    // picker. The menu entry survives because muscle memory and the help
    // pages both point at it; only the destination changed. That is also why
    // #214 was NOT fixed by renaming it to give the menu unique first
    // letters — see AudioMenuLayout.
    private void MenuAudioDevices() => _window.AudioSetupCallback?.Invoke();

    private void MenuEarconScratchpad()
    {
        var dlg = new Dialogs.EarconScratchpadDialog();
        dlg.ShowDialog();
    }

    // The workshop is the mic-setup and monitoring surface, and the Audio menu
    // is the first place an operator looks for it (Noel 2026-08-11). It used to
    // be appended by each of the two callers after this builder ran; it is a row
    // of the Audio menu like any other and now lives in the layout with them.
    private void MenuAudioWorkshop()
        => Dialogs.AudioWorkshopDialog.ShowOrFocus(Rig, 0);

    /// <summary>
    /// Build slice management items (Create/Release Slice).
    /// Sprint 22 Phase 7.
    /// </summary>
    private void BuildSliceItems(IntPtr parent)
    {
        if (Rig == null) return;

        AddWired(parent, "Create Slice", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            int countBefore = Rig.MyNumSlices;
            if (Rig.NewSlice())
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.slice.created",
                    ("count", countBefore + 1)));
            else
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.slice.maximum_reached"));
        });

        AddWired(parent, "Release Slice", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            int numSlices = Rig.MyNumSlices;
            if (numSlices <= 1)
            {
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.slice.cannot_release_only"));
                return;
            }
            int toRemove = numSlices - 1;
            string letter = Rig.VFOToLetter(toRemove);
            if (Rig.RemoveSlice(toRemove))
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.slice.released",
                    ("letter", letter), ("count", numSlices - 1)));
            else
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.slice.release_failed"));
        });
    }

    /// <summary>
    /// Build RX/TX antenna selection submenus. Dynamic — reads antenna lists from the radio.
    /// Sprint 22 Phase 6. QB Track I split the two halves into their own
    /// builders so the TX Antenna submenu can also live under Transmit
    /// (next to Power, where the XVTR/power relationship is taught) —
    /// these submenus existed for four sprints without the app's own
    /// author ever finding them, so they get a second door and mnemonics.
    /// </summary>
    private void BuildAntennaSelectItems(IntPtr parent)
    {
        BuildRxAntennaSubmenu(parent);
        BuildTxAntennaSubmenu(parent);
    }

    /// <summary>RX antenna selection submenu — checkmark on the current choice.</summary>
    private void BuildRxAntennaSubmenu(IntPtr parent)
    {
        if (Rig == null) return;

        var rxSub = AddSubmenu(parent, "&RX Antenna");
        foreach (var ant in Rig.RXAntennaList)
        {
            var antName = ant; // capture for closure
            AddChecked(rxSub, antName, () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                Rig.RXAntennaName = antName;
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.antenna.rx_selected", ("antName", antName)));
            }, () => string.Equals(Rig?.RXAntennaName, antName, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// TX antenna selection submenu — checkmark on the current choice.
    /// Selecting the transverter port announces the power-semantics change:
    /// that's the moment the operator needs to learn that drive is now dBm.
    /// </summary>
    private void BuildTxAntennaSubmenu(IntPtr parent)
    {
        if (Rig == null) return;

        var txSub = AddSubmenu(parent, "&TX Antenna");
        foreach (var ant in Rig.TXAntennaList)
        {
            var antName = ant;
            AddChecked(txSub, antName, () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                Rig.TXAntennaName = antName;
                if (string.Equals(antName, "XVTR", StringComparison.OrdinalIgnoreCase))
                    SpeakAfterMenuClose(Radios.Lexicon.Get("settings.antenna.tx_xvtr"));
                else
                    SpeakAfterMenuClose(Radios.Lexicon.Get("settings.antenna.tx_selected", ("antName", antName)));
            }, () => string.Equals(Rig?.TXAntennaName, antName, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Build ATU (Antenna Tuner) control items.
    /// </summary>
    private void BuildATUItems(IntPtr parent)
    {
        if (Rig == null) return;

        AddChecked(parent, "ATU On/Off", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            bool isOn = Rig.FlexTunerType != FlexBase.FlexTunerTypes.none;
            Rig.FlexTunerType = isOn ? FlexBase.FlexTunerTypes.none : FlexBase.FlexTunerTypes.auto;
            EarconPlayer.ToggleTone(!isOn);
            SpeakAfterMenuClose(isOn
                ? Radios.Lexicon.Get("settings.atu.turned_off")
                : Radios.Lexicon.Get("settings.atu.turned_on"));
        }, () => Rig?.FlexTunerType != FlexBase.FlexTunerTypes.none);

        AddWired(parent, "ATU Mode", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            // Cycle: none → manual → auto → none
            var mode = Rig.FlexTunerType;
            var newMode = mode switch
            {
                FlexBase.FlexTunerTypes.none => FlexBase.FlexTunerTypes.manual,
                FlexBase.FlexTunerTypes.manual => FlexBase.FlexTunerTypes.auto,
                FlexBase.FlexTunerTypes.auto => FlexBase.FlexTunerTypes.none,
                _ => FlexBase.FlexTunerTypes.auto
            };
            Rig.FlexTunerType = newMode;
            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.atu.mode", ("newMode", newMode)));
        });

        AddWired(parent, "ATU Memories", () =>
        {
            if (Rig?.ShowMemoriesDialog != null)
                Rig.ShowMemoriesDialog();
            else
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.atu.memories_unavailable"));
        });
    }

    /// <summary>
    /// Build diversity items with proper feature gating.
    ///
    /// <para>Sprint 30 Track A — always builds SOMETHING. The caller used to
    /// skip this whole method on a 1-SCU radio, so an operator on a 6400 or an
    /// 8400 met a menu with no diversity in it at all and no way to find out
    /// why. "Missing" and "not for this radio" feel identical from the
    /// keyboard, and only one of them is true. Disabled-with-a-reason is the
    /// house pattern (CLAUDE.md accessibility guidance) and it is what the
    /// Feature Availability tab has always done in text.</para>
    /// </summary>
    private void BuildDiversityItems(IntPtr parent)
    {
        if (Rig == null) return;

        if (Rig.DiversityReady)
        {
            AddChecked(parent, "Toggle Diversity", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                Rig.ToggleDiversity();
                EarconPlayer.ToggleTone(Rig.DiversityOn);
                SpeakAfterMenuClose(Rig.DiversityOn
                    ? Radios.Lexicon.Get("settings.diversity.on")
                    : Radios.Lexicon.Get("settings.diversity.off"));
            }, () => Rig?.DiversityOn == true);
            return;
        }

        // DiversityGateMessage is only empty when every gate passes, which is
        // the DiversityReady branch above — so the fallback text is defence,
        // not an expected path. Never leave the item wordless either way.
        AddWired(parent, "Diversity unavailable", () =>
            SpeakAfterMenuClose(
                NonEmpty(Rig?.DiversityGateMessage)
                ?? Radios.Lexicon.Get("settings.diversity.unavailable_fallback")));
    }

    /// <summary>Trim-to-null helper for gate messages, so an empty string from
    /// a radio that has not answered yet never becomes a wordless menu item.</summary>
    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Why the advanced noise-reduction family (RNN, NRS, NRF) cannot be
    /// offered on this radio, or null when it can.
    ///
    /// <para>The ladder is deliberately asymmetric about what it does NOT
    /// know. Hardware is a fact we hold locally, so "your model does not have
    /// the DSP for it" is safe to state. A licence we have never been told
    /// about is not evidence of anything — the radio may simply not have sent
    /// its feature list yet — so an unreported licence leaves the controls in
    /// place rather than declaring a subscribed feature missing. Only a
    /// licence the radio positively reported as disabled produces a
    /// subscription message, and then it is the radio's own wording.</para>
    /// </summary>
    private string? AdvancedNrGateMessage()
    {
        var rig = Rig;
        if (rig == null) return "No radio connected.";

        if (!rig.NeuralNRHardwareSupported)
        {
            return "This radio model does not have the DSP hardware for the advanced "
                 + "noise reduction family — RNN, spectral NR and the NR filter. Legacy NR "
                 + "and the noise blankers above work on every model, and JJ Flex's own "
                 + "PC-side noise reduction runs on this computer and works on every radio.";
        }

        // Never reported: we do not know, so we do not say. The toggles stay.
        if (!rig.NoiseReductionLicenseReported) return null;

        if (!rig.NoiseReductionLicensed)
        {
            return "Your radio reports that the advanced noise reduction features are not "
                 + "included in its current subscription. JJ Flex's own PC-side noise "
                 + "reduction runs on this computer instead and needs no subscription.";
        }

        return null;
    }

    /// <summary>
    /// Build the ESC (Enhanced Signal Clarity) entry.
    ///
    /// <para>Sprint 31 Track R. ESC had no menu item anywhere, on any radio,
    /// regardless of licence or hardware — it appeared only as one line of
    /// prose in Tools ▸ Feature Availability. The investigation the task asked
    /// for found something better than a missing feature: EscDialog has existed
    /// complete since Sprint 9 Track B, with its enable toggle, phase slider,
    /// 90 and 180 degree presets, gain slider and status line all built and
    /// working, and NOTHING has ever constructed it. Same shape as the Saved
    /// Diagnostic Logs browser found this month — built, then never given a
    /// door — and the same answer: give it a door.</para>
    ///
    /// <para>Until now the Feature Availability report could tell an operator
    /// "ESC: disabled" about a control the application provided no way on earth
    /// to enable. That is the sharpest form of the silent absence this task
    /// exists to close.</para>
    /// </summary>
    private void BuildEscItems(IntPtr parent)
    {
        if (Rig == null) return;

        string? gate = EscGateMessage();
        if (gate != null)
        {
            AddWired(parent, "Enhanced Signal Clarity unavailable", () =>
                SpeakAfterMenuClose(EscGateMessage()
                    ?? Radios.Lexicon.Get("settings.esc.available_reopen")));
            return;
        }

        AddWired(parent, "Enhanced Signal Clarity", ShowEscDialog);
    }

    /// <summary>
    /// Why ESC cannot be offered, or null when it can.
    ///
    /// <para>ESC shares the diversity licence (LicenseFeatDivEsc) and rides on
    /// the diversity pair, so its gates are diversity's gates — which means
    /// DiversityGateMessage already encodes exactly the asymmetry this needs,
    /// including "licence status pending" for a licence never reported. Reusing
    /// it rather than writing a second ladder means the two can never drift
    /// into disagreeing about the same radio.</para>
    ///
    /// <para>Note what is deliberately NOT a gate here: diversity being
    /// currently switched OFF. The dialog opens and explains that, because
    /// "turn diversity on first" is an instruction the operator can act on,
    /// while a menu item that vanishes is not.</para>
    /// </summary>
    private string? EscGateMessage()
    {
        var rig = Rig;
        if (rig == null) return "No radio connected.";
        if (rig.DiversityReady) return null;

        string detail = NonEmpty(rig.DiversityGateMessage)
            ?? "Enhanced Signal Clarity is not available on this radio right now.";
        return "Enhanced Signal Clarity works on a diversity pair, so it needs everything "
             + "diversity needs. " + detail + ".";
    }

    /// <summary>
    /// Open the ESC dialog, wiring its delegates to the live rig.
    /// </summary>
    private void ShowEscDialog()
    {
        var rig = Rig;
        if (rig == null) { SpeakNoRadio(); return; }

        var dialog = new Dialogs.EscDialog
        {
            Owner = System.Windows.Window.GetWindow(_window),
            GetEscEnabled = () => rig.EscEnabled,
            SetEscEnabled = v => rig.EscEnabled = v,
            GetPhaseShift = () => rig.EscPhaseShift,
            SetPhaseShift = v => rig.EscPhaseShift = v,
            GetEscGain = () => rig.EscGain,
            SetEscGain = v => rig.EscGain = v,
            HasActiveSlice = () => rig.HasActiveSlice,
            IsDiversityReady = () => rig.DiversityReady,
            IsDiversityOn = () => rig.DiversityOn,
            GetDiversityGateMessage = () => NonEmpty(rig.DiversityGateMessage)
        };
        dialog.ShowDialog();

        // Through FocusHome, the funnel that is correct with no radio — the
        // rig can go away while a dialog is open, and the frequency display is
        // collapsed behind the rescue page when it does.
        _window.FocusHome();
    }

    /// <summary>
    /// Why the antenna tuner cannot be offered on this radio, or null when it
    /// can.
    ///
    /// <para>ATU is a HARDWARE gate rather than a licence gate, which is why
    /// Track A left it — but the asymmetry AdvancedNrGateMessage encodes still
    /// governs the wording, and it bites harder here than it looks. FlexLib's
    /// ATUPresent and ATUEnabled are plain bools that start false, so "the
    /// radio said no tuner" and "the radio has not said anything yet" are the
    /// SAME value. We therefore never assert that the radio has no tuner. What
    /// we can say truthfully in every case is that it has not reported one, and
    /// then name the likeliest reason without claiming it about this
    /// radio.</para>
    ///
    /// <para>The one genuinely distinguishable case gets its own rung: a tuner
    /// the radio reported as fitted but not allowed. Both halves of that came
    /// from the radio, so stating it claims nothing we were not told.</para>
    /// </summary>
    private string? AtuGateMessage()
    {
        var rig = Rig;
        if (rig == null) return "No radio connected.";

        if (rig.HasATU) return null;

        if (rig.ATUHardwarePresent)
        {
            return "Your radio reports that it has an antenna tuner fitted, but that the tuner "
                 + "is not currently allowed to be used. Nothing on this computer can change "
                 + "that — it is set on the radio itself.";
        }

        return "This radio has not reported an antenna tuner, so the tuner controls and ATU "
             + "Tune are not offered. On some models the tuner is an optional part that may "
             + "never have been fitted. Tuning your antenna is then a job for an external "
             + "tuner, and Tools, Feature Availability lists what this radio did report.";
    }

    /// <summary>
    /// Build receiver controls (AGC, Squelch, RF Gain) — shared between menus.
    /// </summary>
    private void BuildReceiverItems(IntPtr parent)
    {
        if (Rig == null) return;

        AddWired(parent, "AGC Mode", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            // Cycle: Off → Slow → Medium → Fast → Off
            var mode = Rig.AGCSpeed;
            var newMode = mode switch
            {
                AGCMode.Off => AGCMode.Slow,
                AGCMode.Slow => AGCMode.Medium,
                AGCMode.Medium => AGCMode.Fast,
                AGCMode.Fast => AGCMode.Off,
                _ => AGCMode.Medium
            };
            Rig.AGCSpeed = newMode;
            SpeakAfterMenuClose(Radios.Lexicon.Get("audio.agc.mode", ("newMode", newMode)));
        });

        AddWired(parent, "AGC Threshold Up", () =>
            AdjustValue("AGC Threshold", () => Rig.AGCThreshold, v => Rig.AGCThreshold = v,
                FlexBase.AGCThresholdIncrement, FlexBase.AGCThresholdMin, FlexBase.AGCThresholdMax));
        AddWired(parent, "AGC Threshold Down", () =>
            AdjustValue("AGC Threshold", () => Rig.AGCThreshold, v => Rig.AGCThreshold = v,
                -FlexBase.AGCThresholdIncrement, FlexBase.AGCThresholdMin, FlexBase.AGCThresholdMax));

        AddSep(parent);

        AddChecked(parent, "Squelch On/Off", () =>
            ToggleDSP("Squelch", () => Rig.Squelch, v => Rig.Squelch = v),
            () => Rig?.Squelch == FlexBase.OffOnValues.on);
        AddWired(parent, "Squelch Level Up", () =>
            AdjustValue("Squelch", () => Rig.SquelchLevel, v => Rig.SquelchLevel = v,
                FlexBase.SquelchLevelIncrement, FlexBase.SquelchLevelMin, FlexBase.SquelchLevelMax));
        AddWired(parent, "Squelch Level Down", () =>
            AdjustValue("Squelch", () => Rig.SquelchLevel, v => Rig.SquelchLevel = v,
                -FlexBase.SquelchLevelIncrement, FlexBase.SquelchLevelMin, FlexBase.SquelchLevelMax));

        AddSep(parent);

        AddWired(parent, "RF Gain Up", () =>
            AdjustValue("RF Gain", () => Rig.RFGain, v => Rig.RFGain = v,
                Rig.RFGainIncrement, Rig.RFGainMin, Rig.RFGainMax));
        AddWired(parent, "RF Gain Down", () =>
            AdjustValue("RF Gain", () => Rig.RFGain, v => Rig.RFGain = v,
                -Rig.RFGainIncrement, Rig.RFGainMin, Rig.RFGainMax));
    }

    /// <summary>
    /// QB Track I — build the Transmit submenu contents. ONE builder, two
    /// doors: Radio → Transmit (Alt+R, T — the addressable path Noel asked
    /// for) and Slice → Transmission both call this, so the two submenus can
    /// never drift apart. Covers the full ScreenFields Transmission expander
    /// (menu-parity audit finding class a: power had NO menu path anywhere).
    /// Explicit &amp; mnemonics are deliberate here — native Win32 menus render
    /// them as underlined access keys and NVDA reads them cleanly (the old
    /// "no ampersands" guideline was about WinForms MenuStrip labels).
    /// </summary>
    private void BuildTransmitItems(IntPtr parent)
    {
        if (Rig == null)
        {
            AddWired(parent, "Connect a radio first", SpeakNoRadio);
            return;
        }

        // --- Power cluster ---
        AddWired(parent, "&Power...", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            var dlg = new Dialogs.PowerDialog(Rig);
            dlg.ShowDialog();
        });
        AddChecked(parent, "Tune &Carrier\tCtrl+Shift+T", () =>
            _window.ToggleTuneCarrier(),
            () => Rig?.TxTune == true);

        AddSep(parent);

        // --- Antenna (the XVTR/power relationship lives here) ---
        BuildTxAntennaSubmenu(parent);

        AddSep(parent);

        // --- Voice chain ---
        AddChecked(parent, "&VOX On/Off", () =>
            ToggleDSP("VOX", () => Rig.Vox, v => Rig.Vox = v),
            () => Rig?.Vox == FlexBase.OffOnValues.on);
        AddWired(parent, "Mic Gain &Up", () =>
            AdjustValue("Mic Gain", () => Rig.MicGain, v => Rig.MicGain = v, 5, 0, 100));
        AddWired(parent, "Mic Gain &Down", () =>
            AdjustValue("Mic Gain", () => Rig.MicGain, v => Rig.MicGain = v, -5, 0, 100));
        AddChecked(parent, "Mic &Boost (+20 dB)", () =>
            ToggleDSP("Mic Boost", () => Rig.MicBoost, v => Rig.MicBoost = v),
            () => Rig?.MicBoost == FlexBase.OffOnValues.on);
        AddChecked(parent, "Mic Bias (low-voltage electret mic power — not 48-volt phantom)", () =>
            ToggleDSP("Mic Bias", () => Rig.MicBias, v => Rig.MicBias = v),
            () => Rig?.MicBias == FlexBase.OffOnValues.on);
        AddChecked(parent, "Co&mpander", () =>
            ToggleDSP("Compander", () => Rig.Compander, v => Rig.Compander = v),
            () => Rig?.Compander == FlexBase.OffOnValues.on);
        AddWired(parent, "Compander Level Up", () =>
            AdjustValue("Compander Level", () => Rig.CompanderLevel, v => Rig.CompanderLevel = v, 5, 0, 100));
        AddWired(parent, "Compander Level Down", () =>
            AdjustValue("Compander Level", () => Rig.CompanderLevel, v => Rig.CompanderLevel = v, -5, 0, 100));
        AddChecked(parent, "&Speech Processor", () =>
            ToggleDSP("Speech Processor", () => Rig.ProcessorOn, v => Rig.ProcessorOn = v),
            () => Rig?.ProcessorOn == FlexBase.OffOnValues.on);
        AddWired(parent, "Processor Mode", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            // Cycle: Normal → DX → DX+ → Normal
            var next = (FlexBase.ProcessorSettings)(((int)Rig.ProcessorSetting + 1) % 3);
            Rig.ProcessorSetting = next;
            string label = next switch
            {
                FlexBase.ProcessorSettings.DX => Radios.Lexicon.Get("audio.processor.name_dx"),
                FlexBase.ProcessorSettings.DXX => Radios.Lexicon.Get("audio.processor.name_dx_plus"),
                _ => Radios.Lexicon.Get("audio.processor.name_normal")
            };
            SpeakAfterMenuClose(Radios.Lexicon.Get("audio.processor.mode", ("label", label)));
        });

        AddSep(parent);

        // --- Monitor ---
        // Mnemonic O — "&TX Antenna" above owns T in this menu.
        AddChecked(parent, "TX M&onitor", () =>
            ToggleDSP("TX Monitor", () => Rig.Monitor, v => Rig.Monitor = v),
            () => Rig?.Monitor == FlexBase.OffOnValues.on);
        AddWired(parent, "Monitor Level Up", () =>
            AdjustValue("Monitor Level", () => Rig.SBMonitorLevel, v => Rig.SBMonitorLevel = v, 5, 0, 100));
        AddWired(parent, "Monitor Level Down", () =>
            AdjustValue("Monitor Level", () => Rig.SBMonitorLevel, v => Rig.SBMonitorLevel = v, -5, 0, 100));

        // --- TX filter ---
        var txFilterSub = AddSubmenu(parent, "TX &Filter");
        AddWired(txFilterSub, "Low Edge Up", () =>
            AdjustValue("TX filter low", () => Rig.TXFilterLow, v => Rig.TXFilterLow = v, TxFilterStep(), 0, 9950));
        AddWired(txFilterSub, "Low Edge Down", () =>
            AdjustValue("TX filter low", () => Rig.TXFilterLow, v => Rig.TXFilterLow = v, -TxFilterStep(), 0, 9950));
        AddWired(txFilterSub, "High Edge Up", () =>
            AdjustValue("TX filter high", () => Rig.TXFilterHigh, v => Rig.TXFilterHigh = v, TxFilterStep(), 50, 10000));
        AddWired(txFilterSub, "High Edge Down", () =>
            AdjustValue("TX filter high", () => Rig.TXFilterHigh, v => Rig.TXFilterHigh = v, -TxFilterStep(), 50, 10000));
        AddWired(txFilterSub, "Read TX Filter", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            SpeakAfterMenuClose(Radios.Lexicon.Get("audio.tx_filter.range",
                ("low", Rig.TXFilterLow), ("high", Rig.TXFilterHigh)));
        });

        AddSep(parent);

        // --- Safety ---
        AddChecked(parent, "Dummy &Load Mode", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            Rig.DummyLoadMode = !Rig.DummyLoadMode;
            EarconPlayer.ToggleTone(Rig.DummyLoadMode);
            if (Rig.DummyLoadMode)
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.dummy_load.on"));
            else
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.dummy_load.off", ("power", Rig.XmitPower)));
        },
        () => Rig?.DummyLoadMode == true);
    }

    #endregion

    #region Main Menu Bar

    private IntPtr BuildMenuBar()
    {
        var bar = CreateMenu();

        // === Radio ===
        var radio = AddPopup(bar, "&Radio");
        if (Rig != null && Rig.IsConnected)
            AddWired(radio, "Disconnect", DisconnectAndSaySo);
        else
            AddWired(radio, "Connect to Radio", () => ConnectWithConfirmation());
        AddWired(radio, "Radio Rescue", ShowRadioRescue);
        AddWired(radio, "Manage SmartLink Accounts", () => _window.ShowSmartLinkAccountManager());
        AddWired(radio, "MultiFlex Clients", () => _window.ShowMultiFlexDialog());
        AddChecked(radio, "Auto-Connect Enabled",
            () => { var msg = _window.ToggleAutoConnect(); if (msg != null) SpeakAfterMenuClose(msg); },
            () => _window.IsAutoConnectEnabled?.Invoke() ?? false);
        AddWired(radio, "Clear Auto-Connect",
            () => { var msg = _window.ClearAutoConnect(); if (msg != null) SpeakAfterMenuClose(msg); });
        // QB Track A stub audit (2026-08-07): these three were AddNotImplemented
        // leftovers from the native-menu migration whose implementations
        // already existed — wire, don't rebuild.
        AddWired(radio, "Operators", () =>
        {
            if (_window.ShowOperatorsCallback != null)
                _window.ShowOperatorsCallback();
            else
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.operators.unavailable"));
        });
        AddWired(radio, "Profiles", () => ShowManageProfilesDialog());
        // ── Profiles on This Radio (#450, #451, #499, #501) ──
        //    The Settings Radios tab is the better long-term home for the
        //    per-radio choice — that is #451's own work — but the answer needs
        //    SOMEWHERE to be given now, because the connect announcement names
        //    it and a sentence that names a place the operator cannot find is a
        //    receipt for a dead end.
        //
        //    THE GRANULARITY IS THE POINT (#501). Until now the only lever was
        //    "load ALL my profiles", which rewrites a borrowed radio's setup —
        //    so an operator who correctly declines it cannot operate at all.
        //    The middle answer, "use my transmit audio here and nothing else",
        //    is applied to the radio's LIVE state and saves nothing on it.
        if (Rig != null && Rig.IsConnected)
        {
            var profilesSub = AddSubmenu(radio, "Profiles on This Radio");

            // The three answers, mutually exclusive. Checkmark only; the row
            // text is never rewritten with a state suffix.
            AddChecked(profilesSub, "Use My Transmit Audio Here",
                () => SetGuestProfileIntent(Radios.ProfileGuestIntent.UseMyTransmitAudio),
                () => Rig?.ProfileIntent == Radios.ProfileGuestIntent.UseMyTransmitAudio);
            AddChecked(profilesSub, "Load All My Profiles Here",
                () => SetGuestProfileIntent(Radios.ProfileGuestIntent.LoadMineAndPutBack),
                () => Rig?.ProfileIntent == Radios.ProfileGuestIntent.LoadMineAndPutBack);
            AddChecked(profilesSub, "Leave This Radio's Profiles Alone",
                () => SetGuestProfileIntent(Radios.ProfileGuestIntent.LeaveAlone),
                () => Rig?.ProfileIntent == Radios.ProfileGuestIntent.LeaveAlone);

            // Which of the operator's transmit-audio profiles to use.
            var whichSub = AddSubmenu(profilesSub, "Which Transmit Audio");
            var presetNames = Rig.LocalTransmitAudioProfileNames();
            if (presetNames.Count == 0)
            {
                AddWired(whichSub, "No transmit audio profiles saved yet", () =>
                    SpeakAfterMenuClose(Radios.Lexicon.Get("settings.profile_guest.no_presets")),
                    enabled: false);
            }
            else
            {
                foreach (var name in presetNames)
                {
                    var n = name; // capture per iteration
                    AddChecked(whichSub, n, () => SetGuestTransmitAudioProfile(n),
                        () => string.Equals(Rig?.LocalTransmitAudioProfileChoice, n,
                                            System.StringComparison.OrdinalIgnoreCase));
                }
            }

            // The one-press restore of an autosave a prior session left off
            // (#499). Present ONLY when actually owed, so it is never a verb
            // that announces its own absence (#121).
            if (Rig.RadioProfileAutosaveOwedBackOn)
            {
                AddWired(radio, "Turn Profile Autosave Back On", () =>
                {
                    switch (Rig.TurnRadioProfileAutosaveBackOn())
                    {
                        case Radios.GuardedOutcome.Done:
                            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.profile_guest.autosave_restored"));
                            break;
                        case Radios.GuardedOutcome.Skipped:
                            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.profile_guest.autosave_already_on"));
                            break;
                        case Radios.GuardedOutcome.Refused:
                            break; // the guard already spoke
                        default:
                            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.profile_guest.autosave_restore_failed"));
                            break;
                    }
                    RebuildCurrentMenu();
                });
            }

            // Offered put-back of a live transmit-audio snapshot a prior
            // session left (#499). Present only when there is one.
            if (Rig.HasStrandedLiveTransmitAudioSnapshot)
            {
                AddWired(radio, "Put This Radio's Own Transmit Audio Back", () =>
                {
                    SpeakAfterMenuClose(Rig.RestoreStrandedLiveTransmitAudio());
                    RebuildCurrentMenu();
                });
            }

            // Offered put-back of an EARLIER BUILD's restore point (the marker
            // design, superseded but still recognised). Present only when one
            // is on the radio.
            if (Rig.HasStrandedProfileRestorePoint)
            {
                AddWired(radio, "Put This Radio's Own Profiles Back", () =>
                {
                    var types = Rig.StrandedProfileRestorePoints.ToList();
                    SpeakAfterMenuClose(Rig.RestoreStrandedProfiles(types));
                    RebuildCurrentMenu();
                });
            }
        }
        AddWired(radio, "Connected Stations", () => ShowConnectedStations());
        // LocalPTT is a one-way claim: FlexLib only supports granting local
        // PTT to this client, never releasing it from here — hence "On".
        AddChecked(radio, "Local PTT On", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            if (Rig.LocalPTT)
            {
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.ptt.local_already_on"));
                return;
            }
            Rig.LocalPTT = true;
            // #128: a boolean that just became true answers back. Only the
            // success path tones — the already-on path above changed nothing,
            // and the rule is tied to the transition, not to the click.
            EarconPlayer.ToggleTone(true);
            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.ptt.local_on"));
        }, () => Rig?.LocalPTT == true);

        // QB Track I — the addressable transmit path Noel asked for:
        // Alt+R → T → P walks to the Power dialog with accelerators the whole
        // way. Same builder as Slice → Transmission (one room, two doors).
        var radioTxSub = AddSubmenu(radio, "&Transmit");
        BuildTransmitItems(radioTxSub);

        var loggingSub = AddSubmenu(radio, "Logging");
        // Stub audit (2026-08-21): Log Characteristics opens the real dialog
        // through the LogFileName command — it works outside Logging mode,
        // which is exactly where you set a log up. The Logging menu bar
        // builder explains why the other three stay honest stubs.
        AddCommand(loggingSub, "Log Characteristics", Radios.CommandValues.LogFileName);
        AddNotImplemented(loggingSub, "Import Log");
        AddNotImplemented(loggingSub, "Export Log");
        AddNotImplemented(loggingSub, "LOTW Merge");

        // ── Maintenance ── (QB Track A, 2026-08-07, Noel's call)
        // Second home for the radio-maintenance actions that otherwise live
        // only inside Settings → Radio Setup. Reboot goes through the SAME
        // shared flow as the hotkey and the Radio Setup step-7 button
        // (RadioMaintenance owns the no-radio announcement, the confirmation
        // that names other connected stations, and the deliberately absent
        // presence gate); firmware update opens the existing Radio Setup
        // surface whose step 3 is the firmware updater — no new firmware UI.
        AddSep(radio);
        AddWired(radio, "Reboot Radio", () =>
            RadioMaintenance.RebootWithConfirmation(Rig, _window.powerNowOff));
        AddWired(radio, "Update Radio Firmware", () => ShowSettingsDialog("Radio Setup"));

        AddSep(radio);
        AddWired(radio, "Exit", () => _window.CloseShellCallback?.Invoke());

        // === Slice ===
        string sliceLabel = Rig != null
            ? $"&Slice ({Rig.TotalNumSlices} of {Rig.MaxSlices})"
            : "&Slice";
        var slice = AddPopup(bar, sliceLabel);

        if (Rig != null)
        {
            // Selection with active slice checkmark.
            // QB Track I fix (Track J audit): iterate OUR slices only —
            // mySlices holds just this client's slices, so positions beyond
            // MyNumSlices rendered as numeric labels that silently no-opped
            // on click under MultiFlex. Other stations' slices get one
            // honest, speaking summary entry instead of dead rows.
            var selSub = AddSubmenu(slice, "Selection");
            for (int i = 0; i < Math.Min(Rig.MyNumSlices, 8); i++)
            {
                int sliceNum = i;
                AddChecked(selSub, $"Slice {Rig.VFOToLetter(i)}",
                    () =>
                    {
                        if (Rig == null || !Rig.ValidVFO(sliceNum)) { SpeakNoRadio(); return; }
                        Rig.RXVFO = sliceNum;
                        SpeakAfterMenuClose(Radios.Lexicon.Get("settings.slice.active",
                            ("letter", Rig.VFOToLetter(sliceNum))));
                    },
                    () => Rig?.RXVFO == sliceNum);
            }
            int otherSlices = Rig.TotalNumSlices - Rig.MyNumSlices;
            if (otherSlices > 0)
            {
                AddWired(selSub, $"{otherSlices} in use by other stations", () =>
                    SpeakAfterMenuClose(otherSlices == 1
                        ? Radios.Lexicon.Get("settings.slice.in_use_by_others_one",
                            ("otherSlices", otherSlices))
                        : Radios.Lexicon.Get("settings.slice.in_use_by_others_many",
                            ("otherSlices", otherSlices))));
            }

            AddSep(selSub);

            // New Slice
            AddWired(selSub, "New Slice", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                int countBefore = Rig.MyNumSlices;
                if (Rig.NewSlice())
                    SpeakAfterMenuClose(Radios.Lexicon.Get("settings.slice.created",
                        ("count", countBefore + 1)));
                else
                    SpeakAfterMenuClose(Radios.Lexicon.Get("settings.slice.cannot_create_maximum"));
            });

            // Release Slice (only when WE have more than one slice — another
            // station's slices are not ours to release).
            // QB Track I fix (Track J audit): the old label baked the slice
            // letter in at menu-BUILD time while the handler acted on the
            // click-time RXVFO — label/action drift whenever the active slice
            // changed in between. The label no longer names a letter; the
            // click-time announcement speaks the letter actually released.
            if (Rig.MyNumSlices > 1)
            {
                AddWired(selSub, "Release Active Slice", () =>
                {
                    if (Rig == null) { SpeakNoRadio(); return; }
                    int toRemove = Rig.RXVFO;
                    if (Rig.MyNumSlices <= 1)
                    {
                        SpeakAfterMenuClose(Radios.Lexicon.Get("settings.slice.cannot_release_last"));
                        return;
                    }
                    int switchTo = -1;
                    for (int j = 0; j < Rig.MyNumSlices; j++)
                    {
                        if (j != toRemove) { switchTo = j; break; }
                    }
                    if (switchTo >= 0)
                    {
                        string removedLetter = Rig.VFOToLetter(toRemove);
                        int countBefore = Rig.MyNumSlices;
                        if (Rig.CanTransmit && toRemove == Rig.TXVFO)
                            Rig.TXVFO = switchTo;
                        Rig.RXVFO = switchTo;
                        if (Rig.RemoveSlice(toRemove))
                            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.slice.released",
                                ("letter", removedLetter), ("count", countBefore - 1)));
                        else
                            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.slice.cannot_release_this"));
                    }
                });
            }

            // ── Making the change stick, from where the change was made ──
            //
            // Sprint 33 Track K, #117 and #59. New Slice and Release Active
            // Slice both work, both say so, and neither survives a disconnect,
            // because the radio restores its slice layout from its own global
            // profile on the next connect. Sprint 32 added the spoken receipt
            // that says so. What no surface said was what to DO about it.
            //
            // Noel, who owns the radio: "I don't know ... what I need to do in
            // JJ Flexible to get it to stick in the radio." The procedure
            // existed the whole time, four steps away on a different menu. An
            // answer that far from the question is not discoverable, so the
            // answer moves to the question — same reasoning, and the same
            // idiom, as the "See MultiFlex Clients on the Radio menu" pointer a
            // few lines above.
            //
            // The label says STATION and not SLICE deliberately. This writes a
            // global profile, which is the whole station — every slice, and
            // everything else the radio keeps in there. An operator who came
            // here thinking about one slice has to learn that in the label, not
            // afterwards from the consequences.
            AddSep(selSub);
            AddWired(selSub, "Save Station Setup to Radio", SaveStationSetupFromMenu);

            // QB Track I — Transmit Slice submenu, mirroring Selection: one
            // entry per slice with the checkmark on the current TX slice,
            // plus an explicit clear. This is the discoverable door for what
            // was previously only the hidden T keypress on the Slice field.
            // Letters come from Slice.Letter via VFOToLetter (the radio's
            // truth — Track J's identity rule), never positional arithmetic.
            if (Rig.CanTransmit)
            {
                // MyNumSlices, not TotalNumSlices — TX can only move between
                // OUR slices (same J-audit dead-entry class as Selection).
                var txSliceSub = AddSubmenu(slice, "Transmit S&lice");
                for (int i = 0; i < Math.Min(Rig.MyNumSlices, 8); i++)
                {
                    int sliceNum = i;
                    AddChecked(txSliceSub, $"Slice {Rig.VFOToLetter(i)}",
                        () =>
                        {
                            if (Rig == null || !Rig.ValidVFO(sliceNum)) { SpeakNoRadio(); return; }
                            Rig.TXVFO = sliceNum;
                            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.slice.transmit",
                                ("letter", Rig.VFOToLetter(sliceNum))));
                        },
                        () => Rig?.TXVFO == sliceNum);
                }
                AddSep(txSliceSub);
                AddChecked(txSliceSub, "No Transmit Slice",
                    () =>
                    {
                        if (Rig == null) { SpeakNoRadio(); return; }
                        if (!Rig.HasTransmitSlice)
                        {
                            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.slice.no_transmit_slice"));
                            return;
                        }
                        Rig.ClearTransmitSlice();
                        SpeakAfterMenuClose(Radios.Lexicon.Get("settings.slice.transmit_cleared"));
                    },
                    () => Rig?.HasTransmitSlice == false);
            }

            // Mode
            var modeSub = AddSubmenu(slice, "Mode");
            foreach (string modeName in RigCaps.ModeTable)
            {
                string m = modeName;
                // Add accelerator hints for modes with hotkeys
                string accel = m switch
                {
                    "USB" => "\tAlt+U",
                    "LSB" => "\tAlt+L",
                    "CW" => "\tAlt+C",
                    "AM" => "\tAlt+A",
                    "FM" => "\tAlt+F",
                    "DIGU" => "\tAlt+D",
                    "DIGL" => "\tAlt+Shift+D",
                    _ => ""
                };
                // Marked, so the menu can answer "what mode am I in?" (#311).
                // It went in through AddWired for eighteen sprints, which takes
                // no state getter — so no mode row could ever carry MF_CHECKED
                // and a screen reader had nothing to announce. The operator had
                // to leave the menu to find out, then come back.
                AddRadioChecked(modeSub, m + accel, () =>
                {
                    if (Rig == null) { SpeakNoRadio(); return; }
                    Rig.Mode = m;
                    SpeakAfterMenuClose(Radios.Lexicon.Get("settings.mode.selected", ("m", m)));
                },
                () => string.Equals(Rig?.Mode, m, StringComparison.OrdinalIgnoreCase));
            }
            AddSep(modeSub);
            AddWired(modeSub, "Next Mode\tAlt+M", () => _window.CycleMode(1));
            AddWired(modeSub, "Previous Mode\tAlt+Shift+M", () => _window.CycleMode(-1));

            // Audio
            var audioSub = AddSubmenu(slice, "Audio");
            // Every row, including Audio Workshop, comes from AudioMenuLayout.
            // This used to append the workshop itself, as did the Classic Audio
            // menu below — the same row wired in two places, which is a second
            // copy of the menu's order living outside the menu's order.
            BuildAudioItems(audioSub);

            // Slice management
            BuildSliceItems(slice);

            // Tuning — Sprint 26 Phase 8 expansion per Don's 2026-04-20 request.
            // "JJ Flexible in threes" pattern: every action on this submenu is
            // also reachable as a field keypress (on the FreqOut) or a global
            // hotkey. The menu is the third, discovery-friendly surface.
            var tuningSub = AddSubmenu(slice, "Tuning");

            // Classic/Modern mode toggle — also on Tools menu; duplicated here
            // so operators looking for "tuning" don't have to know it lives
            // under Tools.
            AddChecked(tuningSub, "Classic Tuning Mode\tCtrl+Shift+M",
                () => _window.ToggleUIMode(),
                () => _window.ActiveUIMode == MainWindow.UIMode.Classic);

            // Modern-mode-only tuning action. Sprint 29 Track F (tuning unity)
            // dropped the coarse/fine toggle and the step-cycling pair — Up and
            // Shift+Up now do what their names suggest, and the step values
            // themselves live in Settings → Tuning. The remaining menu entry
            // is the "what are my steps right now?" announcement.
            AddWired(tuningSub, "Speak Current Step\tShift+S", () => _window.TuningMenuSpeakStep());

            AddSep(tuningSub);

            AddWired(tuningSub, "RIT On/Off", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                var rit = new FlexBase.RITData(Rig.RIT);
                rit.Active = !rit.Active;
                Rig.RIT = rit;
                EarconPlayer.ToggleTone(rit.Active);
                SpeakAfterMenuClose(rit.Active
                    ? Radios.Lexicon.Get("settings.rit.on")
                    : Radios.Lexicon.Get("settings.rit.off"));
            });
            AddWired(tuningSub, "XIT On/Off", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                var xit = new FlexBase.RITData(Rig.XIT);
                xit.Active = !xit.Active;
                Rig.XIT = xit;
                EarconPlayer.ToggleTone(xit.Active);
                SpeakAfterMenuClose(xit.Active
                    ? Radios.Lexicon.Get("settings.xit.on")
                    : Radios.Lexicon.Get("settings.xit.off"));
            });

            // Receiver
            var rxSub = AddSubmenu(slice, "Receiver");
            BuildReceiverItems(rxSub);

            // DSP
            var dspSub = AddSubmenu(slice, "DSP");
            BuildDSPItems(dspSub);

            // Antenna — RX/TX select, ATU (always), Diversity (always).
            // QB Track I: mnemonic N ("Audio" owns first-letter A), so
            // exploration by accelerator reaches it directly.
            var antSub = AddSubmenu(slice, "A&ntenna");
            BuildAntennaSelectItems(antSub);
            AddSep(antSub);
            // Sprint 31 Track R — the last silent absence in this submenu. The
            // four ATU items used to vanish whole on a radio without a tuner,
            // and from the keyboard "missing" and "not for this radio" feel
            // identical while only one of them is true. Same treatment Track A
            // gave Diversity directly below.
            string? atuGate = AtuGateMessage();
            if (atuGate != null)
            {
                AddWired(antSub, "Antenna tuner unavailable", () =>
                    SpeakAfterMenuClose(AtuGateMessage()
                        ?? Radios.Lexicon.Get("settings.atu.available_reopen")));
            }
            else
            {
                BuildATUItems(antSub);
                AddSep(antSub);
                AddWired(antSub, "ATU Tune\tCtrl+T", () => _window.StartATUTuneCycle());
            }
            // Unconditional as of Sprint 30 Track A. The old
            // DiversityHardwareSupported guard meant a 1-SCU radio got no
            // diversity entry at all; BuildDiversityItems now explains itself
            // instead, which is the difference between "not for this radio"
            // and "this app forgot".
            {
                AddSep(antSub);
                BuildDiversityItems(antSub);
                BuildEscItems(antSub);
            }

            // Transmission (was "FM" — renamed for consistency with Classic
            // menu). QB Track I: contents now come from the shared
            // BuildTransmitItems builder — identical to Radio → Transmit, so
            // the two doors can never drift apart.
            var txSub = AddSubmenu(slice, "Transmission");
            BuildTransmitItems(txSub);
        }
        else
        {
            AddWired(slice, "Connect a radio first", SpeakNoRadio);
        }

        // === Band ===
        var band = AddPopup(bar, "&Band");
        if (Rig != null)
        {
            BuildBandItems(band);
        }
        else
        {
            AddWired(band, "Connect a radio first", SpeakNoRadio);
        }

        // === Filter ===
        var filter = AddPopup(bar, "Filt&er");
        if (Rig != null)
        {
            BuildFilterItems(filter);
        }
        else
        {
            AddWired(filter, "Connect a radio first", SpeakNoRadio);
        }

        // === ScreenFields (Panel Navigation) ===
        var screenFields = AddPopup(bar, "Scree&nFields");
        AddChecked(screenFields, "Show Field Panel", () =>
        {
            var panel = _window.FieldsPanel;
            bool newVisible = panel.Visibility != Visibility.Visible;
            panel.Visibility = newVisible ? Visibility.Visible : Visibility.Collapsed;
            _window.FieldsPanelUserVisible = newVisible;
            // Traced because this menu item is the ONLY operator-facing control
            // for the panel, so it is the first thing a "where did my fields go"
            // investigation must be able to rule in or out. On 2026-09-01 it was
            // suspected, wrongly, and the capture could not say (#500).
            // The SaveFieldsPanelVisibleCallback?.Invoke that stood here was a
            // no-op: the callback was never assigned anywhere. Deleted with it.
            Tracing.TraceLine($"ScreenFields: Show Field Panel toggled to {newVisible}",
                TraceLevel.Info);
            EarconPlayer.ToggleTone(newVisible);
            SpeakAfterMenuClose(newVisible
                ? Radios.Lexicon.Get("settings.field_panel.shown")
                : Radios.Lexicon.Get("settings.field_panel.hidden"));
        }, () => _window.FieldsPanel.Visibility == Visibility.Visible);
        AddSep(screenFields);
        AddWired(screenFields, "Noise Reduction and DSP\tCtrl+Shift+N",
            () => _window.FieldsPanel.ToggleCategory(0));
        AddWired(screenFields, "Audio\tCtrl+Shift+U",
            () => _window.FieldsPanel.ToggleCategory(1));
        AddWired(screenFields, "Receiver\tCtrl+Shift+R",
            () => _window.FieldsPanel.ToggleCategory(2));
        AddWired(screenFields, "Transmission\tCtrl+Shift+X",
            () => _window.FieldsPanel.ToggleCategory(3));
        AddWired(screenFields, "Antenna\tCtrl+Shift+A",
            () => _window.FieldsPanel.ToggleCategory(4));

        // === Audio ===
        var audio = AddPopup(bar, "Audi&o");
        // Same rows, same order, same shape whether a radio is connected or
        // not — see AudioMenuLayout. Parity with the Slice, Audio submenu is
        // now structural rather than something each caller has to remember.
        BuildAudioItems(audio);

        // === Tools ===
        var tools = AddPopup(bar, "&Tools");
        AddWired(tools, "Command Finder", () => ShowCommandFinderDialog());
        AddWired(tools, "Settings", () => ShowSettingsDialog());
        // Second door to the Settings Radios tab — per-radio configuration is
        // a destination of its own (rename a radio, pin its connection mode),
        // so it gets a direct entry rather than making users know which tab
        // holds it. Same one-concept-two-doors pattern as Radio Setup.
        AddWired(tools, "Configure Radio", () => ShowSettingsDialog("Radios"));
        // Sprint 30 Track D — the front door of the reporting pipeline, and the
        // replacement for the retired Help > Tracing. Tools is the operations
        // menu; there is no Operations menu in the native menu bar, which is
        // why CLAUDE.md's long-standing "Operations > Tracing" pointer was
        // wrong. Same deep-link pattern as Configure Radio.
        // Sprint 34 — the Fixer Tool's door. A submenu from the start rather
        // than a single item, ruled by Noel 2026-08-25: the tool is a stage
        // runner over a stage SET, and connection problems are the next set.
        // A submenu that grows costs nobody a relearn; an item that becomes a
        // submenu later costs everybody one.
        var fixSub = AddSubmenu(tools, "Fix");
        AddWired(fixSub, "Transmit problems...", () =>
            // MainWindow is a UserControl hosted in WinForms, not a Window, so
            // the owner is resolved the same way every other dialog here does
            // it. It can legitimately come back null; JJFlexDialog owns itself
            // to the main window in that case.
            Dialogs.FixerDialog.Show(() => Rig, System.Windows.Window.GetWindow(_window)));
        // Sprint 35 Track B — test runs persist as they happen (#251), so the
        // Test ID on a report is quotable against a saved copy. This is the
        // door to those copies: view, export, delete. It sits inside Fix
        // because a saved run is a Fix artifact, and the submenu is already
        // ruled to grow.
        //
        // NAMED FOR WHAT YOU CAN DO, not for what is behind it (#381). Noel:
        // "You have a menu that is called 'saved check runs' I'd might say view
        // or resume saved test runs." The old name told an operator what was in
        // there and not why they would open it — and the reason is the one
        // thing they cannot guess, that a stopped run can be picked up. The
        // exit prompt has offered exactly that since Sprint 40; this is the
        // other door to it and it advertised nothing.
        AddWired(fixSub, "View or resume saved test runs...", () =>
            Dialogs.FixerPastRunsDialog.Show(() => Rig,
                System.Windows.Window.GetWindow(_window)));

        // Sprint 36 Track C (#271) — the QSO signal analyzer's saved captures:
        // view, rename, export, delete. Top level of Tools, not inside Fix — a
        // capture is a measurement of a contact, not a repair artifact, and
        // the stop announcement names this exact route ("under Tools, Signal
        // captures"), so the words and the menu must agree.
        AddWired(tools, "Signal captures...", () =>
            Dialogs.SignalCapturesDialog.Show(System.Windows.Window.GetWindow(_window)));

        // RENAMED from "Diagnostics" 2026-08-25, and the rename is the point.
        // This item does not diagnose anything — it deep-links to the settings
        // tab that turns the diagnostic LOG on and saves a capture. Sitting a
        // thing called "Diagnostics" next to a thing called "Fix" invited
        // exactly the wrong guess about which one finds your problem. The name
        // now says what it is; "Diagnostic Log" is also what the design doc and
        // the help context already call it, so this narrows the vocabulary
        // rather than widening it.
        AddWired(tools, "Diagnostic Log", () => ShowSettingsDialog("Diagnostics"));
        // Sprint 29 Track D — manual update check. Lives next to Settings
        // since the Updates settings tab is its preference home; this entry
        // is the single-action trigger for the same flow.
        AddWired(tools, "Check for Updates", () => ShowCheckForUpdates());
        AddWired(tools, "Speak Status\tCtrl+Shift+S", () =>
        {
            // Delay speech so it fires after menu close + focus return,
            // otherwise NVDA's focus announcement stomps on the status.
            _window.Dispatcher.BeginInvoke(async () =>
            {
                await System.Threading.Tasks.Task.Delay(250);
                _window.SpeakStatusCallback?.Invoke();
            });
        });
        AddWired(tools, "Status Dialog\tCtrl+Alt+S", () =>
            _window.ShowStatusDialogCallback?.Invoke());
        // QB Track A stub audit: the registered StationLookup command was
        // already working (Ctrl+L, Command Finder) — the menu item was the
        // only dead door. Route through ExecuteCommandCallback so the menu,
        // the hotkey, and the Command Finder share one dispatch path.
        AddCommand(tools, "Station Lookup\tCtrl+L", CommandValues.StationLookup);
        AddSep(tools);
        AddWired(tools, "Enter Logging Mode", () => _window.EnterLoggingMode());
        AddChecked(tools, "Classic Tuning Mode\tCtrl+Shift+M",
            () => _window.ToggleUIMode(),
            () => _window.ActiveUIMode == MainWindow.UIMode.Classic);
        AddSep(tools);
        // QB Track H (2026-08-07): the Hotkey Editor is the editable door into
        // the one Keys surface; Help → Key Assignments is the viewing door.
        AddWired(tools, "Hotkey Editor", () => ShowKeysSurface(editable: true));
        // QB Track A stub audit: band-plan data (HamBands.Bands) and the WPF
        // ShowBandsDialog both existed, unconnected. Works without a radio —
        // the band table is static data.
        AddWired(tools, "Band Plans", () => ShowBandPlansDialog());
        AddWired(tools, "Feature Availability", () => ShowFeatureAvailability());
        // GPS / GNSS status and reference-oscillator selection. Lives next to
        // Feature Availability because it answers the same shape of question:
        // what hardware does this radio actually have, and is it working.
        AddWired(tools, "GPS and Reference", () =>
        {
            // 2026-08-06: reported "No radio connected" from this item while
            // connected, which the connected-state trace contradicts. Three
            // paths speak near-identical words, so each one traces and speaks
            // distinctly until the repro names the guilty path.
            var gpsRig = Rig;
            Tracing.TraceLine($"Menu: GPS and Reference (rig={(gpsRig == null ? "null" : "present")})", TraceLevel.Info);
            if (gpsRig == null) { SpeakAfterMenuClose(Radios.Lexicon.Get("settings.gps.no_radio")); return; }
            try
            {
                new Dialogs.GpsStatusDialog(gpsRig).ShowDialog();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Menu: GPS and Reference dialog failed: {ex}", TraceLevel.Error);
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.gps.window_failed"));
            }
        });
        // #420, DRAFT WORDING — the three items below all write text, so "as
        // Text" named nothing, and both exports prepare for a restore, so "for
        // Restore" named nothing either. Both were the wrong axis.
        //
        // The one thing that genuinely separates them is whether the item OPENS
        // THE STORED PROFILES, and everything else follows from it: opening a
        // profile means loading it on the radio, which moves the station,
        // shows up on every connected client, takes a minute or two, and is
        // best-effort to put back. Not opening them means an instant read that
        // touches nothing.
        //
        // So each label now ends with what it does to the radio, in the same
        // words, and all three diverge on the FIRST word — a menu is arrowed
        // through and heard, not scanned.
        AddWired(tools, "Profile comparison report, loads each one", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            // The restore export walks profiles in the background; two walks
            // at once would restore each other's wrong state.
            if (ProfileReporter.WalkInProgress)
            {
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.restore_export.busy"));
                return;
            }
            var report = ProfileReporter.GenerateReport(Rig);
            var path = ProfileReporter.SaveReport(report);
            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.profile.report_saved", ("path", path)));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        });
        // #227: everything the radio holds, as plain text, BEFORE a factory
        // reset takes it all. Read-only and safe any time — unlike Profile
        // Report above, which loads each profile to compare them. The path is
        // SPOKEN, not just shown: an export the operator cannot find is not
        // an export.
        AddWired(tools, "Quick settings export, read only", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            try
            {
                var export = ProfileReporter.GenerateStationSettingsExport(Rig);
                var path = ProfileReporter.SaveStationSettingsExport(
                    export, Rig.ConnectedSerial);
                SpeakAfterMenuClose(Radios.Lexicon.Get(
                    "settings.station_export.saved", ("path", path)));
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Quick settings export: {ex}", TraceLevel.Error);
                SpeakAfterMenuClose(Radios.Lexicon.Get(
                    "settings.station_export.failed", ("error", ex.Message)));
            }
        });
        // The RESTORE-GRADE export — the capture half of #414. A radio's
        // settings largely live INSIDE its profiles, and the text export
        // above cannot see into a profile it has not loaded. This one walks
        // every stored profile — global, TX, mic — and writes what each holds
        // as key = value lines, then puts the original profiles back and
        // checks. Background, because the walk loads each profile on the
        // radio in turn and takes a minute or two: frozen UI for that long
        // reads as a hang. Progress and the saved path are SPOKEN.
        AddWired(tools, "Full restore capture, loads every profile", () =>
        {
            var restoreRig = Rig;
            if (restoreRig == null) { SpeakNoRadio(); return; }
            if (restoreRig.Transmit)
            {
                // The walk rewrites power, filters and antennas mid-load.
                SpeakAfterMenuClose(Radios.Lexicon.Get(
                    "settings.restore_export.not_while_transmitting"));
                return;
            }
            if (ProfileReporter.WalkInProgress)
            {
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.restore_export.busy"));
                return;
            }
            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.restore_export.start"));
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Give the start announcement room to finish: the first
                    // progress line interrupts, and the one sentence that
                    // sets expectations must not lose the race to it.
                    System.Threading.Thread.Sleep(4000);
                    var export = ProfileReporter.GenerateRestoreGradeExport(restoreRig,
                        progress => Radios.ScreenReaderOutput.Speak(
                            progress,
                            Radios.Speech.SpeechIntent.Latest,
                            Radios.VerbosityLevel.Terse,
                            coalesceKey: "restore-export-progress"));
                    if (export == null)
                    {
                        Radios.ScreenReaderOutput.Speak(
                            Radios.Lexicon.Get("settings.restore_export.busy"),
                            Radios.VerbosityLevel.Terse, interrupt: true);
                        return;
                    }
                    var savedPath = ProfileReporter.SaveRestoreGradeExport(
                        export.Text, restoreRig.ConnectedSerial);
                    // Critical on purpose: this is the delayed answer to a
                    // command the operator gave a minute ago, and the
                    // not-put-back variant is a safety fact — the radio may
                    // be sitting on the wrong profile.
                    string doneKey = !export.WalkRan
                        ? "settings.restore_export.saved_no_walk"
                        : export.EverythingPutBack
                            ? "settings.restore_export.saved_put_back"
                            : "settings.restore_export.saved_not_put_back";
                    Radios.ScreenReaderOutput.Speak(
                        Radios.Lexicon.Get(doneKey, ("path", savedPath)),
                        Radios.VerbosityLevel.Critical, interrupt: true);
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(savedPath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"Full restore capture: {ex}", TraceLevel.Error);
                    Radios.ScreenReaderOutput.Speak(
                        Radios.Lexicon.Get("settings.restore_export.failed", ("error", ex.Message)),
                        Radios.VerbosityLevel.Critical, interrupt: true);
                }
            });
        });
        AddSep(tools);
        AddWired(tools, "Export Profiles", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            bool success = Rig.ExportProfileDatabase();
            if (!success)
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.profile.export_failed"));
        });
        AddWired(tools, "Import Profiles", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            bool success = Rig.ImportProfileDatabase();
            if (!success)
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.profile.import_failed"));
        });
        AddSep(tools);
        AddWired(tools, "View Test Results", () => _window.ShowTestResultsCallback?.Invoke());
        // #329: this was AddNotImplemented for as long as anyone can remember,
        // while both WPF dialogs for it sat finished in the tree since Sprint 9
        // and the Hotkey Editor was already telling operators that CW message
        // keys are "managed under CW Messages".
        AddWired(tools, "Manage CW Messages", () => _window.ManageCWMessagesCallback?.Invoke());
        AddSep(tools);
        AddWired(tools, "Audio Workshop\tCtrl+Shift+W", () =>
            Dialogs.AudioWorkshopDialog.ShowOrFocus(Rig, 0));

        // === Help (shared) ===
        BuildHelpPopup(bar);

        return bar;
    }

    private void BuildBandItems(IntPtr parent)
    {
        // Helper: check if current RX frequency is in the given band
        Func<Bands.BandNames, bool> isOnBand = (band) =>
        {
            if (Rig == null) return false;
            var bi = Bands.Query(Rig.RXFrequency);
            return bi != null && bi.Band == band;
        };

        // Main bands (F3-F9)
        AddChecked(parent, "160m\tF3", () => _window.BandJump(Bands.BandNames.m160), () => isOnBand(Bands.BandNames.m160));
        AddChecked(parent, "80m\tF4", () => _window.BandJump(Bands.BandNames.m80), () => isOnBand(Bands.BandNames.m80));
        AddChecked(parent, "40m\tF5", () => _window.BandJump(Bands.BandNames.m40), () => isOnBand(Bands.BandNames.m40));
        AddChecked(parent, "20m\tF6", () => _window.BandJump(Bands.BandNames.m20), () => isOnBand(Bands.BandNames.m20));
        AddChecked(parent, "15m\tF7", () => _window.BandJump(Bands.BandNames.m15), () => isOnBand(Bands.BandNames.m15));
        AddChecked(parent, "10m\tF8", () => _window.BandJump(Bands.BandNames.m10), () => isOnBand(Bands.BandNames.m10));
        AddChecked(parent, "6m\tF9", () => _window.BandJump(Bands.BandNames.m6), () => isOnBand(Bands.BandNames.m6));
        AddSep(parent);

        // WARC bands (Shift+F3-F6)
        AddChecked(parent, "60m\tShift+F3", () => _window.BandJump(Bands.BandNames.m60), () => isOnBand(Bands.BandNames.m60));
        AddChecked(parent, "30m\tShift+F4", () => _window.BandJump(Bands.BandNames.m30), () => isOnBand(Bands.BandNames.m30));
        AddChecked(parent, "17m\tShift+F5", () => _window.BandJump(Bands.BandNames.m17), () => isOnBand(Bands.BandNames.m17));
        AddChecked(parent, "12m\tShift+F6", () => _window.BandJump(Bands.BandNames.m12), () => isOnBand(Bands.BandNames.m12));
        AddSep(parent);

        // Navigation
        AddWired(parent, "Band Up\tAlt+Up", () => _window.BandNavigate(1));
        AddWired(parent, "Band Down\tAlt+Down", () => _window.BandNavigate(-1));
        AddSep(parent);

        // 60m channel navigation
        AddWired(parent, "60m Channel Up\tAlt+Shift+Up", () => _window.SixtyMeterChannelNavigate(1));
        AddWired(parent, "60m Channel Down\tAlt+Shift+Down", () => _window.SixtyMeterChannelNavigate(-1));
    }

    #endregion

    #region Logging Menu Bar

    private IntPtr BuildLoggingMenuBar()
    {
        var bar = CreateMenu();

        // === Log ===
        // Stub audit (2026-08-21): five of these answered "not yet
        // implemented" for features that already work through the command
        // layer — most visibly Log Statistics, which Ctrl+J, L had been
        // speaking for sprints while the menu denied it existed. The stubs
        // that remain are honest: Import/Export/LOTW Merge have WPF dialogs
        // built (Sprint 9 Track B) but no glue to a log session yet, and
        // Reset Confirmations has no implementation anywhere.
        var log = AddPopup(bar, "&Log");
        AddCommand(log, "New Entry\tCtrl+N", Radios.CommandValues.NewLogEntry);
        AddCommand(log, "Write Entry\tCtrl+W", Radios.CommandValues.LogFinalize);
        AddCommand(log, "Search Log\tCtrl+Shift+F", Radios.CommandValues.SearchLog);
        // #310: this was AddNotImplemented because LogOpenFullForm dead-ended
        // in a MainWindow stub, and the comment here said so — an honest "not
        // yet" in preference to a silent no-op, which was the right call at the
        // time. The stub is gone and the command reaches the same full log form
        // Alt+C already opens, so the menu can have it.
        AddCommand(log, "Full Log Form\tCtrl+Alt+L", Radios.CommandValues.LogOpenFullForm);
        AddSep(log);
        // LogFileName is the working door to the characteristics dialog, and
        // Ctrl+Shift+N (CommandValues.LogCharacteristicsDialog) now arrives at
        // the same one — #310 pointed its callout at the shared route rather
        // than at the stub it used to dead-end in. Either would do here; this
        // one stays because it is the route the other two callers already use.
        AddCommand(log, "Log Characteristics", Radios.CommandValues.LogFileName);
        AddNotImplemented(log, "Import Log");
        AddNotImplemented(log, "Export Log");
        AddNotImplemented(log, "LOTW Merge");
        AddSep(log);
        AddCommand(log, "Log Statistics\tCtrl+J, L", Radios.CommandValues.LogStats);
        AddSep(log);
        AddNotImplemented(log, "Reset Confirmations");

        // === Navigate ===
        var navigate = AddPopup(bar, "&Navigate");
        AddStub(navigate, "First Entry");
        AddStub(navigate, "Previous Entry");
        AddStub(navigate, "Next Entry");
        AddStub(navigate, "Last Entry");

        // === Mode ===
        var mode = AddPopup(bar, "&Mode");
        AddWired(mode, "Exit to Classic Tuning", () =>
        {
            _window.LastNonLogMode = MainWindow.UIMode.Classic;
            _window.ExitLoggingMode();
        });
        AddWired(mode, "Exit to Modern Tuning", () =>
        {
            _window.LastNonLogMode = MainWindow.UIMode.Modern;
            _window.ExitLoggingMode();
        });

        // === Help (shared) ===
        BuildHelpPopup(bar);

        return bar;
    }

    #endregion

    #region Help Menu (shared)

    private void BuildHelpPopup(IntPtr bar)
    {
        var help = AddPopup(bar, "&Help");
        // #40 residual: "What's New" was the only Help item WITH a mnemonic,
        // which made the menu's access-key story inconsistent. Every item
        // carries one now, unique within the menu (see the class doc's
        // ampersand carve-out — native menus render them cleanly).
        AddWired(help, "&Help Topics\tF1", () => HelpLauncher.ShowHelp());
        AddWired(help, "Keyboard &Reference", () => HelpLauncher.ShowHelp("KeyboardReference"));
        AddWired(help, "What's &New", () => HelpLauncher.ShowHelp("WhatsNew"));
        AddSep(help);
        // QB Track H (2026-08-07): ONE Key Assignments item (the old
        // Alphabetical / By Function duplicates opened the same dialog three
        // times over). Arrangement is a combo inside the surface now.
        AddWired(help, "Key &Assignments", () => ShowKeysSurface(editable: false));
        // Help > Tracing is GONE. It opened a dialog that could not tell you
        // whether tracing was on, started traces the archive could not see, and
        // wrote them to Documents where nothing rotates or bundles them. Its job
        // is now Tools > Diagnostics, which deep-links to Settings >
        // Diagnostics. See docs/planning/active/diagnostic-log-surface.md §2.
        AddSep(help);
        AddWired(help, "&Earcon Explorer", () =>
            Dialogs.AudioWorkshopDialog.ShowOrFocus(Rig, "Earcon Explorer"));
        AddSep(help);
        // "b" because Key Assignments owns A.
        AddWired(help, "A&bout", () =>
        {
            var dialog = new Dialogs.AboutDialog
            {
                Rig = Rig,
                SpeakCallback = (msg, interrupt) => Radios.ScreenReaderOutput.Speak(msg, interrupt)
            };
            dialog.ShowDialog();
            // Restore focus to the JJ Flexible Home after the dialog closes.
            // Without this, WPF returns focus to whatever unnamed parent
            // container was last focused, and screen readers announce
            // generic role text like "pane" — leaving the user unsure of
            // where they are. Re-focusing FreqOut triggers the standard
            // home-arrival announcement via DisplayBox_GotKeyboardFocus,
            // restoring orientation cleanly. 2026-04-24 fix flagged by Noel
            // after Phase 8c-ii About dialog rework.
            //
            // Through FocusHome (Sprint 30 Track A): with no radio the
            // frequency display is COLLAPSED behind the rescue page, and
            // Focus() on a collapsed element quietly fails — which is the exact
            // "focus lands nowhere" this comment was written to prevent.
            _window.FocusHome();
        });
    }

    #endregion

    #region Helpers

    /// <summary>
    /// BUG-023: If already connected, confirm before connecting to a different radio.
    /// </summary>
    private void ConnectWithConfirmation()
    {
        if (Rig != null && Rig.IsConnected)
        {
            var confirm = new Dialogs.ConfirmActionDialog(
                Radios.Lexicon.Get("connect.switch_radio.title"),
                Radios.Lexicon.Get("connect.switch_radio.body"),
                question: Radios.Lexicon.Get("connect.switch_radio.question"),
                yesLabel: Radios.Lexicon.Get("connect.switch_radio.yes_label"));
            if (confirm.ShowDialog() != true) return;
        }

        // Sprint 42 Track D (#395): the whole connect flow — picker,
        // Connecting window, Start — runs inside this call, and its window
        // churn must not narrate itself over the connect narration. The end
        // request sits in a finally so a cancelled picker or a thrown
        // connect can never leave the scope stuck; the finish decides
        // between "the narration owns the announcement" and "run the
        // return-to-app landing".
        _window.BeginConnectFlowQuiet("menu connect command", door: true);
        try
        {
            _window.SelectRadioCallback?.Invoke();
        }
        finally
        {
            _window.EndConnectFlowQuiet("menu connect flow returned", door: true);
        }
    }

    /// <summary>Add a popup (dropdown) menu to the menu bar.</summary>
    private IntPtr AddPopup(IntPtr menuBar, string text)
    {
        var popup = CreatePopupMenu();
        AppendMenuW(menuBar, MF_POPUP, (UIntPtr)popup, text);
        // Track name for screen reader announcement (strip & accelerator prefix)
        _popupNames[popup] = text.Replace("&", "");
        return popup;
    }

    /// <summary>Add a submenu to a parent popup menu.</summary>
    private IntPtr AddSubmenu(IntPtr parentPopup, string text)
    {
        var sub = CreatePopupMenu();
        AppendMenuW(parentPopup, MF_POPUP, (UIntPtr)sub, text);
        return sub;
    }

    /// <summary>
    /// Add a menu item with a specific handler. Pass <paramref name="enabled"/>
    /// false to grey it — the row stays present, stays arrow-reachable, and
    /// stays where it was, which is what keeps first-letter navigation stable
    /// (#214). A greyed item raises no WM_COMMAND, so its handler will not run.
    /// </summary>
    private void AddWired(IntPtr popup, string text, Action handler, bool enabled = true)
    {
        int id = _nextId++;
        AppendMenuW(popup, MF_STRING | (enabled ? 0 : MF_GRAYED), (UIntPtr)id, text);
        _handlers[id] = handler;
        _itemNames[id] = text;
    }

    /// <summary>Add a checkable menu item — checkmark updated dynamically via WM_INITMENUPOPUP.</summary>
    private void AddChecked(IntPtr popup, string text, Action handler, Func<bool> stateGetter,
                            bool enabled = true)
    {
        int id = _nextId++;
        AppendMenuW(popup, MF_STRING | (enabled ? 0 : MF_GRAYED), (UIntPtr)id, text);
        _handlers[id] = handler;
        _itemNames[id] = text;
        _checkItems.Add((popup, id, stateGetter, text, enabled));
    }

    /// <summary>
    /// Add one member of a mutually exclusive group — a mode, a band, a
    /// selection. Checkmark only: the row's text is never rewritten with a
    /// state suffix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists rather than an <see cref="AddChecked"/> call
    /// (#311).</b> The slice Mode submenu had no state at all — every entry
    /// went in through <c>AddWired</c>, which takes no state getter, so
    /// nothing could ever set MF_CHECKED and a screen reader had nothing to
    /// report. Arrowing the menu was a guess-and-exit loop: leave to hear what
    /// mode you are in, then go back in. Every neighbouring submenu — slice
    /// select, TX slice, Classic tuning, the band jumps — already marked its
    /// state, which is what made this one surprising.
    /// </para>
    /// <para>
    /// <b>But it is not the toggle case.</b> <c>AddChecked</c> also rewrites
    /// the row as "{text}: On" or "{text}: Off" so a toggle announces its
    /// state in words. Applied to ten modes that reads out nine "Off"s on the
    /// way to one "On", and on a row whose text is <c>"USB\tAlt+U"</c> the
    /// suffix lands inside the accelerator column. The checkmark alone is what
    /// a radio group needs, and it is what the reader already speaks.
    /// </para>
    /// </remarks>
    private void AddRadioChecked(IntPtr popup, string text, Action handler, Func<bool> stateGetter,
                                 bool enabled = true)
    {
        AddChecked(popup, text, handler, stateGetter, enabled);
        // The id AddChecked just allocated, taken from the row it just
        // registered rather than predicted from _nextId. Predicting it works
        // today and would silently mark the wrong row the day id allocation
        // changes — and a wrong row here is a mode that announces itself
        // "checked" when it is not.
        _radioGroupItems.Add(_checkItems[^1].id);
    }

    // ── Verbs that are not there yet, and how they should say so ──
    //
    // Sprint 32 Track H. Both helpers below used to add a NORMAL, enabled menu
    // item that announced its own absence only after the operator had arrowed
    // to it and pressed Enter. That asymmetry is the whole problem: a sighted
    // user sees a greyed item, reads "unavailable" from its appearance in
    // passing, and never tries it — while a keyboard and screen-reader operator
    // pays the full round trip first, every time, on every one of the nineteen
    // items that used these.
    //
    // Disable, hide, or label — any of the three beats it. These do two: the
    // item is greyed, so the screen reader says "unavailable" as the operator
    // arrives on it, and the state is in the LABEL too, so the announcement
    // carries the reason rather than only the fact. Greyed items are still
    // reachable by arrow key on Windows menus, so nothing becomes undiscoverable
    // — the operator learns the feature exists and that it is not ready, in one
    // pass, without pressing anything.
    //
    // The handlers stay wired. A disabled item raises no WM_COMMAND so they will
    // not run, but they cost nothing and they are the record of what the item is
    // meant to become.

    /// <summary>
/// Add a menu item that routes through the registered command layer —
    /// the same dispatch the hotkeys and the Command Finder use, so the menu
    /// can never grow its own diverging version of a feature. Exists because
    /// the Logging menu's Log Statistics item still answered "not yet
    /// implemented" for sprints after Ctrl+J, L started working: the same
    /// feature gave two different answers depending on which door you came
    /// through, and the menu — the door a new operator finds first — was the
    /// one that lied (stub audit, 2026-08-21).
    /// </summary>
    private void AddCommand(IntPtr popup, string text, Radios.CommandValues command)
    {
        // Strip the accelerator column and mnemonic marker for speech.
        string spokenName = text.Split('	')[0].Replace("&", "");
        AddWired(popup, text, () =>
        {
            if (_window.ExecuteCommandCallback != null)
                _window.ExecuteCommandCallback(command);
            else
                SpeakAfterMenuClose(Radios.Lexicon.Get("settings.menu.not_available",
                    ("spokenName", spokenName)));
        });
    }

    /// <summary>
    /// Add a menu item whose implementation is not wired yet. Greyed, and says
    /// so in its label. Speaks an honest "not yet implemented" — the old text
    /// claimed "not yet connected to radio", which was a lie for stubs and sent
    /// users hunting for a connection problem that didn't exist (QB Track A,
    /// 2026-08-07).
    /// </summary>
    private void AddNotImplemented(IntPtr popup, string text)
    {
        int id = _nextId++;
        AppendMenuW(popup, MF_STRING | MF_GRAYED, (UIntPtr)id,
            $"{text} - not yet implemented");
        _itemNames[id] = text + " (not implemented)";
        _handlers[id] = () =>
        {
            Tracing.TraceLine($"Menu: {text} (not yet wired)", TraceLevel.Info);
            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.menu.not_implemented", ("text", text)));
        };
    }

    /// <summary>Add a stub menu item, greyed, labelled "coming soon".</summary>
    private void AddStub(IntPtr popup, string text)
    {
        int id = _nextId++;
        AppendMenuW(popup, MF_STRING | MF_GRAYED, (UIntPtr)id, $"{text} - coming soon");
        _itemNames[id] = text + " (coming soon)";
        _handlers[id] = () =>
        {
            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.menu.coming_soon", ("text", text)));
        };
    }

    /// <summary>
    /// Speak a message after the menu closes. Uses a 150ms delay so the screen reader
    /// picks up the speech after the menu closes and focus returns to the main window.
    /// </summary>
    private void SpeakAfterMenuClose(string message,
        Radios.VerbosityLevel level = Radios.VerbosityLevel.Terse)
    {
        _window.Dispatcher.BeginInvoke(async () =>
        {
            // 500ms: NVDA needs time to process menu-close event internally.
            // Interrupt: cut off NVDA's window title re-announcement so user hears
            // the actual result (e.g., "Antenna 1") instead of the title first.
            await System.Threading.Tasks.Task.Delay(500);
            Radios.ScreenReaderOutput.Speak(message, level, interrupt: true);
        });
    }

    /// <summary>
    /// Radio ▸ Radio Rescue — Noel's name, and his reason: the rescue page
    /// arrives on its own three minutes after the radio goes, and nobody should
    /// have to wait that out when they already know the session is over.
    ///
    /// <para>Present on EVERY radio in every state rather than appearing only
    /// while disconnected, which is the same rule as the gated items below: an
    /// item that comes and goes teaches the operator nothing, while one that is
    /// always there and explains itself teaches them what it is for. It also
    /// makes the item discoverable BEFORE the day they need it, which is the
    /// only day exploring a menu is expensive.</para>
    /// </summary>
    private void ShowRadioRescue()
    {
        if (Rig != null && Rig.IsConnected)
        {
            // Never tear down a working session from a menu item whose name
            // sounds helpful. Explain instead, and name the radio so the
            // operator can tell this is a real connection and not a stale claim.
            string name = NonEmpty(Rig.RadioNickname)
                ?? Radios.Lexicon.Get("connect.rescue.your_radio_fallback");
            SpeakAfterMenuClose(Radios.Lexicon.Get(
                "connect.rescue.already_connected", ("name", name)));
            return;
        }

        if (_window.InRescueMode)
        {
            _window.FocusHome();
            SpeakAfterMenuClose(Radios.Lexicon.Get("connect.rescue.already_showing"));
            return;
        }

        // The operator asked for it, so the lead is short — they do not need to
        // be told why they are here. Focus goes through FocusHome, the funnel
        // that is correct with no radio; the page's own name carries the rest.
        _window.EnterRescueMode(Radios.Lexicon.Get("connect.rescue.entering"));

        if (!_window.InRescueMode)
        {
            // EnterRescueMode declines while a rig object still exists, and one
            // survives a cancelled picker without ever having connected — the
            // exact state Track A documented in SelectRadio. Rather than let an
            // item the operator deliberately chose do nothing at all, say so.
            // Silence here would be the same defect this sprint is closing,
            // committed by the fix for it.
            SpeakAfterMenuClose(Radios.Lexicon.Get("connect.rescue.not_available_yet"));
            return;
        }

        _window.FocusHome();
    }

    /// <summary>
    /// Radio ▸ Disconnect — tear the radio down and SAY SO.
    ///
    /// <para>Sprint 31 Track R, from Noel on 2026-08-19: "disconnecting
    /// announces nothing." Established from the code rather than guessed at,
    /// and the cause is not the one it looked like. This item never used the
    /// PendingDisconnectLead mechanism at all — it called CloseRadioCallback
    /// (globals.CloseTheRadio) directly, a path that has never announced
    /// anything in any version. The lead belongs to SelectRadio, the SWITCH
    /// path, which hands its message to the radio picker that arrives a beat
    /// later. Disconnect opens no window, so it had nothing to hand a message
    /// to and simply said nothing.</para>
    ///
    /// <para>That also settles why speech is correct HERE when it is wrong
    /// there: the flush hazard is a window CHANGE. Disconnect produces none —
    /// the shell stays put and only Home's contents change — so an utterance
    /// timed after the menu closes survives to be heard. Same rule as always:
    /// information belongs to the surface that holds focus, and here that
    /// surface never moved.</para>
    ///
    /// <para>Critical rather than Terse: losing the radio is a state change the
    /// operator has to hear whatever their verbosity setting is.</para>
    ///
    /// <para>#360, 2026-08-28, measured from the operator's ears and a
    /// reader-side capture: pressing Disconnect produced 2.2 seconds of
    /// complete silence, then the Home landing announcement, then the news —
    /// "Disconnected" arriving 4.6 seconds after the keypress, BEHIND a
    /// sentence about where focus landed. The teardown runs synchronously on
    /// the UI thread for seconds, so the old shape — tear down, then
    /// SpeakAfterMenuClose — could not say anything until the work was done,
    /// and a keypress that produces nothing for two seconds reads as a dead
    /// key (#338's defect, recreated here). The shape now: acknowledge the
    /// press FIRST ("Disconnecting from X"), hand that to the reader, then run
    /// the teardown, then state the completed event — QUEUED, not
    /// interrupting, so it can never again cancel its own companions and
    /// arrives as the final word. The whole sequence is deferred past the
    /// menu-close beat because a native menu's dismissal makes the reader
    /// cancel and re-announce; anything spoken synchronously here dies in
    /// that cancel.</para>
    /// </summary>
    private void DisconnectAndSaySo()
    {
        // A second press inside the deferred half-second must not stack a
        // second teardown behind the first.
        if (_disconnectInFlight) return;

        var rig = Rig;
        string? radioName = null;

        // Sprint 33 Track K. The offer goes HERE and not inside
        // FlexBase.Disconnect, which is the obvious-looking place and is wrong:
        // Disconnect is also reached from Dispose and from application
        // shutdown, where putting up a modal means a dialog owned by a window
        // that is being torn down underneath it. This is the deliberate,
        // operator-initiated path, on the UI thread, with the application still
        // fully alive — the only place the question can be asked safely.
        //
        // Before SuppressSpeech is set, so the offer's own announcements are
        // heard, and before the teardown, which disposes the rig. When the
        // offer shows a dialog, the dialog itself acknowledges the keypress —
        // a window that arrives carries its own title.
        OfferStationSaveBeforeDisconnect(rig);

        if (rig != null)
        {
            radioName = NonEmpty(rig.RadioNickname);
            // Keep the radio layer quiet, exactly as SelectRadio does. FlexBase's
            // own message exists for UNEXPECTED drops, where nothing else is
            // explaining what happened; here we are the explanation, and two
            // voices racing is worse than either alone.
            try { rig.SuppressSpeech = true; } catch { /* never block the disconnect */ }
        }

        _disconnectInFlight = true;
        _window.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                // Same 500 ms SpeakAfterMenuClose waits: the reader needs the
                // menu-close event to finish before speech survives.
                await System.Threading.Tasks.Task.Delay(500);

                // The acknowledgment — the press has been heard, the work is
                // starting. Interrupting, to cut the reader's own window-title
                // re-announcement exactly as SpeakAfterMenuClose does.
                Radios.ScreenReaderOutput.Speak(
                    radioName == null
                        ? Radios.Lexicon.Get("connect.disconnecting_plain")
                        : Radios.Lexicon.Get("connect.disconnecting_from", ("radioName", radioName)),
                    Radios.VerbosityLevel.Critical, interrupt: true);

                // Read the name BEFORE this: the callback disposes the rig.
                // Blocks this thread for the duration of the teardown; the
                // acknowledgment is already the reader's to finish.
                _window.CloseRadioCallback?.Invoke();

                // The completed event, once it is true — queued, so it lands
                // behind the Home landing announcements instead of cancelling
                // them (the old interrupt=True here is what killed
                // "Disconnected" and "Session closed" mid-word in the #360
                // capture), and last, so the news is the final word.
                Radios.ScreenReaderOutput.Speak(
                    radioName == null
                        ? Radios.Lexicon.Get("connect.disconnected_plain")
                        : Radios.Lexicon.Get("connect.disconnected_from", ("radioName", radioName)),
                    Radios.Speech.SpeechIntent.Queue,
                    Radios.VerbosityLevel.Critical);
            }
            finally
            {
                _disconnectInFlight = false;
            }
        });
    }

    /// <summary>True from the moment Radio ▸ Disconnect is accepted until its
    /// deferred teardown-and-announce sequence finishes (#360).</summary>
    private bool _disconnectInFlight;

    /// <summary>Add a separator line.</summary>
    private void AddSep(IntPtr popup)
    {
        AppendMenuW(popup, MF_SEPARATOR, UIntPtr.Zero, null);
    }

    #endregion

    #region Dialog Launchers — Sprint 16 Track C

    /// <summary>
    /// Open the one Keys surface (QB Track H). Editable from Tools →
    /// Hotkey Editor; viewing from Help → Key Assignments. Works with or
    /// without a radio — key bindings are app state, not radio state.
    /// </summary>
    private void ShowKeysSurface(bool editable)
    {
        var commands = _window.KeyCommandsRef;
        if (commands == null)
        {
            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.keys.data_unavailable"));
            return;
        }
        var dialog = new Dialogs.KeysDialog(commands, editable);
        dialog.ShowDialog();
    }

    /// <summary>
    /// Show the Command Finder dialog with all available commands.
    /// </summary>
    private void ShowCommandFinderDialog()
    {
        var dialog = new Dialogs.CommandFinderDialog
        {
            GetCommands = () => _window.GetCommandFinderItemsCallback?.Invoke()
                ?? new List<Dialogs.CommandFinderItem>(),
            ExecuteCommand = (tag) => _window.ExecuteCommandCallback?.Invoke(tag),
            SpeakText = (msg) => Radios.ScreenReaderOutput.Speak(msg),
            CurrentMode = _window.ActiveUIMode.ToString()
        };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Public deep-link entry: open Settings, optionally already sitting on a
    /// named tab ("Radio Setup", "Network", ...). Used by advisory dialogs so
    /// "Open Radio Setup" is a button, not directions.
    /// </summary>
    public void OpenSettings(string? tab = null) => ShowSettingsDialog(tab);

    /// <summary>
    /// Show the Settings dialog (PTT, Tuning, License, Audio tabs).
    /// </summary>
    private void ShowSettingsDialog(string? openAtTab = null)
    {
        var pttConfig = _window.CurrentPttConfig ?? new PttConfig();
        var handlers = _window.FreqHandlers;
        int coarseStep = handlers?.CoarseTuneStep ?? 1000;
        int fineStep = handlers?.FineTuneStep ?? 10;
        var licenseConfig = handlers?.License;

        // User-scope config lives at BaseConfigDir (root). The FreqHandlers
        // GetConfigDirectory delegate returns BaseConfigDir regardless of
        // connection state -- this is how user-global preferences (calibration
        // unlocks, typing sound) stay visible in Settings even when no radio
        // is connected.
        string? rootConfigDir = handlers?.GetConfigDirectory?.Invoke();

        AudioOutputConfig audioConfig;
        if (_window.OpenParms != null)
        {
            // Connected: load the per-radio config (radio-specific fields) and
            // merge user-global fields from root. Root is authoritative for
            // TuningHash and TypingSound -- the unlock handler always writes
            // them there, and changes made in Settings OK also persist there.
            audioConfig = AudioOutputConfig.Load(_window.OpenParms.ConfigDirectory);
            if (!string.IsNullOrEmpty(rootConfigDir))
            {
                var rootConfig = AudioOutputConfig.Load(rootConfigDir);
                if (!string.IsNullOrEmpty(rootConfig.TuningHash))
                    audioConfig.TuningHash = rootConfig.TuningHash;
                if (rootConfig.TypingSound != TypingSoundMode.Beep)
                    audioConfig.TypingSound = rootConfig.TypingSound;
            }
            _window.CurrentAudioConfig = audioConfig;
        }
        else if (!string.IsNullOrEmpty(rootConfigDir))
        {
            // Disconnected: load directly from root. TuningHash (unlock state)
            // and TypingSound preference are visible; radio-specific fields
            // show whatever was last saved there. Changes persist back to
            // root on Settings OK.
            audioConfig = AudioOutputConfig.Load(rootConfigDir);
        }
        else
        {
            audioConfig = new AudioOutputConfig();
        }

        var dialog = new Dialogs.SettingsDialog(pttConfig, coarseStep, fineStep, licenseConfig, audioConfig);
        dialog.FreqHandlers = _window.FreqHandlers;
        // The Audio tab's device picker needs audioDevices.xml. Prefer the path
        // the connect handed us; fall back to the one globals publishes at
        // startup, so the picker still works with no radio connected.
        dialog.AudioDevicesFile =
            _window.OpenParms?.AudioDevicesFile ?? _window.AudioDevicesFilePath;
        dialog.Rig = _window.RigControl;
        // Settings → Radio Setup → Restart shares the hotkey binding's reboot flow;
        // this is the one piece it cannot reach on its own, since the display state
        // reset lives on MainWindow.
        dialog.OnRebootInitiated = _window.powerNowOff;
        if (_window.OpenParms != null)
        {
            dialog.ConfigDirectory = _window.OpenParms.ConfigDirectory;
            dialog.OperatorName = _window.OpenParms.GetOperatorName?.Invoke();
        }
        if (openAtTab != null && dialog.SelectTabByHeader(openAtTab))
        {
            // Sprint 31 Track R: selecting the tab is not the same as LANDING on
            // it. Selection is a visual fact; without focus the dialog opens and
            // the screen reader announces the title plus whatever WPF happened to
            // focus first, so a deep link taken from the rescue page could leave
            // an operator who cannot see the tab strip with no evidence they
            // arrived anywhere other than plain Settings.
            //
            // Focusing the selected category folds the arrival into the dialog's
            // own opening announcement — the same principle as
            // PendingDisconnectLead, applied to a category instead of a window
            // title. Deferred to Loaded because focus set before the window
            // exists is discarded; this is the sibling of the papercut already
            // commented in SettingsDialog ("focusing a field on an unselected
            // tab fails silently"), pointing the other way.
            //
            // Sprint 32 Track G (task #134): this was `landed.Focus()` on the
            // selected TabItem, which worked only while the tab strip was a
            // real focusable visual. Settings now navigates by a category list
            // and the strip is templated away, so focusing the TabItem would
            // silently do nothing — turning a deep link into exactly the
            // no-evidence arrival the comment above exists to prevent.
            dialog.Loaded += (_, _) => dialog.FocusCategory();
        }

        // Track C (OK/Apply convention): the app-side application + persistence
        // runs after EVERY successful commit — Apply-and-stay included — not
        // only after the dialog closes. This used to live in an
        // if-result-was-true block below, which would have made Apply a
        // dialog-local illusion that evaporated on Cancel-after-Apply.
        dialog.SettingsApplied = () =>
        {
            _window.ApplySettingsChanges(dialog.CoarseTuneStep, dialog.FineTuneStep);

            // Persist user-scope fields to root so they're available in any
            // subsequent session regardless of which radio is connected. When
            // connected, the per-radio config also gets the full save at
            // PowerOff (existing flow). When disconnected, this is the only
            // persist path for user changes made in Settings.
            //
            // CW fields matter at app startup BEFORE any connect -- the
            // MainWindow constructor loads these from root so CW delegates are
            // live + CwNotificationsEnabled is set in time for AS to fire at
            // connect-start. Without these being in root, AS was silently
            // skipping because the flag was false until per-radio PowerOn.
            if (!string.IsNullOrEmpty(rootConfigDir))
            {
                var rootConfig = AudioOutputConfig.Load(rootConfigDir);
                rootConfig.TuningHash = audioConfig.TuningHash;
                rootConfig.TypingSound = audioConfig.TypingSound;
                rootConfig.CwNotificationsEnabled = audioConfig.CwNotificationsEnabled;
                rootConfig.CwModeAnnounce = audioConfig.CwModeAnnounce;
                rootConfig.CwSidetoneHz = audioConfig.CwSidetoneHz;
                rootConfig.CwSpeedWpm = audioConfig.CwSpeedWpm;
                // Sprint 33 Track K. Settings are intents, not commands: the
                // operator may well turn this on while disconnected — it is a
                // preference ABOUT disconnecting — and without this line it
                // would be accepted, announced, and silently lost, which is the
                // exact defect class the intents rule exists to prevent.
                rootConfig.OfferStationSaveOnDisconnect =
                    audioConfig.OfferStationSaveOnDisconnect;
                // Sprint 33 Track F: the pitch source, the keying
                // waveform and the alert voice set are user-scope for
                // the same reason the four above are — they describe
                // this operator's ears, not this radio. Left out of
                // this list they would apply until the next restart
                // and then quietly revert, which is worse than not
                // offering them.
                rootConfig.CwPitchFollowsRadio = audioConfig.CwPitchFollowsRadio;
                rootConfig.CwWaveform = audioConfig.CwWaveform;
                rootConfig.EarconVoiceSet = audioConfig.EarconVoiceSet;
                rootConfig.Save(rootConfigDir);
            }
        };

        dialog.ShowDialog();

        // Always apply typing sound after Settings (config may have been saved even on Cancel)
        if (_window.FreqHandlers != null)
            _window.FreqHandlers.TypingSound = audioConfig.TypingSound;
    }

    /// <summary>
    /// Sprint 29 Track D — Tools → Check for Updates command. Manual
    /// trigger for the same flow Settings → Updates → Check now runs:
    /// fetch the manifest for the user's selected channel, compare
    /// against the running version, surface either an "up to date"
    /// confirmation or the Update available dialog.
    ///
    /// Defers when the user has an active radio session so we don't
    /// pull the rug out mid-QSO per the "no update prompts during
    /// active radio sessions" rule. They can still get the dialog
    /// from Settings → Updates if they want to force it.
    /// </summary>
    private void ShowCheckForUpdates()
    {
        if (Rig != null && Rig.IsConnected)
        {
            var confirm = new Dialogs.ConfirmActionDialog(
                Radios.Lexicon.Get("connect.update.advisory_title"),
                Radios.Lexicon.Get("connect.update.while_connected_body"),
                question: Radios.Lexicon.Get("connect.update.while_connected_question"),
                yesLabel: Radios.Lexicon.Get("connect.update.while_connected_yes_label"));
            if (confirm.ShowDialog() != true) return;
        }

        Radios.ScreenReaderOutput.Speak(
            Radios.Lexicon.Get("connect.update.checking"),
            Radios.VerbosityLevel.Terse, true);

        _ = RunUpdateCheckAsync();
    }

    private async System.Threading.Tasks.Task RunUpdateCheckAsync()
    {
        try
        {
            var settings = JJFlexUpdater.UpdaterSettings.Load();
            var service = new JJFlexUpdater.UpdaterService();
            var available = await service.CheckForUpdateAsync(settings.Channel)
                                         .ConfigureAwait(true);

            settings.LastCheckUtc = DateTimeOffset.UtcNow;
            settings.Save();

            if (available is null)
            {
                string upToDate = Radios.Lexicon.Get("connect.update.up_to_date",
                    ("channel", settings.Channel.ToDisplayString()));
                Radios.ScreenReaderOutput.Speak(
                    upToDate,
                    Radios.VerbosityLevel.Critical, true);
                MessageBox.Show(
                    upToDate,
                    Radios.Lexicon.Get("connect.update.check_title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // SkippedVersion: the user has already said "skip this one."
            // Honor that — they can still see it via Settings → Updates if
            // they want to reverse course.
            if (string.Equals(settings.SkippedVersion, available.AvailableVersion,
                              StringComparison.OrdinalIgnoreCase))
            {
                Radios.ScreenReaderOutput.Speak(
                    Radios.Lexicon.Get("connect.update.skipped_version",
                        ("version", available.AvailableVersion)),
                    Radios.VerbosityLevel.Critical, true);
                return;
            }

            var dialog = new Dialogs.UpdateAvailableDialog(available);
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            string unreachable = Radios.Lexicon.Get("connect.update.unreachable");
            Radios.ScreenReaderOutput.Speak(
                unreachable,
                Radios.VerbosityLevel.Critical, true);
            Dialogs.AdvisoryDialog.Show(Radios.Lexicon.Get("connect.update.advisory_title"),
                unreachable + "\n\n" + ex.Message);
        }
    }

    /// <summary>
    /// Show the connected-stations list (QB Track A stub audit). The WPF
    /// ShowStationNamesDialog existed unused since the migration; FlexBase
    /// exposes the station list directly.
    /// </summary>
    private void ShowConnectedStations()
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        List<string> stations;
        try
        {
            stations = Rig.Stations
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        catch (Exception ex)
        {
            Tracing.TraceLine($"ShowConnectedStations: {ex.Message}", TraceLevel.Error);
            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.stations.list_unreadable"));
            return;
        }
        if (stations.Count == 0)
        {
            // The dialog silently self-closes on an empty list — say why
            // nothing appeared instead of letting the click go silent.
            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.stations.none_connected"));
            return;
        }
        var dialog = new Dialogs.ShowStationNamesDialog { StationNames = stations };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Show the band-plans browser (QB Track A stub audit). Mirrors the old
    /// WinForms ShowBands form: pick a band (optionally license class and
    /// mode), get the band edges and division breakdown. Static data — works
    /// with no radio connected; when a radio is present the current band and
    /// a mode guess are preselected.
    /// </summary>
    private void ShowBandPlansDialog()
    {
        // Index 0 of the license/mode lists means "All" (no filter), matching
        // the enums' "none" member — the dialog treats index 0 the same way.
        var licenseNames = Enum.GetNames(typeof(Bands.Licenses))
            .Select(n => n == "none" ? "All" : n).ToArray();
        var modeNames = Enum.GetNames(typeof(Bands.Modes))
            .Select(n => n == "none" ? "All" : n).ToArray();
        var bandNames = Bands.TheBands.Select(b => b.Name).ToArray();

        int initialBand = -1;
        int initialMode = -1;
        var rig = Rig;
        if (rig != null)
        {
            var current = Bands.Query(rig.RXFrequency);
            if (current != null) initialBand = current.ID;
            string mode = rig.Mode ?? "";
            initialMode = mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase)
                ? (int)Bands.Modes.CW
                : (int)Bands.Modes.PhoneCW;
        }

        var dialog = new Dialogs.ShowBandsDialog
        {
            BandNames = bandNames,
            LicenseNames = licenseNames,
            ModeNames = modeNames,
            InitialBandIndex = initialBand,
            InitialLicenseIndex = 0,
            InitialModeIndex = initialMode,
            QueryBands = (bandIdx, licenseIdx, modeIdx) =>
            {
                if (bandIdx < 0) return null;
                var band = (Bands.BandNames)bandIdx;
                bool haveLicense = licenseIdx > 0;
                bool haveMode = modeIdx > 0;
                Bands.BandItem result;
                if (haveLicense && haveMode)
                    result = Bands.Query(band, (Bands.Licenses)licenseIdx, (Bands.Modes)modeIdx);
                else if (haveLicense)
                    result = Bands.Query(band, (Bands.Licenses)licenseIdx);
                else if (haveMode)
                    result = Bands.Query(band, (Bands.Modes)modeIdx);
                else
                    result = Bands.Query(band);
                return result == null ? "No band plan entry found." : FormatBandPlan(result);
            }
        };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Format a band-plan entry for the ShowBandsDialog result box.
    /// Frequencies in kHz, one division per line — screen-reader-friendly
    /// plain text, no tabs or columns.
    /// </summary>
    private static string FormatBandPlan(Bands.BandItem band)
    {
        static string KHz(ulong hz) => (hz / 1000).ToString();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{band.Name}: {KHz(band.Low)} to {KHz(band.High)} kilohertz");
        if (band.Divisions != null)
        {
            foreach (var d in band.Divisions)
            {
                var line = new System.Text.StringBuilder();
                line.Append($"{KHz(d.Low)} to {KHz(d.High)}");
                if (d.License != null && d.License.Length > 0)
                    line.Append(": " + string.Join(", ", d.License.Select(l => l.ToString())));
                if (d.Mode != null && d.Mode.Length > 0)
                    line.Append(" - " + string.Join(", ", d.Mode.Select(m => m.ToString())));
                sb.AppendLine(line.ToString());
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Show the Feature Availability tab of the RadioInfo dialog.
    /// QB Track L: the delegate is wired in MainWindow.OnRadioStarted; if it
    /// is somehow still null, say so — a menu item must never go silent.
    /// </summary>
    private void ShowFeatureAvailability()
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        if (Rig.ShowRadioInfoDialog == null)
        {
            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.radio_info.not_available"));
            return;
        }
        Rig.ShowRadioInfoDialog((int)Dialogs.RadioInfoTab.FeatureAvailability);
    }

    /// <summary>
    /// Show the Manage Profiles dialog.
    /// </summary>
    private void ShowManageProfilesDialog()
    {
        if (Rig == null) { SpeakNoRadio(); return; }

        var callbacks = new Dialogs.ProfileDialogCallbacks
        {
            GetDisplayItems = () =>
            {
                var items = new List<Dialogs.ProfileDisplayItem>();
                var types = new[] {
                    Radios.ProfileTypes.global,
                    Radios.ProfileTypes.tx,
                    Radios.ProfileTypes.mic
                };
                // The operator's own list, PLUS whatever the radio itself is
                // carrying that the operator has never adopted. Only the first
                // half was shown until Sprint 32 Track H, which meant a profile
                // created in another client was invisible here — and, worse,
                // that the uniqueness check on Add could not see it, so adding a
                // name the radio already used looked fine and then Save
                // overwrote the radio's profile without a word.
                foreach (var ptype in types)
                {
                    var profiles = Rig.GetProfilesByType(ptype);
                    if (profiles == null) continue;
                    foreach (var p in profiles)
                    {
                        string suffix = p.Default ? " (default)" : "";
                        string typeLabel = ptype.ToString().ToUpperInvariant();
                        items.Add(new Dialogs.ProfileDisplayItem
                        {
                            DisplayText = $"[{typeLabel}] {p.Name}{suffix}",
                            ProfileData = p
                        });
                    }
                }
                foreach (var p in RigOnlyProfiles())
                {
                    string typeLabel = p.ProfileType.ToString().ToUpperInvariant();
                    items.Add(new Dialogs.ProfileDisplayItem
                    {
                        DisplayText = $"[{typeLabel}] {p.Name} (on radio)",
                        ProfileData = p
                    });
                }
                return items;
            },
            GetProfileTypeNames = () => new[] { "Global", "TX", "MIC" },
            GetProfileNamesByType = (typeIndex) =>
            {
                var ptype = ProfileTypeFromIndex(typeIndex);
                // Both halves, for the reason in GetDisplayItems: a uniqueness
                // check that cannot see the radio's own profiles is not a
                // uniqueness check.
                var mine = Rig.GetProfilesByType(ptype)?.Select(p => p.Name)
                           ?? Enumerable.Empty<string>();
                var onRadio = RigOnlyProfiles()
                    .Where(p => p.ProfileType == ptype)
                    .Select(p => p.Name);
                return mine.Concat(onRadio);
            },
            OnAdd = (result) =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                var ptype = ProfileTypeFromIndex(result.ProfileTypeIndex);
                var profile = new Radios.Profile_t(result.Name, ptype, result.IsDefault);
                // Adds the NAME to the operator's list. Nothing is written to
                // the radio here — the dialog's Save button is the radio-side
                // write, and keeping the two separate is what makes "add a
                // global profile, then save it" the Save As that was missing
                // without ever letting a typo land on somebody's rig.
                if (Rig.AddOperatorProfile(profile))
                    SpeakAfterMenuClose(Radios.Lexicon.Get("settings.profile.added", ("profile", profile.Name)));
                else
                    SpeakAfterMenuClose(Radios.Lexicon.Get("settings.profile.add_failed", ("profile", profile.Name)));
            },
            OnUpdate = (originalData, result) =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                if (originalData is not Radios.Profile_t original)
                {
                    SpeakAfterMenuClose(Radios.Lexicon.Get("settings.profile.invalid_data"));
                    return;
                }
                var ptype = ProfileTypeFromIndex(result.ProfileTypeIndex);
                var replacement = new Radios.Profile_t(result.Name, ptype, result.IsDefault);
                if (Rig.UpdateOperatorProfile(original, replacement))
                    SpeakAfterMenuClose(Radios.Lexicon.Get("settings.profile.updated", ("profile", replacement.Name)));
                else
                    SpeakAfterMenuClose(Radios.Lexicon.Get("settings.profile.update_failed", ("profile", original.Name)));
            },
            OnDelete = (profileData) =>
            {
                if (Rig == null) { SpeakNoRadio(); return null; }
                if (profileData is Radios.Profile_t profile)
                {
                    // #486: a refusal is not a failure. When the change-nothing
                    // guard declines, it has ALREADY spoken (naming the setting
                    // and the route); returning an error string here would
                    // overwrite that with a generic one and send the operator
                    // to a bug report instead of to Settings. Only a genuine
                    // failure returns a message.
                    switch (Rig.DeleteProfileGuarded(profile))
                    {
                        case Radios.GuardedOutcome.Done:
                        case Radios.GuardedOutcome.Refused:
                            return null;
                        default:
                            return Radios.Lexicon.Get("settings.profile.delete_failed",
                                ("profile", profile.Name));
                    }
                }
                return Radios.Lexicon.Get("settings.profile.invalid_data");
            },
            OnSelect = (profileData) =>
            {
                if (Rig == null) { SpeakNoRadio(); return null; }
                if (profileData is Radios.Profile_t profile)
                {
                    // #486, the exact case Noel hit: the guard refused, spoke
                    // its explanation, and then a generic could-not-select
                    // message won and sent him to a bug report.
                    switch (Rig.SelectProfileGuarded(profile))
                    {
                        case Radios.GuardedOutcome.Done:
                            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.profile.selected",
                                ("profile", profile.Name)));
                            return null;
                        case Radios.GuardedOutcome.Refused:
                            // The guard already spoke; say nothing more.
                            return null;
                        default:
                            return Radios.Lexicon.Get("settings.profile.select_failed",
                                ("profile", profile.Name));
                    }
                }
                return Radios.Lexicon.Get("settings.profile.invalid_data");
            },
            OnSave = (profileData) =>
            {
                if (profileData is Radios.Profile_t profile)
                {
                    Rig.SaveProfile(profile, immediately: true);
                    SpeakAfterMenuClose(Radios.Lexicon.Get("settings.profile.saved", ("profile", profile.Name)));
                }
            },
            IsGlobalProfile = (profileData) =>
                profileData is Radios.Profile_t p && p.ProfileType == Radios.ProfileTypes.global,
            GetProfileEditData = (profileData) =>
            {
                if (profileData is Radios.Profile_t p)
                {
                    int typeIndex = p.ProfileType switch
                    {
                        Radios.ProfileTypes.global => 0,
                        Radios.ProfileTypes.tx => 1,
                        Radios.ProfileTypes.mic => 2,
                        _ => 0
                    };
                    return (p.Name, typeIndex, p.Default);
                }
                return ("", 0, false);
            }
        };

        var dialog = new Dialogs.ProfileDialog(callbacks);
        dialog.ShowDialog();
    }

    /// <summary>
    /// Slice ▸ Save Station Setup to Radio — the one-step answer to "how do I
    /// make this stick", reachable from the menu where the operator changed the
    /// thing they want to keep.
    /// </summary>
    /// <remarks>
    /// <para>This CONFIRMS rather than just doing it, and the confirmation is
    /// not ceremony. The operator has no other way to learn which global
    /// profile the radio currently has loaded — that name appears in no status
    /// line and no announcement — so the prompt is the only surface that
    /// answers "into what?" before the write happens rather than after. It also
    /// carries the scope correction: an operator who arrived here after
    /// releasing one slice is about to store the entire station.</para>
    ///
    /// <para>No suppressKey. A "don't show again" on this one would remove the
    /// only place the target profile is ever named, which is precisely the
    /// knowledge gap this exists to close — and the dialog's own rule is that
    /// suppression is for teaching text whose outcome is re-readable
    /// elsewhere. This outcome is not.</para>
    ///
    /// <para>The refusal path speaks the blocker instead of greying the item
    /// out. Settings are intents: the operator asked a reasonable question and
    /// deserves the reason, and "another operator is connected" is information
    /// they very much want, not a reason to go quiet.</para>
    /// </remarks>
    private void SaveStationSetupFromMenu()
    {
        if (Rig == null) { SpeakNoRadio(); return; }

        var blocker = Rig.StationLayoutSaveBlocker();
        if (blocker != null)
        {
            SpeakAfterMenuClose(blocker, Radios.VerbosityLevel.Critical);
            return;
        }

        string profileName = Rig.CurrentGlobalProfileName;

        // ── APPROVED BY NOEL, 2026-08-20 (Sprint 33 Track K) ──
        // THIS ROUTE CARRIES THE SHARED-STATE SENTENCE and the disconnect offer
        // does not. That split is deliberate: the disconnect prompt interrupts
        // someone who is already leaving, so it earns only what prevents a
        // mistake, while this is a menu item the operator deliberately chose and
        // has room to explain properly. If the sentence is ever removed from
        // here it has to go back there — the two were traded against each other.
        //
        // "what you changed" rather than "the slice you changed": several slices
        // may have moved, or one may have been released.
        //
        // An explicit noLabel, because the default "No" does not say what
        // happens. "Don't save" states the outcome, matching the disconnect
        // prompt's "Disconnect without saving".
        var confirm = new Dialogs.ConfirmActionDialog(
            Radios.Lexicon.Get("settings.station_layout.menu_title"),
            Radios.Lexicon.Get("settings.station_layout.menu_body",
                ("profileName", profileName)),
            warnings: new[]
            {
                Radios.Lexicon.Get("settings.station_layout.menu_warning_scope"),
                Radios.Lexicon.Get("settings.station_layout.menu_warning_shared",
                    ("profileName", profileName))
            },
            question: Radios.Lexicon.Get("settings.station_layout.menu_question",
                ("profileName", profileName)),
            yesLabel: Radios.Lexicon.Get("settings.station_layout.menu_yes_label"),
            noLabel: Radios.Lexicon.Get("settings.station_layout.menu_no_label"));

        if (confirm.ShowDialog() != true)
        {
            SpeakAfterMenuClose(Radios.Lexicon.Get("settings.station_layout.nothing_saved"));
            return;
        }

        var error = Rig.SaveCurrentStationLayout();
        SpeakAfterMenuClose(
            error ?? Radios.Lexicon.Get("settings.station_layout.saved",
                ("profileName", profileName)),
            Radios.VerbosityLevel.Critical);
    }

    /// <summary>
    /// Offer to save the station layout on the way out, when the operator has
    /// turned that on and there is genuinely something to ask about.
    /// </summary>
    /// <remarks>
    /// <para>Silent and instant in every case but one: <see
    /// cref="Radios.FlexBase.ShouldOfferStationLayoutSave"/> is false unless
    /// the setting is on, the operator changed the slice set or a
    /// radio-persisted setting this session (#225 widened the trigger past
    /// slices), the radio is theirs, and they are the only operator on it. A
    /// disconnect where any of that fails looks exactly like it did before
    /// this shipped.
    /// </para>
    ///
    /// <para>No is a real answer and is honoured without argument — no second
    /// prompt, no "are you sure". The operator disconnecting is not asking to
    /// negotiate.</para>
    /// </remarks>
    private void OfferStationSaveBeforeDisconnect(Radios.FlexBase? rig)
    {
        if (rig == null) return;

        bool shouldOffer;
        try { shouldOffer = rig.ShouldOfferStationLayoutSave(); }
        catch { return; }   // an offer must never be able to block a disconnect
        if (!shouldOffer) return;

        string profileName = rig.CurrentGlobalProfileName;

        // ── APPROVED BY NOEL, 2026-08-20 (Sprint 33 Track K) ──
        // Two changes from the draft, both his.
        //
        // "slice settings or frequencies" rather than "the slices" — the offer
        // fires on any slice-set change including a release, so naming what
        // actually changed is truer than naming the objects.
        //
        // THE SHARED-STATE WARNING WAS CUT FROM THIS PROMPT and kept on the
        // menu route only. It is factually correct — a global profile is
        // station state, and everyone who connects does get it — but this offer
        // is gated on the radio being the operator's OWN, so the audience is
        // other people using their radio, which for most operators is nobody.
        // A disconnect prompt interrupts someone who is already leaving, so it
        // earns only the sentence that prevents a mistake. The menu item is a
        // deliberate act with room for the fuller explanation, and keeps it.
        //
        // Also dropped: "to keep the station from losing settings". The station
        // never had them, so nothing is being lost.
        var confirm = new Dialogs.ConfirmActionDialog(
            Radios.Lexicon.Get("settings.station_layout.disconnect_title"),
            Radios.Lexicon.Get("settings.station_layout.disconnect_body",
                ("profileName", profileName)),
            warnings: new[]
            {
                Radios.Lexicon.Get("settings.station_layout.disconnect_warning",
                    ("profileName", profileName))
            },
            question: Radios.Lexicon.Get("settings.station_layout.disconnect_question",
                ("profileName", profileName)),
            yesLabel: Radios.Lexicon.Get("settings.station_layout.disconnect_yes_label"),
            noLabel: Radios.Lexicon.Get("settings.station_layout.disconnect_no_label"));

        if (confirm.ShowDialog() != true) return;

        var error = rig.SaveCurrentStationLayout();
        SpeakAfterMenuClose(
            error ?? Radios.Lexicon.Get("settings.station_layout.saved",
                ("profileName", profileName)),
            Radios.VerbosityLevel.Critical);
    }

    /// <summary>
    /// The profiles the radio itself is carrying that are not in the operator's
    /// own list. Display profiles are excluded because the dialog offers no way
    /// to act on them. Never null.
    /// </summary>
    private List<Radios.Profile_t> RigOnlyProfiles()
    {
        if (Rig == null) return new List<Radios.Profile_t>();
        try
        {
            var all = Rig.GetRigProfiles(Rig.Callouts?.Profiles);
            if (all == null) return new List<Radios.Profile_t>();
            return all
                .Where(p => p.ProfileType != Radios.ProfileTypes.display)
                .ToList();
        }
        catch (Exception ex)
        {
            // Reading the radio's profile lists must never be able to stop the
            // dialog opening — the operator's own list is still useful.
            Tracing.TraceLine($"RigOnlyProfiles: {ex.Message}", TraceLevel.Warning);
            return new List<Radios.Profile_t>();
        }
    }

    private static Radios.ProfileTypes ProfileTypeFromIndex(int typeIndex) => typeIndex switch
    {
        0 => Radios.ProfileTypes.global,
        1 => Radios.ProfileTypes.tx,
        2 => Radios.ProfileTypes.mic,
        _ => Radios.ProfileTypes.global
    };

    #endregion
}
