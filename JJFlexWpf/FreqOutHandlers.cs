using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using HamBands;
using JJFlexWpf.Controls;
using JJTrace;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// Field handler methods for FreqOut interactive tuning.
/// Ported from Form1.vb FreqOut handlers (pre-WPF migration).
/// Each handler receives a FieldAction with field key, WPF key event, and cursor position.
///
/// Sprint 12 Phase 12.6.
/// </summary>
public class FreqOutHandlers
{
    private readonly MainWindow _window;

    // Delegate properties for VB.NET globals access
    public Func<bool>? GetSplitVFOs { get; set; }
    public Action<bool>? SetSplitVFOs { get; set; }
    public Func<bool>? GetShowXmitFrequency { get; set; }
    public Action<bool>? SetShowXmitFrequency { get; set; }
    public Func<bool>? GetMemoryMode { get; set; }
    public Action<bool>? SetMemoryMode { get; set; }
    public Func<ulong>? GetRXFrequency { get; set; }
    public Action<ulong>? SetRXFrequency { get; set; }
    public Func<string, string>? FormatFreq { get; set; }
    public Func<string, ulong>? FreqInt64 { get; set; }

    // Step multiplier for +N/-N step size override
    private int _stepMultiplier = 1;
    private string _stepBuffer = "";
    private bool _inStepEntry;

    // Frequency readout toggle — when off, tuning doesn't speak the new frequency
    private bool _freqReadout = true;

    /// <summary>Whether frequency readout is currently enabled.</summary>
    public bool FreqReadoutEnabled => _freqReadout;

    // Filter edge selection mode — double-tap bracket to adjust a single edge
    private enum FilterEdgeMode { None, LowerEdge, UpperEdge }
    private FilterEdgeMode _filterEdgeMode = FilterEdgeMode.None;
    private Key _lastBracketKey;
    private DateTime _lastBracketTime = DateTime.MinValue;
    private CancellationTokenSource? _filterEdgeTimeout;

    /// <summary>
    /// Toggle frequency readout on/off (Ctrl+Shift+F global hotkey).
    /// When off, Up/Down tuning doesn't auto-speak the new frequency.
    /// </summary>
    public void ToggleFreqReadout()
    {
        _freqReadout = !_freqReadout;
        if (_freqReadout) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
        Radios.ScreenReaderOutput.Speak(
            _freqReadout ? "Frequency readout on" : "Frequency readout off", VerbosityLevel.Terse, true);
    }

    // Tuning speech debounce — rate-limits frequency announcements during rapid tuning
    private CancellationTokenSource? _tuneDebounce;
    private bool _firstTuneStep = true;

    /// <summary>
    /// Suppression window for ShowFrequency speech. When tuning speech fires via
    /// SpeakTuningDebounced, ShowFrequency's FreqOut.Write should skip writing to
    /// avoid double-speaking the frequency. Checked by MainWindow.ShowFrequency().
    /// </summary>
    public DateTime TuningSpeechUntil { get; private set; } = DateTime.MinValue;

    // Modern mode tuning — coarse/fine with configurable step lists.
    // Defaults are sane HF values. Users can configure via operator profile later.
    private int[] _coarseSteps = { 1000, 2000, 5000 };       // 1k, 2k, 5k Hz
    private int[] _fineSteps = { 5, 10, 100 };                // 5, 10, 100 Hz
    private bool _coarseMode = true;   // true = coarse, false = fine
    private int _coarseStepIndex = 0;  // default 1kHz
    private int _fineStepIndex = 1;    // default 10Hz

    /// <summary>
    /// Current active tuning step in Hz (from whichever mode is active).
    /// </summary>
    public int CurrentTuneStep => _coarseMode
        ? _coarseSteps[_coarseStepIndex]
        : _fineSteps[_fineStepIndex];

    /// <summary>
    /// Current coarse tuning step in Hz.
    /// </summary>
    public int CoarseTuneStep
    {
        get => _coarseSteps[_coarseStepIndex];
        set
        {
            for (int i = 0; i < _coarseSteps.Length; i++)
            {
                if (_coarseSteps[i] == value) { _coarseStepIndex = i; return; }
            }
            _coarseStepIndex = 0;
        }
    }

    /// <summary>
    /// Current fine tuning step in Hz.
    /// </summary>
    public int FineTuneStep
    {
        get => _fineSteps[_fineStepIndex];
        set
        {
            for (int i = 0; i < _fineSteps.Length; i++)
            {
                if (_fineSteps[i] == value) { _fineStepIndex = i; return; }
            }
            _fineStepIndex = 1;
        }
    }

    /// <summary>
    /// Whether currently in coarse tuning mode (vs fine).
    /// </summary>
    public bool IsCoarseMode => _coarseMode;

    /// <summary>Get a copy of the current coarse step list.</summary>
    public int[] GetCoarseSteps() => (int[])_coarseSteps.Clone();

    /// <summary>Get a copy of the current fine step list.</summary>
    public int[] GetFineSteps() => (int[])_fineSteps.Clone();

    /// <summary>Replace the coarse step list. Must have at least one value, sorted ascending.</summary>
    public void SetCoarseSteps(int[] steps)
    {
        if (steps == null || steps.Length == 0) return;
        _coarseSteps = (int[])steps.Clone();
        Array.Sort(_coarseSteps);
        if (_coarseStepIndex >= _coarseSteps.Length) _coarseStepIndex = 0;
    }

    /// <summary>Replace the fine step list. Must have at least one value, sorted ascending.</summary>
    public void SetFineSteps(int[] steps)
    {
        if (steps == null || steps.Length == 0) return;
        _fineSteps = (int[])steps.Clone();
        Array.Sort(_fineSteps);
        if (_fineStepIndex >= _fineSteps.Length) _fineStepIndex = 0;
    }

    /// <summary>
    /// Callback to persist step sizes to operator profile.
    /// Set by ApplicationEvents.vb.
    /// </summary>
    public Action<int, int>? SaveStepSizes { get; set; }

    /// <summary>
    /// Filter presets for the current operator. Set by ApplicationEvents.vb during radio connect.
    /// </summary>
    public Radios.FilterPresets? FilterPresets { get; set; }

    // Quick-type frequency entry — accumulate digits, confirm with Enter
    private string _quickTypeBuffer = "";
    private DateTime _lastDigitTime = DateTime.MinValue;
    private CancellationTokenSource? _quickTypeTimeout;
    private bool _inQuickType;

    public FreqOutHandlers(MainWindow window)
    {
        _window = window;
    }

    private FlexBase? Rig => _window.RigControl;

    /// <summary>
    /// Convert a WPF KeyEventArgs to a simple key character for digit/letter handlers.
    /// Returns '\0' when Alt is held so letter handlers don't conflict with menu accelerators.
    /// </summary>
    private static char KeyToChar(KeyEventArgs e)
    {
        // Don't convert letters when Alt is held — let menu accelerators handle them
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) return '\0';

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key switch
        {
            >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
            >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (key - Key.NumPad0)),
            >= Key.A and <= Key.Z => (char)('A' + (key - Key.A)),
            Key.OemPlus or Key.Add => '+',
            Key.OemMinus or Key.Subtract => '-',
            Key.OemPeriod or Key.Decimal => '.',
            Key.OemComma => ',',
            Key.Space => ' ',
            _ => '\0'
        };
    }

    private static Key RawKey(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

    #region AdjustFreq

    /// <summary>
    /// Frequency field handler — digit-by-digit tuning.
    /// Up/Down tune by position (with optional step multiplier), digits enter frequency, K rounds to KHz.
    /// +N/-N sets a step multiplier (e.g. +25 at 1kHz position → Up/Down tunes by 25kHz).
    /// </summary>
    public void AdjustFreq(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        var key = RawKey(e);
        char ch = KeyToChar(e);

        // Position within frequency field (0 = leftmost digit = highest significance)
        int fieldStart = _window.FreqOut.GetFieldPosition("Freq");
        int posInField = _window.FreqOut.SelectionStart - fieldStart;
        int fieldLen = _window.FreqOut.GetFieldLength("Freq");
        if (posInField < 0) posInField = 0;
        if (posInField >= fieldLen) posInField = fieldLen - 1;

        // Tune step: count actual digits to the right of cursor position.
        // Freq field text has dots and leading spaces (e.g. "   3.828.750"),
        // so we can't use fieldLen - skipping non-digit chars gives the correct exponent.
        string fieldText = _window.FreqOut.Read("Freq");
        int digitsToRight = 0;
        for (int i = posInField + 1; i < fieldText.Length; i++)
        {
            char c = fieldText[i];
            if (c != '.' && c != ' ')
                digitsToRight++;
        }
        ulong baseStep = 1;
        for (int i = 0; i < digitsToRight; i++)
            baseStep *= 10;
        ulong step = baseStep * (ulong)_stepMultiplier;

        // In step-entry mode, digits accumulate into the step buffer instead of entering frequency
        if (_inStepEntry && ch >= '0' && ch <= '9')
        {
            _stepBuffer += ch;
            if (int.TryParse(_stepBuffer, out int mult) && mult > 0)
            {
                _stepMultiplier = mult;
                string? stepUnit = Controls.FrequencyDisplay.GetStepNameForKey("Freq", posInField, fieldLen, fieldText);
                // stepUnit is like "1 kilohertz", "10 kilohertz" — strip the leading number
                // so "Step 5 1 kilohertz" becomes "Step 5 kilohertz"
                string unit = StripLeadingNumber(stepUnit) ?? "";
                Radios.ScreenReaderOutput.Speak($"Step {mult} {unit}".Trim(), VerbosityLevel.Terse);
            }
            e.Handled = true;
            return;
        }

        // Quick-type frequency entry: digits, dot, Enter, Escape
        if (HandleQuickTypeKey(key, ch, e)) return;

        // Reset tune debounce on non-tuning keys (Up/Down/U/D are tuning keys)
        if (key != Key.Up && key != Key.Down && ch != 'U' && ch != 'D')
            ResetTuneDebounce();

        switch (key)
        {
            case Key.Up:
                _inStepEntry = false;
                TuneFreq(step);
                e.Handled = true;
                break;

            case Key.Down:
                _inStepEntry = false;
                TuneFreq(unchecked((ulong)(-(long)step)));
                e.Handled = true;
                break;

            default:
                if (ch == '+' || ch == '-')
                {
                    // Enter step-entry mode
                    _inStepEntry = true;
                    _stepBuffer = "";
                    _stepMultiplier = 1;
                    Radios.ScreenReaderOutput.Speak("Step entry");
                    e.Handled = true;
                }
                else if (ch == 'K')
                {
                    RoundToKHz();
                    // Also reset step multiplier
                    if (_stepMultiplier != 1)
                    {
                        _stepMultiplier = 1;
                        _inStepEntry = false;
                        _stepBuffer = "";
                        Radios.ScreenReaderOutput.Speak("Step reset", VerbosityLevel.Terse);
                    }
                    e.Handled = true;
                }
                else if (ch == 'S')
                {
                    // Delegate to Split handler
                    var splitField = GetField("Split");
                    if (splitField != null) AdjustSplit(splitField, e);
                }
                else if (ch == 'T')
                {
                    // Toggle ShowXmitFrequency
                    if (GetShowXmitFrequency != null && SetShowXmitFrequency != null)
                    {
                        SetShowXmitFrequency(!GetShowXmitFrequency());
                        e.Handled = true;
                    }
                }
                else if (ch == 'V')
                {
                    // Delegate to VFO handler
                    var vfoField = GetField("VFO");
                    if (vfoField != null) AdjustVFO(vfoField, e);
                }
                else if (ch == 'U')
                {
                    _inStepEntry = false;
                    TuneFreq(step);
                    e.Handled = true;
                }
                else if (ch == 'D')
                {
                    _inStepEntry = false;
                    TuneFreq(unchecked((ulong)(-(long)step)));
                    e.Handled = true;
                }
                else if (ch == 'F' && Keyboard.Modifiers == ModifierKeys.None)
                {
                    // F: One-shot read current frequency
                    _window.SpeakFrequency();
                    e.Handled = true;
                }
                break;
        }
    }

    private void TuneFreq(ulong delta)
    {
        if (Rig == null || GetRXFrequency == null || SetRXFrequency == null) return;
        ulong current = GetRXFrequency();
        ulong newFreq = current + delta;
        if (newFreq > 0 && newFreq < 100_000_000_000UL) // sanity limit
        {
            SetRXFrequency(newFreq);
            if (_freqReadout)
            {
                SpeakTuningDebounced(FormatFreqForSpeech(newFreq));
            }
            CheckBandBoundary(newFreq);
        }
    }

    /// <summary>
    /// Preferred frequency units for speech announcements. Set from PttConfig.
    /// </summary>
    public Radios.FrequencyUnits FrequencyUnits { get; set; } = Radios.FrequencyUnits.Hz;

    /// <summary>
    /// Format a frequency in Hz to a spoken string, respecting the units preference.
    /// Hz: "14.250.000", kHz: "14,225 kilohertz", MHz: "14.225 megahertz"
    /// </summary>
    private string FormatFreqForSpeech(ulong freqHz)
    {
        switch (FrequencyUnits)
        {
            case Radios.FrequencyUnits.kHz:
                double khz = freqHz / 1000.0;
                if (freqHz % 1000 == 0)
                    return $"{khz:N0} kilohertz";
                return $"{khz:N1} kilohertz";

            case Radios.FrequencyUnits.MHz:
                double mhz = freqHz / 1_000_000.0;
                // Show enough decimals to represent the actual frequency
                // 3.806.000 → "3.806", 3.806.250 → "3.806250"
                if (freqHz % 1000 == 0)
                    return $"{mhz:N3} megahertz";
                return $"{mhz:N6} megahertz";

            default: // Hz — original dotted format
                string s = freqHz.ToString();
                while (s.Length < 7) s = "0" + s;
                int len = s.Length;
                return s.Substring(0, len - 6) + "." + s.Substring(len - 6, 3) + "." + s.Substring(len - 3);
        }
    }

    /// <summary>
    /// Speak a tuning frequency with debounce. First step speaks immediately;
    /// subsequent steps within the debounce window reset a timer. When the timer fires,
    /// the current frequency is spoken (not the value from when the timer started).
    /// Debounce can be disabled via AudioOutputConfig.TuneDebounceEnabled.
    /// </summary>
    private void SpeakTuningDebounced(string message)
    {
        // Set suppression window so ShowFrequency doesn't double-speak
        TuningSpeechUntil = DateTime.UtcNow.AddMilliseconds(500);

        // Check debounce config — when disabled, speak every step
        var config = _window.CurrentAudioConfig;
        if (config != null && !config.TuneDebounceEnabled)
        {
            Radios.ScreenReaderOutput.Speak(message, VerbosityLevel.Terse, interrupt: true);
            return;
        }

        if (_firstTuneStep)
        {
            Radios.ScreenReaderOutput.Speak(message, VerbosityLevel.Terse, interrupt: true);
            _firstTuneStep = false;
            return; // Already spoke — don't also start debounce timer
        }

        _tuneDebounce?.Cancel();
        _tuneDebounce = new CancellationTokenSource();
        var token = _tuneDebounce.Token;

        var debounceMs = config?.TuneDebounceMs ?? 300;
        debounceMs = Math.Clamp(debounceMs, 50, 1000);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(debounceMs, token);
                if (!token.IsCancellationRequested)
                {
                    var freqGetter = GetRXFrequency;
                    if (freqGetter != null)
                    {
                        var currentFreq = FormatFreqForSpeech(freqGetter());
                        Radios.ScreenReaderOutput.Speak(currentFreq, VerbosityLevel.Terse, interrupt: true);
                    }
                    _firstTuneStep = true;
                }
            }
            catch (TaskCanceledException) { }
        }, token);
    }

    /// <summary>
    /// Reset the tuning debounce state so the next tune step speaks immediately.
    /// Call on focus leave, field change, or non-tuning key press.
    /// </summary>
    public void ResetTuneDebounce()
    {
        _firstTuneStep = true;
        _tuneDebounce?.Cancel();
        _tuneDebounce = null;
    }

    /// <summary>
    /// Strip leading number from step names like "1 kilohertz" → "kilohertz".
    /// </summary>
    private static string? StripLeadingNumber(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == ' '))
            i++;
        return i > 0 && i < s.Length ? s.Substring(i) : s;
    }

    private void EnterFreqDigit(char digit, int posInField, int fieldLen)
    {
        if (Rig == null || FreqInt64 == null || SetRXFrequency == null) return;
        string fieldText = _window.FreqOut.Read("Freq");
        // Convert posInField (raw position with dots/spaces) to digit-only index
        int digitIndex = 0;
        for (int i = 0; i < posInField && i < fieldText.Length; i++)
        {
            if (fieldText[i] != '.' && fieldText[i] != ' ')
                digitIndex++;
        }
        // Strip formatting (spaces, dots)
        string clean = fieldText.Trim().Replace(" ", "").Replace(".", "");
        if (digitIndex >= 0 && digitIndex < clean.Length)
        {
            char[] chars = clean.ToCharArray();
            chars[digitIndex] = digit;
            string newText = new string(chars);
            try
            {
                ulong newFreq = FreqInt64(newText);
                if (newFreq > 0)
                {
                    SetRXFrequency(newFreq);
                    if (FormatFreq != null)
                        Radios.ScreenReaderOutput.Speak(FormatFreq(newFreq.ToString()), VerbosityLevel.Terse);
                }
            }
            catch { /* ignore parse errors */ }
        }
        // Advance cursor
        if (posInField < fieldLen - 1)
            _window.FreqOut.SelectionStart++;
    }

    private void RoundToKHz()
    {
        if (Rig == null || GetRXFrequency == null || SetRXFrequency == null) return;
        ulong freq = GetRXFrequency();
        ulong rounded = ((freq + 500) / 1000) * 1000;
        SetRXFrequency(rounded);
    }

    #endregion

    #region QuickType

    /// <summary>
    /// Accumulate a digit into the quick-type frequency entry buffer.
    /// After a 1-second pause, prompts for confirmation.
    /// </summary>
    private void QuickTypeDigit(char digit)
    {
        var now = DateTime.Now;
        if (!_inQuickType || (now - _lastDigitTime).TotalMilliseconds > 1500)
        {
            // Start new quick-type session
            _quickTypeBuffer = "";
            _inQuickType = true;
        }

        _quickTypeBuffer += digit;
        _lastDigitTime = now;

        // Play typing sound
        EarconPlayer.PlayTypingSound(digit, TypingSound);

        // Speak the digit for feedback
        Radios.ScreenReaderOutput.Speak(digit.ToString(), VerbosityLevel.Terse, true);

        // Cancel any pending timeout
        _quickTypeTimeout?.Cancel();
        _quickTypeTimeout = new CancellationTokenSource();
        var token = _quickTypeTimeout.Token;

        // Prompt after 1 second of no input
        Task.Delay(1000, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                _window.Dispatcher.BeginInvoke(new Action(PromptQuickType));
        });
    }

    /// <summary>
    /// Accept a decimal point during quick-type entry.
    /// </summary>
    private void QuickTypeDot()
    {
        if (!_inQuickType) return;
        if (_quickTypeBuffer.Contains('.')) return; // only one decimal allowed

        _quickTypeBuffer += '.';
        _lastDigitTime = DateTime.Now;
        Radios.ScreenReaderOutput.Speak("point", VerbosityLevel.Terse, true);

        // Reset the timeout
        _quickTypeTimeout?.Cancel();
        _quickTypeTimeout = new CancellationTokenSource();
        var token = _quickTypeTimeout.Token;
        Task.Delay(1000, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                _window.Dispatcher.BeginInvoke(new Action(PromptQuickType));
        });
    }

    /// <summary>
    /// Speak confirmation prompt after typing pause.
    /// </summary>
    private void PromptQuickType()
    {
        if (!_inQuickType || string.IsNullOrEmpty(_quickTypeBuffer)) return;

        double? freqMhz = ParseQuickTypeFreq(_quickTypeBuffer);
        if (freqMhz == null)
        {
            Radios.ScreenReaderOutput.Speak("Invalid frequency, press Escape to cancel", VerbosityLevel.Critical, true);
            return;
        }

        string display = FormatFreqForSpeech(freqMhz.Value);
        Radios.ScreenReaderOutput.Speak(
            $"Change frequency to {display}? Press Enter to confirm, Escape to cancel",
            VerbosityLevel.Terse, true);
    }

    /// <summary>
    /// Parse the quick-type buffer into a frequency in MHz.
    /// Digits without decimal = kHz. With decimal = MHz.
    /// </summary>
    private static double? ParseQuickTypeFreq(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        if (input.Contains('.'))
        {
            // Contains decimal — treat as MHz
            if (double.TryParse(input, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double mhz)
                && mhz > 0.1 && mhz < 500)
                return mhz;
        }
        else
        {
            // No decimal — treat as kHz, convert to MHz
            if (double.TryParse(input, out double khz) && khz > 100 && khz < 500000)
                return khz / 1000.0;
        }
        return null;
    }

    /// <summary>
    /// Format a frequency in MHz for spoken output.
    /// </summary>
    private static string FormatFreqForSpeech(double mhz)
    {
        if (mhz >= 1.0) return $"{mhz:F3} megahertz";
        return $"{mhz * 1000:F0} kilohertz";
    }

    /// <summary>
    /// Commit the quick-type frequency entry.
    /// </summary>
    private void CommitQuickType()
    {
        if (!_inQuickType || string.IsNullOrEmpty(_quickTypeBuffer)) return;
        _quickTypeTimeout?.Cancel();

        // Check for calibration reference before frequency parsing
        string? calibRef = CalibrationEngine.VerifyCalibration(_quickTypeBuffer);
        if (calibRef != null)
        {
            HandleCalibrationUnlock(calibRef);
            CancelQuickType();
            return;
        }

        double? freqMhz = ParseQuickTypeFreq(_quickTypeBuffer);
        if (freqMhz == null)
        {
            Radios.ScreenReaderOutput.Speak("Invalid frequency", VerbosityLevel.Critical, true);
            CancelQuickType();
            return;
        }

        // Convert MHz to Hz (ulong, our internal format)
        ulong freqHz = (ulong)(freqMhz.Value * 1_000_000);
        SetRXFrequency?.Invoke(freqHz);
        CheckBandBoundary(freqHz);

        // Typewriter bell in mechanical mode, regular ding otherwise
        if (TypingSound == TypingSoundMode.Mechanical)
            EarconPlayer.TypewriterBellTone();
        else
            EarconPlayer.DingTone();
        string display = FormatFreqForSpeech(freqMhz.Value);
        Radios.ScreenReaderOutput.Speak($"Frequency set to {display}", VerbosityLevel.Terse, true);
        CancelQuickType();
    }

    /// <summary>
    /// Cancel quick-type entry and restore previous state.
    /// </summary>
    private void CancelQuickType()
    {
        _inQuickType = false;
        _quickTypeBuffer = "";
        _quickTypeTimeout?.Cancel();
    }

    /// <summary>
    /// Handle Enter/Escape/digit/dot keys during quick-type mode in a frequency field.
    /// Returns true if the key was consumed by quick-type.
    /// </summary>
    private bool HandleQuickTypeKey(Key key, char ch, KeyEventArgs e)
    {
        if (key == Key.Escape && _inQuickType)
        {
            CancelQuickType();
            Radios.ScreenReaderOutput.Speak("Cancelled", VerbosityLevel.Terse, true);
            e.Handled = true;
            return true;
        }
        if (key == Key.Enter && _inQuickType)
        {
            CommitQuickType();
            e.Handled = true;
            return true;
        }
        if (ch >= '0' && ch <= '9')
        {
            QuickTypeDigit(ch);
            e.Handled = true;
            return true;
        }
        if (ch == '.' && _inQuickType)
        {
            QuickTypeDot();
            e.Handled = true;
            return true;
        }
        // Letters: accumulate only when ALREADY in quick-type mode (for calibration references).
        // Do NOT start quick-type with letters — that would eat C (coarse/fine), F (speak freq), etc.
        if (_inQuickType && (ch >= 'A' && ch <= 'Z' || ch >= 'a' && ch <= 'z'))
        {
            _quickTypeBuffer += ch;
            _lastDigitTime = DateTime.Now;
            e.Handled = true;
            return true;
        }
        return false;
    }

    #endregion

    #region AdjustVFO

    /// <summary>
    /// VFO field handler — cycle slices, enter memory mode.
    /// </summary>
    public void AdjustVFO(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        var key = RawKey(e);
        char ch = KeyToChar(e);

        switch (key)
        {
            case Key.Up:
                CycleVFO(1);
                e.Handled = true;
                break;
            case Key.Down:
                CycleVFO(-1);
                e.Handled = true;
                break;
            default:
                if (ch >= '0' && ch <= '9')
                {
                    int slice = ch - '0';
                    if (Rig.ValidVFO(slice))
                    {
                        Rig.RXVFO = slice;
                        Radios.ScreenReaderOutput.Speak($"Slice {Rig.VFOToLetter(slice)}", VerbosityLevel.Terse);
                        e.Handled = true;
                    }
                }
                else if (ch >= 'A' && ch <= 'H')
                {
                    // Direct slice select: A=slice 0, B=slice 1, etc.
                    int target = ch - 'A';
                    if (Rig.ValidVFO(target))
                    {
                        Rig.RXVFO = target;
                        Radios.ScreenReaderOutput.Speak($"Slice {Rig.VFOToLetter(target)} active", VerbosityLevel.Terse);
                        e.Handled = true;
                    }
                }
                else if (ch == 'M')
                {
                    SetMemoryMode?.Invoke(true);
                    e.Handled = true;
                }
                else if (ch == 'V')
                {
                    SetMemoryMode?.Invoke(false);
                    e.Handled = true;
                }
                break;
        }
    }

    private void CycleVFO(int direction, bool wrap = false)
    {
        if (Rig == null) return;
        int current = Rig.RXVFO;
        int total = Rig.MyNumSlices;
        if (total <= 0) return;

        Tracing.TraceLine($"CycleVFO:dir={direction} current={current} total={total} wrap={wrap}", TraceLevel.Info);

        int next = current + direction;
        if (wrap)
        {
            if (next >= total) next = 0;
            if (next < 0) next = total - 1;
        }
        else
        {
            if (next >= total)
            {
                string letter = Rig.VFOToLetter(current);
                Radios.ScreenReaderOutput.Speak($"Slice {letter}, last slice", VerbosityLevel.Terse, true);
                return;
            }
            if (next < 0)
            {
                string letter = Rig.VFOToLetter(current);
                Radios.ScreenReaderOutput.Speak($"Slice {letter}, first slice", VerbosityLevel.Terse, true);
                return;
            }
        }

        // Find next valid VFO
        int attempts = 0;
        while (!Rig.ValidVFO(next) && attempts < total)
        {
            next += direction;
            if (wrap)
            {
                if (next >= total) next = 0;
                if (next < 0) next = total - 1;
            }
            if (!wrap && (next >= total || next < 0))
            {
                string letter = Rig.VFOToLetter(current);
                Radios.ScreenReaderOutput.Speak($"Slice {letter}, {(direction > 0 ? "last" : "first")} slice", VerbosityLevel.Terse, true);
                return;
            }
            attempts++;
        }

        if (Rig.ValidVFO(next))
        {
            Rig.RXVFO = next;
            string letter = Rig.VFOToLetter(next);
            double freqMhz = Rig.GetVFOFrequency(next);
            string mode = Rig.GetVFOMode(next);
            string owner = Rig.GetSliceOwnerForVFO(next);
            string ownerSuffix = owner != null ? $", {owner}" : "";
            string freqSuffix = freqMhz > 0 ? $", {freqMhz:F3} {mode}" : "";
            Radios.ScreenReaderOutput.Speak($"Slice {letter}{freqSuffix}{ownerSuffix}", VerbosityLevel.Terse);
        }
        else
        {
            Tracing.TraceLine($"CycleVFO:no valid VFO found after {attempts} attempts, next={next}", TraceLevel.Warning);
        }
    }

    #endregion

    #region AdjustSlice

    /// <summary>
    /// Single slice field handler — Space cycles active slice, M toggles mute,
    /// T sets transmit, digit keys jump to specific slice.
    /// Period (.) creates a new slice, comma (,) releases the current slice.
    /// </summary>
    public void AdjustSlice(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        var key = RawKey(e);
        char ch = KeyToChar(e);
        int vfo = Rig.RXVFO;

        switch (ch)
        {
            case ' ':
                // Cycle to next valid slice (wraps around)
                CycleVFO(1, wrap: true);
                e.Handled = true;
                break;
            case 'M':
                // Toggle mute on current slice — same property as menu checkmark
                {
                    bool newMute = !Rig.SliceMute;
                    Rig.SliceMute = newMute;
                    if (newMute) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
                    Radios.ScreenReaderOutput.Speak(newMute ? "Muted" : "Unmuted", VerbosityLevel.Terse);
                }
                e.Handled = true;
                break;
            case 'T':
                if (Rig.CanTransmit && Rig.ValidVFO(vfo))
                {
                    Rig.TXVFO = vfo;
                    Radios.ScreenReaderOutput.Speak($"Slice {Rig.VFOToLetter(vfo)} transmit", VerbosityLevel.Terse);
                }
                e.Handled = true;
                break;
            // Sprint 28 Phase 5 — X rebound from transceive to XIT toggle for
            // cross-field consistency. Transceive moved to '=' (mnemonic: RX = TX).
            case '=':
                // Transceive: set both RX and TX to current slice (formerly X)
                if (Rig.ValidVFO(vfo))
                {
                    Rig.RXVFO = vfo;
                    if (Rig.CanTransmit)
                        Rig.TXVFO = vfo;
                    Radios.ScreenReaderOutput.Speak($"Slice {Rig.VFOToLetter(vfo)} transceive", VerbosityLevel.Terse);
                }
                e.Handled = true;
                break;
            case 'X':
                // Sprint 28 Phase 5 — X is now XIT toggle on all three home
                // fields (Slice, SliceOps, Home). Matches the frequency field's
                // existing X=XIT semantic.
                {
                    var xit = new FlexBase.RITData(Rig.XIT);
                    xit.Active = !xit.Active;
                    Rig.XIT = xit;
                    if (xit.Active) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
                    Radios.ScreenReaderOutput.Speak(
                        xit.Active ? "XIT on" : "XIT off", VerbosityLevel.Terse, true);
                }
                e.Handled = true;
                break;
            case 'R':
                // Sprint 28 Phase 5 — R is now RIT toggle on all three home
                // fields. Was pan-right on the Slice field; pan moved to
                // PageUp/Home/PageDown (see key.PageUp/Home/PageDown handlers
                // below).
                {
                    var rit = new FlexBase.RITData(Rig.RIT);
                    rit.Active = !rit.Active;
                    Rig.RIT = rit;
                    if (rit.Active) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
                    Radios.ScreenReaderOutput.Speak(
                        rit.Active ? "RIT on" : "RIT off", VerbosityLevel.Terse, true);
                }
                e.Handled = true;
                break;
            case '.':
                // Create/activate a new slice
                {
                    int countBefore = Rig.MyNumSlices;
                    if (Rig.NewSlice())
                    {
                        Radios.ScreenReaderOutput.Speak($"Slice created, {countBefore + 1} active", VerbosityLevel.Terse, true);
                    }
                    else
                    {
                        Radios.ScreenReaderOutput.Speak("Maximum slices reached", VerbosityLevel.Critical, true);
                    }
                }
                e.Handled = true;
                break;
            case ',':
                // Release/remove the current slice
                if (Rig.MyNumSlices <= 1)
                {
                    Radios.ScreenReaderOutput.Speak("Cannot release last slice", VerbosityLevel.Critical, true);
                }
                else
                {
                    int toRemove = vfo;
                    int countBefore = Rig.MyNumSlices;
                    string removedLetter = Rig.VFOToLetter(toRemove);
                    // Find another slice to switch to
                    int switchTo = -1;
                    for (int i = 0; i < countBefore; i++)
                    {
                        if (i != toRemove)
                        {
                            switchTo = i;
                            break;
                        }
                    }
                    if (switchTo >= 0)
                    {
                        string switchLetter = Rig.VFOToLetter(switchTo);
                        // Move TX away from the slice being removed if needed
                        if (Rig.CanTransmit && toRemove == Rig.TXVFO)
                            Rig.TXVFO = switchTo;
                        Rig.RXVFO = switchTo;
                        if (Rig.RemoveSlice(toRemove))
                        {
                            Radios.ScreenReaderOutput.Speak($"Slice {removedLetter} released, {countBefore - 1} active", VerbosityLevel.Terse, true);
                        }
                        else
                        {
                            Rig.RXVFO = toRemove; // revert
                            Radios.ScreenReaderOutput.Speak("Cannot release this slice", VerbosityLevel.Critical, true);
                        }
                    }
                }
                e.Handled = true;
                break;
            default:
                switch (key)
                {
                    case Key.Up:
                        CycleVFO(1);
                        e.Handled = true;
                        break;
                    case Key.Down:
                        CycleVFO(-1);
                        e.Handled = true;
                        break;
                    // Sprint 28 Phase 5 — pan triad moved from L/C/R letter keys
                    // to navigation keys to match Slice Operations field's existing
                    // pattern and free R for RIT toggle. Nav keys carry inherent
                    // spatial semantics (PageUp/PageDown = directional) reducing
                    // cognitive load for screen-reader users.
                    case Key.PageDown:
                        if (Rig.ValidVFO(vfo))
                        {
                            Rig.SetVFOPan(vfo, FlexBase.MinPan);
                            Radios.ScreenReaderOutput.Speak("Pan left", VerbosityLevel.Terse);
                        }
                        e.Handled = true;
                        break;
                    case Key.Home:
                        if (Rig.ValidVFO(vfo))
                        {
                            Rig.SetVFOPan(vfo, (FlexBase.MaxPan - FlexBase.MinPan) / 2);
                            Radios.ScreenReaderOutput.Speak("Pan center", VerbosityLevel.Terse);
                        }
                        e.Handled = true;
                        break;
                    case Key.PageUp:
                        if (Rig.ValidVFO(vfo))
                        {
                            Rig.SetVFOPan(vfo, FlexBase.MaxPan);
                            Radios.ScreenReaderOutput.Speak("Pan right", VerbosityLevel.Terse);
                        }
                        e.Handled = true;
                        break;
                }

                if (ch >= '0' && ch <= '9')
                {
                    int target = ch - '0';
                    if (Rig.ValidVFO(target))
                    {
                        Rig.RXVFO = target;
                        Radios.ScreenReaderOutput.Speak($"Slice {Rig.VFOToLetter(target)} active", VerbosityLevel.Terse);
                        e.Handled = true;
                    }
                }
                else if (ch >= 'A' && ch <= 'H')
                {
                    int target = ch - 'A';
                    if (Rig.ValidVFO(target))
                    {
                        Rig.RXVFO = target;
                        Radios.ScreenReaderOutput.Speak($"Slice {Rig.VFOToLetter(target)} active", VerbosityLevel.Terse);
                        e.Handled = true;
                    }
                }
                break;
        }
    }

    private void AdjustGain(int vfo, int delta)
    {
        if (Rig == null) return;
        int current = Rig.GetVFOGain(vfo);
        int newVal = Math.Clamp(current + delta, FlexBase.MinGain, FlexBase.MaxGain);
        Rig.SetVFOGain(vfo, newVal);
        // interrupt=true to cut off NVDA's TextBox content change reading
        Radios.ScreenReaderOutput.Speak($"Volume {newVal}", VerbosityLevel.Terse, true);
    }

    private void AdjustPan(int vfo, int delta)
    {
        if (Rig == null) return;
        int current = Rig.GetVFOPan(vfo);
        int newVal = Math.Clamp(current + delta, FlexBase.MinPan, FlexBase.MaxPan);
        Rig.SetVFOPan(vfo, newVal);
        Radios.ScreenReaderOutput.Speak($"Pan {newVal}", VerbosityLevel.Terse);
    }

    #endregion

    #region AdjustSliceOps

    /// <summary>
    /// Slice Operations field handler — per-slice volume, pan, and mute.
    /// Up/Down = volume, PageUp/PageDown = pan, Space = mute toggle.
    ///
    /// <para>
    /// Sprint 26 Phase 8 Jim-parity: letter shortcuts match Jim's original
    /// slice-status-row vocabulary. <c>S</c> sets sound (unmute), <c>M</c>
    /// sets mute (explicit), <c>Space</c> toggles. Setting is idempotent
    /// — pressing <c>S</c> when already sounding re-announces the state
    /// for confirmation.
    /// </para>
    /// </summary>
    public void AdjustSliceOps(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        var key = RawKey(e);
        char ch = KeyToChar(e);
        int vfo = Rig.RXVFO;

        switch (key)
        {
            case Key.Up:
                if (Rig.ValidVFO(vfo))
                    AdjustGain(vfo, FlexBase.GainIncrement);
                e.Handled = true;
                break;
            case Key.Down:
                if (Rig.ValidVFO(vfo))
                    AdjustGain(vfo, -FlexBase.GainIncrement);
                e.Handled = true;
                break;
            case Key.PageUp:
                if (Rig.ValidVFO(vfo))
                    AdjustPan(vfo, FlexBase.PanIncrement);
                e.Handled = true;
                break;
            case Key.PageDown:
                if (Rig.ValidVFO(vfo))
                    AdjustPan(vfo, -FlexBase.PanIncrement);
                e.Handled = true;
                break;
            default:
                if (ch == ' ')
                {
                    // Toggle mute — fastest path, Jim's "space bar" pattern.
                    bool newMute = !Rig.SliceMute;
                    Rig.SliceMute = newMute;
                    if (newMute) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
                    string letter = Rig.VFOToLetter(vfo);
                    Radios.ScreenReaderOutput.Speak(
                        newMute ? $"Slice {letter} muted" : $"Slice {letter} unmuted",
                        VerbosityLevel.Terse, true);
                    e.Handled = true;
                }
                else if (ch == 'M')
                {
                    // Jim parity: explicit mute (idempotent — re-announces if already muted).
                    Rig.SliceMute = true;
                    EarconPlayer.FeatureOnTone();
                    string letter = Rig.VFOToLetter(vfo);
                    Radios.ScreenReaderOutput.Speak($"Slice {letter} muted", VerbosityLevel.Terse, true);
                    e.Handled = true;
                }
                else if (ch == 'S')
                {
                    // Jim parity: explicit sound / unmute (idempotent).
                    Rig.SliceMute = false;
                    EarconPlayer.FeatureOffTone();
                    string letter = Rig.VFOToLetter(vfo);
                    Radios.ScreenReaderOutput.Speak($"Slice {letter} sounding", VerbosityLevel.Terse, true);
                    e.Handled = true;
                }
                else if (ch == 'A')
                {
                    // Jim parity: make this slice the active (RX) slice. Redundant with
                    // the Slice-selector field's cycling, but matches Jim's muscle-memory
                    // vocabulary so operators used to JJ Flex 3 don't have to relearn.
                    if (Rig.ValidVFO(vfo))
                    {
                        Rig.RXVFO = vfo;
                        string letter = Rig.VFOToLetter(vfo);
                        Radios.ScreenReaderOutput.Speak($"Slice {letter} active", VerbosityLevel.Terse, true);
                    }
                    e.Handled = true;
                }
                else if (ch == 'T')
                {
                    // Jim parity: make this slice the TX slice.
                    if (Rig.CanTransmit && Rig.ValidVFO(vfo))
                    {
                        Rig.TXVFO = vfo;
                        string letter = Rig.VFOToLetter(vfo);
                        Radios.ScreenReaderOutput.Speak($"Slice {letter} transmit", VerbosityLevel.Terse, true);
                    }
                    e.Handled = true;
                }
                else if (ch == '=')
                {
                    // Sprint 28 Phase 5 — transceive rebound from X to '=' (RX = TX)
                    // so X can carry XIT toggle on all three home fields consistently.
                    // Preserves Jim-parity (a dedicated transceive hotkey still exists);
                    // just a different key, same semantic.
                    if (Rig.ValidVFO(vfo))
                    {
                        Rig.RXVFO = vfo;
                        if (Rig.CanTransmit) Rig.TXVFO = vfo;
                        string letter = Rig.VFOToLetter(vfo);
                        Radios.ScreenReaderOutput.Speak($"Slice {letter} transceive", VerbosityLevel.Terse, true);
                    }
                    e.Handled = true;
                }
                else if (ch == 'X')
                {
                    // Sprint 28 Phase 5 — X is XIT toggle on all three home fields.
                    var xit = new FlexBase.RITData(Rig.XIT);
                    xit.Active = !xit.Active;
                    Rig.XIT = xit;
                    if (xit.Active) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
                    Radios.ScreenReaderOutput.Speak(
                        xit.Active ? "XIT on" : "XIT off", VerbosityLevel.Terse, true);
                    e.Handled = true;
                }
                else if (ch == 'R')
                {
                    // Sprint 28 Phase 5 — R is RIT toggle on all three home fields.
                    var rit = new FlexBase.RITData(Rig.RIT);
                    rit.Active = !rit.Active;
                    Rig.RIT = rit;
                    if (rit.Active) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
                    Radios.ScreenReaderOutput.Speak(
                        rit.Active ? "RIT on" : "RIT off", VerbosityLevel.Terse, true);
                    e.Handled = true;
                }
                break;
        }
    }

    #endregion

    #region AdjustSplit

    /// <summary>
    /// Split field handler — toggle split mode.
    /// </summary>
    public void AdjustSplit(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        var key = RawKey(e);
        char ch = KeyToChar(e);

        switch (key)
        {
            case Key.Space:
            case Key.Up:
            case Key.Down:
                if (GetSplitVFOs != null && SetSplitVFOs != null)
                {
                    SetSplitVFOs(!GetSplitVFOs());
                    Radios.ScreenReaderOutput.Speak(GetSplitVFOs() ? "Split on" : "Split off", VerbosityLevel.Terse);
                }
                e.Handled = true;
                break;
            default:
                if (ch == 'S')
                {
                    SetSplitVFOs?.Invoke(true);
                    Radios.ScreenReaderOutput.Speak("Split on", VerbosityLevel.Terse);
                    e.Handled = true;
                }
                else if (ch == 'T')
                {
                    // Show TX frequency
                    SetShowXmitFrequency?.Invoke(true);
                    Radios.ScreenReaderOutput.Speak("Showing transmit frequency", VerbosityLevel.Terse);
                    e.Handled = true;
                }
                break;
        }
    }

    #endregion

    #region AdjustRit / AdjustXit / AdjustRITXIT

    public void AdjustRit(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        AdjustRITXIT(Rig.RIT, true, field, e);
    }

    public void AdjustXit(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        AdjustRITXIT(Rig.XIT, false, field, e);
    }

    /// <summary>
    /// Shared RIT/XIT handler — toggle, adjust by position, digit entry.
    /// </summary>
    private void AdjustRITXIT(FlexBase.RITData data, bool isRIT,
        FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        var key = RawKey(e);
        char ch = KeyToChar(e);

        string fieldKey = isRIT ? "RIT" : "XIT";
        int fieldStart = _window.FreqOut.GetFieldPosition(fieldKey);
        int posInField = _window.FreqOut.SelectionStart - fieldStart;
        int fieldLen = _window.FreqOut.GetFieldLength(fieldKey);
        if (posInField < 0) posInField = 0;
        if (posInField >= fieldLen) posInField = fieldLen - 1;

        // Step by position (field is 5 chars: sign + 4 digits like +0150)
        int step = 1;
        for (int i = 0; i < (fieldLen - 1 - posInField); i++)
            step *= 10;

        switch (key)
        {
            case Key.Space:
                // Toggle active
                var toggled = new FlexBase.RITData(data);
                toggled.Active = !toggled.Active;
                if (isRIT) Rig.RIT = toggled;
                else Rig.XIT = toggled;
                if (toggled.Active) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
                Radios.ScreenReaderOutput.Speak($"{fieldKey} {(toggled.Active ? "on" : "off")}", VerbosityLevel.Terse);
                e.Handled = true;
                break;

            case Key.Up:
                AdjustRITXITValue(data, isRIT, step);
                e.Handled = true;
                break;

            case Key.Down:
                AdjustRITXITValue(data, isRIT, -step);
                e.Handled = true;
                break;

            default:
                if (ch == 'U')
                {
                    AdjustRITXITValue(data, isRIT, step);
                    e.Handled = true;
                }
                else if (ch == 'D')
                {
                    AdjustRITXITValue(data, isRIT, -step);
                    e.Handled = true;
                }
                else if (ch >= '0' && ch <= '9')
                {
                    // Digit entry at position
                    EnterRITXITDigit(data, isRIT, ch, posInField, fieldLen);
                    e.Handled = true;
                }
                else if (ch == '+')
                {
                    var pos = new FlexBase.RITData(data);
                    pos.Value = Math.Abs(pos.Value);
                    if (isRIT) Rig.RIT = pos;
                    else Rig.XIT = pos;
                    e.Handled = true;
                }
                else if (ch == '-')
                {
                    var neg = new FlexBase.RITData(data);
                    neg.Value = -Math.Abs(neg.Value);
                    if (isRIT) Rig.RIT = neg;
                    else Rig.XIT = neg;
                    e.Handled = true;
                }
                else if (ch == '=' && isRIT)
                {
                    // Copy RIT to XIT
                    var copy = new FlexBase.RITData(Rig.RIT);
                    Rig.XIT = copy;
                    Radios.ScreenReaderOutput.Speak("Copied RIT to XIT", VerbosityLevel.Terse);
                    e.Handled = true;
                }
                break;
        }
    }

    private void AdjustRITXITValue(FlexBase.RITData data, bool isRIT, int delta)
    {
        if (Rig == null) return;
        var updated = new FlexBase.RITData(data);
        updated.Value += delta;
        updated.Active = true;
        if (isRIT) Rig.RIT = updated;
        else Rig.XIT = updated;
        string label = isRIT ? "RIT" : "XIT";
        string sign = updated.Value >= 0 ? "+" : "";
        Radios.ScreenReaderOutput.Speak($"{label} {sign}{updated.Value}", VerbosityLevel.Terse);
    }

    private void EnterRITXITDigit(FlexBase.RITData data, bool isRIT, char digit, int posInField, int fieldLen)
    {
        if (Rig == null) return;
        // Value displayed as sign + 4 digits (e.g. +0150)
        string absText = Math.Abs(data.Value).ToString("d4");
        if (posInField > 0 && posInField <= absText.Length)
        {
            char[] chars = absText.ToCharArray();
            chars[posInField - 1] = digit; // skip sign position
            if (int.TryParse(new string(chars), out int newVal))
            {
                var updated = new FlexBase.RITData(data);
                updated.Value = data.Value < 0 ? -newVal : newVal;
                updated.Active = true;
                if (isRIT) Rig.RIT = updated;
                else Rig.XIT = updated;
            }
        }
    }

    #endregion

    #region AdjustVox

    /// <summary>
    /// VOX field handler — toggle VOX on/off.
    /// Uses FlexBase.Vox property (handles both SimpleVOX and CW break-in).
    /// </summary>
    public void AdjustVox(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        var key = RawKey(e);

        if (key == Key.Space || key == Key.Up || key == Key.Down)
        {
            var newState = Rig.ToggleOffOn(Rig.Vox);
            Rig.Vox = newState;
            Radios.ScreenReaderOutput.Speak(newState == FlexBase.OffOnValues.on ? "VOX on" : "VOX off", VerbosityLevel.Terse);
            e.Handled = true;
        }
    }

    #endregion

    #region AdjustSMeter

    /// <summary>
    /// S-Meter field handler — read-only, no interactive keys.
    /// </summary>
    public void AdjustSMeter(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        // S-meter is display only — announce current reading
        var key = RawKey(e);
        if (key == Key.Space)
        {
            string reading = _window.FreqOut.Read("SMeter").Trim();
            Radios.ScreenReaderOutput.Speak($"S meter {reading}", VerbosityLevel.Terse);
            e.Handled = true;
        }
    }

    #endregion

    #region AdjustMute

    /// <summary>
    /// Mute field handler — Space/M toggles mute on the current active slice.
    /// </summary>
    public void AdjustMute(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        var key = RawKey(e);
        char ch = KeyToChar(e);

        if (key == Key.Space || ch == 'M')
        {
            bool newMute = !Rig.SliceMute;
            Rig.SliceMute = newMute;
            if (newMute) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
            Radios.ScreenReaderOutput.Speak(newMute ? "Muted" : "Unmuted", VerbosityLevel.Terse);
            e.Handled = true;
        }
    }

    #endregion

    #region AdjustVolume

    /// <summary>
    /// Volume field handler — Up/Down adjusts gain by GainIncrement (10).
    /// Reuses the existing AdjustGain helper.
    /// </summary>
    public void AdjustVolume(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        var key = RawKey(e);
        int vfo = Rig.RXVFO;

        switch (key)
        {
            case Key.Up:
                if (Rig.ValidVFO(vfo))
                    AdjustGain(vfo, FlexBase.GainIncrement);
                e.Handled = true;
                break;
            case Key.Down:
                if (Rig.ValidVFO(vfo))
                    AdjustGain(vfo, -FlexBase.GainIncrement);
                e.Handled = true;
                break;
        }
    }

    #endregion

    #region AdjustOffset

    /// <summary>
    /// FM offset field handler.
    /// </summary>
    public void AdjustOffset(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        var key = RawKey(e);
        char ch = KeyToChar(e);

        if (ch == '+')
        {
            Rig.OffsetDirection = FlexBase.OffsetDirections.plus;
            Radios.ScreenReaderOutput.Speak("Offset plus", VerbosityLevel.Terse);
            e.Handled = true;
        }
        else if (ch == '-')
        {
            Rig.OffsetDirection = FlexBase.OffsetDirections.minus;
            Radios.ScreenReaderOutput.Speak("Offset minus", VerbosityLevel.Terse);
            e.Handled = true;
        }
        else if (key == Key.Space || key == Key.Up || key == Key.Down)
        {
            // Cycle offset direction: off → plus → minus → off
            var dir = Rig.OffsetDirection;
            dir = dir switch
            {
                FlexBase.OffsetDirections.off => FlexBase.OffsetDirections.plus,
                FlexBase.OffsetDirections.plus => FlexBase.OffsetDirections.minus,
                _ => FlexBase.OffsetDirections.off
            };
            Rig.OffsetDirection = dir;
            Radios.ScreenReaderOutput.Speak($"Offset {dir}", VerbosityLevel.Terse);
            e.Handled = true;
        }
    }

    #endregion

    #region AdjustMem

    /// <summary>
    /// Memory field handler — navigate memories via CurrentMemoryChannel.
    /// </summary>
    public void AdjustMem(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        var key = RawKey(e);

        switch (key)
        {
            case Key.Up:
                Rig.CurrentMemoryChannel++;
                Rig.SelectMemory();
                Radios.ScreenReaderOutput.Speak($"Memory {Rig.CurrentMemoryChannel}", VerbosityLevel.Terse);
                e.Handled = true;
                break;
            case Key.Down:
                Rig.CurrentMemoryChannel--;
                Rig.SelectMemory();
                Radios.ScreenReaderOutput.Speak($"Memory {Rig.CurrentMemoryChannel}", VerbosityLevel.Terse);
                e.Handled = true;
                break;
        }
    }

    #endregion

    #region Modern Mode Tuning — Sprint 13B

    /// <summary>
    /// Modern mode frequency handler — simplified coarse/fine tuning.
    /// Up/Down = tune by current step. C = toggle coarse/fine mode.
    /// PageUp/PageDown = cycle step within current mode.
    /// F = toggle frequency readout. S = announce current step.
    /// </summary>
    public void AdjustFreqModern(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        var key = RawKey(e);
        char ch = KeyToChar(e);

        // Quick-type frequency entry: digits, dot, Enter, Escape
        if (HandleQuickTypeKey(key, ch, e)) return;

        switch (key)
        {
            case Key.Up:
                TuneFreq((ulong)CurrentTuneStep);
                e.Handled = true;
                break;

            case Key.Down:
                TuneFreq(unchecked((ulong)(-(long)CurrentTuneStep)));
                e.Handled = true;
                break;

            case Key.PageUp:
                CycleStep(1);
                e.Handled = true;
                break;

            case Key.PageDown:
                CycleStep(-1);
                e.Handled = true;
                break;

            default:
                if (ch == 'C')
                {
                    // Toggle coarse/fine mode
                    _coarseMode = !_coarseMode;
                    string modeName = _coarseMode ? "Coarse" : "Fine";
                    Radios.ScreenReaderOutput.Speak(
                        $"{modeName}, {FormatStepForSpeech(CurrentTuneStep)}", VerbosityLevel.Terse, true);
                    e.Handled = true;
                }
                else if (ch == 'S' && Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    // Shift+S: Announce current step
                    string modeName = _coarseMode ? "Coarse" : "Fine";
                    Radios.ScreenReaderOutput.Speak(
                        $"{modeName}, {FormatStepForSpeech(CurrentTuneStep)}", VerbosityLevel.Terse, true);
                    e.Handled = true;
                }
                else if (ch == 'F' && Keyboard.Modifiers == ModifierKeys.None)
                {
                    // F: One-shot read current frequency
                    _window.SpeakFrequency();
                    e.Handled = true;
                }
                else if (ch == 'M')
                {
                    // Mute/unmute active slice
                    if (Rig != null)
                    {
                        bool newMute = !Rig.SliceMute;
                        Rig.SliceMute = newMute;
                        if (newMute) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
                        Radios.ScreenReaderOutput.Speak(
                            newMute ? "Muted" : "Unmuted", VerbosityLevel.Terse, true);
                    }
                    e.Handled = true;
                }
                else if (ch == 'V')
                {
                    // Cycle to next slice (VFO)
                    CycleVFO(1);
                    e.Handled = true;
                }
                else if (ch == 'R')
                {
                    // Toggle RIT
                    if (Rig != null)
                    {
                        var rit = new FlexBase.RITData(Rig.RIT);
                        rit.Active = !rit.Active;
                        Rig.RIT = rit;
                        if (rit.Active) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
                        Radios.ScreenReaderOutput.Speak(
                            rit.Active ? "RIT on" : "RIT off", VerbosityLevel.Terse, true);
                    }
                    e.Handled = true;
                }
                else if (ch == 'X')
                {
                    // Toggle XIT
                    if (Rig != null)
                    {
                        var xit = new FlexBase.RITData(Rig.XIT);
                        xit.Active = !xit.Active;
                        Rig.XIT = xit;
                        if (xit.Active) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
                        Radios.ScreenReaderOutput.Speak(
                            xit.Active ? "XIT on" : "XIT off", VerbosityLevel.Terse, true);
                    }
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>
    /// Sprint 26 Phase 8: public wrappers for menu access to tuning actions
    /// that are also bound to hotkeys on the frequency field (C, PageUp/Down,
    /// Shift+S). The "JJ Flexible in threes" pattern — every tuning action
    /// reachable via field keypress, global hotkey, AND menu item.
    /// </summary>
    public void ToggleCoarseFineFromMenu()
    {
        _coarseMode = !_coarseMode;
        string modeName = _coarseMode ? "Coarse" : "Fine";
        Radios.ScreenReaderOutput.Speak(
            $"{modeName}, {FormatStepForSpeech(CurrentTuneStep)}", VerbosityLevel.Terse, true);
    }

    /// <summary>Step up or down through the active step list (menu caller).</summary>
    public void CycleStepFromMenu(int direction)
    {
        CycleStep(direction);
    }

    /// <summary>Announce current tuning mode + step (menu caller).</summary>
    public void SpeakCurrentStepFromMenu()
    {
        string modeName = _coarseMode ? "Coarse" : "Fine";
        Radios.ScreenReaderOutput.Speak(
            $"{modeName}, {FormatStepForSpeech(CurrentTuneStep)}", VerbosityLevel.Terse, true);
    }

    /// <summary>
    /// Cycle through the active step list (coarse or fine depending on current mode).
    /// </summary>
    private void CycleStep(int direction)
    {
        if (_coarseMode)
        {
            int newIndex = _coarseStepIndex + direction;
            if (newIndex < 0) newIndex = 0;
            if (newIndex >= _coarseSteps.Length) newIndex = _coarseSteps.Length - 1;
            _coarseStepIndex = newIndex;
        }
        else
        {
            int newIndex = _fineStepIndex + direction;
            if (newIndex < 0) newIndex = 0;
            if (newIndex >= _fineSteps.Length) newIndex = _fineSteps.Length - 1;
            _fineStepIndex = newIndex;
        }
        string modeName = _coarseMode ? "Coarse" : "Fine";
        Radios.ScreenReaderOutput.Speak($"{modeName}, {FormatStepForSpeech(CurrentTuneStep)}", VerbosityLevel.Terse, true);
        SaveStepSizes?.Invoke(CoarseTuneStep, FineTuneStep);
    }

    /// <summary>
    /// Format a step size in Hz to a spoken string like "5 kilohertz" or "100 hertz".
    /// </summary>
    internal static string FormatStepForSpeech(int hz)
    {
        if (hz >= 1000000) return $"{hz / 1000000} megahertz";
        if (hz >= 1000) return $"{hz / 1000} kilohertz";
        return $"{hz} hertz";
    }

    /// <summary>
    /// Adaptive filter step: 10 Hz below 200 Hz bandwidth, 25 Hz below 500 Hz, 50 Hz default.
    /// </summary>
    private static int GetAdaptiveFilterStep(int low, int high)
    {
        int width = high - low;
        if (width < 200) return 10;
        if (width < 500) return 25;
        if (width < 2000) return 50;
        if (width < 5000) return 100;
        return 200;
    }

    /// <summary>
    /// Handle bracket keys for filter adjustment.
    /// Sprint 15 key scheme:
    ///   [ = low edge down (expand low side)
    ///   ] = high edge up (expand high side)
    ///   Ctrl+[ = squeeze (narrow both edges equally)
    ///   Ctrl+] = pull (widen both edges equally)
    ///   Shift+[ = shift passband down (slide)
    ///   Shift+] = shift passband up (slide)
    ///   Alt+[ = previous preset, Alt+] = next preset (handled separately)
    /// Uses SetFilter() to set both edges atomically (Sprint 14 race fix).
    /// </summary>
    public void HandleFilterHotkey(KeyEventArgs e)
    {
        if (Rig == null) return;
        var key = RawKey(e);
        var mods = System.Windows.Input.Keyboard.Modifiers;
        bool shift = (mods & ModifierKeys.Shift) != 0;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool alt = (mods & ModifierKeys.Alt) != 0;
        const int minWidth = 50;

        // Alt+bracket = preset cycling (handled in Phase 4)
        if (alt && !ctrl && !shift)
        {
            HandlePresetCycle(key, e);
            return;
        }

        int low = Rig.FilterLow;
        int high = Rig.FilterHigh;
        int origLow = low, origHigh = high;
        int step = GetAdaptiveFilterStep(low, high);
        var (lowMin, highMax) = GetFilterBounds();

        if (shift && !ctrl) // Shift = slide passband (unchanged from Sprint 14)
        {
            if (key == Key.OemOpenBrackets)
            {
                low -= step;
                high -= step;
            }
            else if (key == Key.OemCloseBrackets)
            {
                low += step;
                high += step;
            }
            else return;
        }
        else if (ctrl && !shift) // Ctrl = squeeze/pull (both edges equally)
        {
            if (key == Key.OemOpenBrackets)
            {
                // Squeeze: narrow both edges inward
                int newLow = low + step;
                int newHigh = high - step;
                if (newHigh - newLow >= minWidth) { low = newLow; high = newHigh; }
                else
                {
                    Radios.ScreenReaderOutput.Speak("Filter at minimum", VerbosityLevel.Terse, true);
                    e.Handled = true;
                    return;
                }
            }
            else if (key == Key.OemCloseBrackets)
            {
                // Pull: widen both edges outward
                low -= step;
                high += step;
            }
            else return;
        }
        else if (!ctrl && !shift && !alt) // Unmodified = independent edges or edge mode
        {
            // Check for double-tap to enter/exit edge selection mode.
            // Sprint 28 Phase 2 — tolerance window is now user-configurable via
            // Settings > Accessibility > Double-tap tolerance (default 500 ms,
            // up from the previous hardcoded 300 ms). Users who preferred the
            // snappier feel can pick "Quick (250 ms)".
            var now = DateTime.UtcNow;
            bool isDoubleTap = (key == _lastBracketKey) &&
                               (now - _lastBracketTime).TotalMilliseconds < Radios.AccessibilityConfig.Current.DoubleTapToleranceMs &&
                               _filterEdgeMode == FilterEdgeMode.None;
            _lastBracketKey = key;
            _lastBracketTime = now;

            if (isDoubleTap)
            {
                // Enter edge selection mode
                _filterEdgeMode = key == Key.OemOpenBrackets ? FilterEdgeMode.LowerEdge : FilterEdgeMode.UpperEdge;
                string edgeName = _filterEdgeMode == FilterEdgeMode.LowerEdge ? "lower" : "upper";
                Radios.ScreenReaderOutput.Speak($"Adjust {edgeName} filter. Brackets move edge. Escape to exit.", true);
                EarconPlayer.FilterEdgeEnterTone();
                ResetFilterEdgeTimeout();
                e.Handled = true;
                return;
            }

            if (_filterEdgeMode != FilterEdgeMode.None)
            {
                // In edge mode: brackets move the selected edge in either direction
                ResetFilterEdgeTimeout();
                if (_filterEdgeMode == FilterEdgeMode.LowerEdge)
                {
                    if (key == Key.OemOpenBrackets) low -= step;
                    else if (key == Key.OemCloseBrackets) low += step;
                    else return;
                }
                else // UpperEdge
                {
                    if (key == Key.OemOpenBrackets) high -= step;
                    else if (key == Key.OemCloseBrackets) high += step;
                    else return;
                }
                if (high - low < minWidth) { Radios.ScreenReaderOutput.Speak("Filter at minimum", VerbosityLevel.Terse, true); e.Handled = true; return; }
                EarconPlayer.FilterEdgeMoveTone(_filterEdgeMode == FilterEdgeMode.LowerEdge);
            }
            else
            {
                // Normal single-tap: expand in one direction
                if (key == Key.OemOpenBrackets)
                    low -= step;
                else if (key == Key.OemCloseBrackets)
                    high += step;
                else return;
            }
        }
        else return;

        // Clamp to mode-specific bounds — track what was requested vs what survived clamping
        int requestedLow = low, requestedHigh = high;
        low = Math.Max(low, lowMin);
        high = Math.Min(high, highMax);
        if (high - low < minWidth) high = low + minWidth; // safety

        Rig.SetFilter(low, high);

        // Boundary announcements — only announce "at limit" when clamping actually
        // restricted movement, not just because an edge sits at a mode boundary.
        // In LSB, high is naturally 0 == highMax; the old OR check falsely said
        // "at limit" on every adjustment even when the low edge had room to move.
        if (low == origLow && high == origHigh)
        {
            Radios.ScreenReaderOutput.Speak("Filter at limit", VerbosityLevel.Terse, true);
        }
        else
        {
            int width = high - low;
            string widthStr = width >= 1000 ? $"{width / 1000.0:F1} k" : $"{width}";
            bool lowClamped = low != requestedLow;
            bool highClamped = high != requestedHigh;
            string atLimit = "";
            if (lowClamped && highClamped)
                atLimit = ", at limit";
            else if (lowClamped)
                atLimit = ", lower limit";
            else if (highClamped)
                atLimit = ", upper limit";
            Radios.ScreenReaderOutput.Speak($"Filter {low} to {high}, {widthStr}{atLimit}", VerbosityLevel.Terse, true);
        }
        e.Handled = true;
    }

    /// <summary>
    /// Handle Alt+[ / Alt+] for filter preset cycling.
    /// Alt+[ = previous (narrower) preset, Alt+] = next (wider) preset.
    /// </summary>
    private void HandlePresetCycle(Key key, KeyEventArgs e)
    {
        if (Rig == null || FilterPresets == null)
        {
            Radios.ScreenReaderOutput.Speak("No presets loaded", VerbosityLevel.Critical, true);
            e.Handled = true;
            return;
        }

        string mode = Rig.Mode ?? "USB";
        int direction = key == Key.OemCloseBrackets ? 1 : -1;
        var preset = FilterPresets.CyclePreset(mode, Rig.FilterLow, Rig.FilterHigh, direction);

        if (preset == null)
        {
            string boundary = direction > 0 ? "Widest preset" : "Narrowest preset";
            Radios.ScreenReaderOutput.Speak(boundary, VerbosityLevel.Terse, true);
        }
        else
        {
            var (mirroredLow, mirroredHigh) = FilterPresets.MirrorForMode(mode, preset.Low, preset.High);
            Rig.SetFilter(mirroredLow, mirroredHigh);
            Radios.ScreenReaderOutput.Speak($"{preset.Name}, {preset.FormatForSpeech()}", VerbosityLevel.Terse, true);
        }
        e.Handled = true;
    }

    /// <summary>
    /// Reset the 10-second filter edge mode timeout. After 10s of inactivity, exits edge mode.
    /// </summary>
    private void ResetFilterEdgeTimeout()
    {
        _filterEdgeTimeout?.Cancel();
        _filterEdgeTimeout = new CancellationTokenSource();
        var token = _filterEdgeTimeout.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(10000, token);
                _filterEdgeMode = FilterEdgeMode.None;
                _window.Dispatcher.Invoke(() =>
                {
                    EarconPlayer.FilterEdgeExitTone();
                    Radios.ScreenReaderOutput.Speak("Filter edge mode ended", VerbosityLevel.Terse);
                });
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>
    /// Cancel filter edge mode (called on Escape).
    /// </summary>
    public void CancelFilterEdgeMode()
    {
        if (_filterEdgeMode != FilterEdgeMode.None)
        {
            _filterEdgeMode = FilterEdgeMode.None;
            _filterEdgeTimeout?.Cancel();
            EarconPlayer.FilterEdgeExitTone();
            Radios.ScreenReaderOutput.Speak("Filter edge mode cancelled", VerbosityLevel.Terse);
        }
    }

    /// <summary>
    /// True if currently in filter edge selection mode.
    /// </summary>
    public bool InFilterEdgeMode => _filterEdgeMode != FilterEdgeMode.None;

    /// <summary>
    /// Returns which filter edge is grabbed, or null if not in edge mode.
    /// </summary>
    public string? FilterEdgeStatus =>
        _filterEdgeMode == FilterEdgeMode.LowerEdge ? "lower filter edge grabbed" :
        _filterEdgeMode == FilterEdgeMode.UpperEdge ? "upper filter edge grabbed" :
        null;

    /// <summary>
    /// Returns active filter preset description, or null if not on a named preset.
    /// </summary>
    public string? ActiveFilterPresetStatus
    {
        get
        {
            if (FilterPresets == null || Rig == null) return null;
            string mode = Rig.Mode ?? "USB";
            int idx = FilterPresets.FindActivePreset(mode, Rig.FilterLow, Rig.FilterHigh);
            if (idx < 0) return null;
            var presets = FilterPresets.GetPresetsForMode(mode);
            return $"filter {presets[idx].FormatForSpeech()}";
        }
    }

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
            "CW" => (-12000, 12000), // actual CW bounds depend on pitch, this is the outer limit
            "USB" or "DIGU" or "FDV" => (0, 12000),
            _ => (-12000, 12000) // AM, DSB, SAM, etc.
        };
    }

    #endregion

    #region Band Navigation

    /// <summary>
    /// Ordered list of bands supported by FlexRadio transceivers.
    /// Used for BandUp/BandDown navigation.
    /// </summary>
    private static readonly Bands.BandNames[] FlexBands =
    {
        Bands.BandNames.m160,
        Bands.BandNames.m80,
        Bands.BandNames.m60,
        Bands.BandNames.m40,
        Bands.BandNames.m30,
        Bands.BandNames.m20,
        Bands.BandNames.m17,
        Bands.BandNames.m15,
        Bands.BandNames.m12,
        Bands.BandNames.m10,
        Bands.BandNames.m6,
    };

    /// <summary>
    /// Human-readable band names for speech output.
    /// </summary>
    private static string BandDisplayName(Bands.BandNames band) => band switch
    {
        Bands.BandNames.m160 => "160 meter",
        Bands.BandNames.m80 => "80 meter",
        Bands.BandNames.m60 => "60 meter",
        Bands.BandNames.m40 => "40 meter",
        Bands.BandNames.m30 => "30 meter",
        Bands.BandNames.m20 => "20 meter",
        Bands.BandNames.m17 => "17 meter",
        Bands.BandNames.m15 => "15 meter",
        Bands.BandNames.m12 => "12 meter",
        Bands.BandNames.m10 => "10 meter",
        Bands.BandNames.m6 => "6 meter",
        _ => band.ToString()
    };

    /// <summary>
    /// Band memory — remembers last frequency per band per mode.
    /// Loaded when radio connects, saved when band changes.
    /// </summary>
    public BandMemory? BandMem { get; set; }

    /// <summary>
    /// When true, band jumps save/recall frequency per band+mode.
    /// When false, band jumps always go to band center.
    /// </summary>
    public bool BandMemoryEnabled { get; set; } = true;

    /// <summary>
    /// License configuration for boundary notifications and TX lockout.
    /// </summary>
    public LicenseConfig? License { get; set; }

    /// <summary>
    /// Delegate to get the config directory path for saving band memory.
    /// </summary>
    public Func<string>? GetConfigDirectory { get; set; }

    /// <summary>
    /// Delegate to get the current operator name for saving band memory.
    /// </summary>
    public Func<string>? GetOperatorName { get; set; }

    /// <summary>
    /// Current typing sound mode for frequency entry keystrokes.
    /// </summary>
    public TypingSoundMode TypingSound { get; set; } = TypingSoundMode.Beep;

    /// <summary>
    /// Handle a successful calibration reference unlock.
    /// Plays verification tone, saves state, speaks confirmation.
    /// </summary>
    /// <summary>Public entry point for calibration unlock from JJ Ctrl+F.</summary>
    public void HandleCalibrationPublic(string referenceName) => HandleCalibrationUnlock(referenceName);

    private void HandleCalibrationUnlock(string referenceName)
    {
        // Reset handler — clear all unlocks
        if (referenceName == CalibrationEngine.ResetRef)
        {
            string configDir2 = GetConfigDirectory?.Invoke() ?? "";
            if (!string.IsNullOrEmpty(configDir2))
            {
                var config2 = AudioOutputConfig.Load(configDir2);
                config2.TuningHash = "";
                config2.TypingSound = TypingSoundMode.Beep;
                config2.Save(configDir2);

                // Also clear in Radios subdirectory
                string radiosDir2 = System.IO.Path.Combine(configDir2, "Radios");
                if (System.IO.Directory.Exists(radiosDir2))
                {
                    var rc = AudioOutputConfig.Load(radiosDir2);
                    rc.TuningHash = "";
                    rc.TypingSound = TypingSoundMode.Beep;
                    rc.Save(radiosDir2);
                }
            }
            TypingSound = TypingSoundMode.Beep;
            EarconPlayer.ReverseBoomTone();
            Radios.ScreenReaderOutput.Speak("All modes reset.", VerbosityLevel.Critical, true);
            return;
        }

        CalibrationEngine.PlayVerificationTone(referenceName);

        string configDir = GetConfigDirectory?.Invoke() ?? "";
        if (!string.IsNullOrEmpty(configDir))
        {
            string tuningHash = "";
            // Save to root config dir
            var config = AudioOutputConfig.Load(configDir);
            var unlocked = new HashSet<string>(
                (config.TuningHash ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
            unlocked.Add(referenceName);
            tuningHash = string.Join(",", unlocked);
            config.TuningHash = tuningHash;
            config.Save(configDir);

            // Also save to Radios subdirectory (where Settings reads from)
            string radiosDir = System.IO.Path.Combine(configDir, "Radios");
            if (System.IO.Directory.Exists(radiosDir))
            {
                var radiosConfig = AudioOutputConfig.Load(radiosDir);
                radiosConfig.TuningHash = tuningHash;
                radiosConfig.Save(radiosDir);
            }
        }

        // Load extended sounds based on which reference was unlocked
        if (referenceName == CalibrationEngine.Ref2)
        {
            CalibrationEngine.LoadKeyboardSounds();
            Radios.ScreenReaderOutput.Speak("Mechanical keyboard mode unlocked!", VerbosityLevel.Critical, true);
        }
        else if (referenceName == CalibrationEngine.Ref1)
        {
            Radios.ScreenReaderOutput.Speak("Touch-tone mode unlocked!", VerbosityLevel.Critical, true);
        }
    }

    /// <summary>
    /// Check if a specific calibration reference has been unlocked.
    /// </summary>
    public static bool IsCalibrationUnlocked(string referenceName, string tuningHash)
    {
        if (string.IsNullOrEmpty(tuningHash)) return false;
        return tuningHash.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(s => s.Equals(referenceName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Tracks the last band the user was on, for boundary detection.
    /// </summary>
    private Bands.BandNames? _lastBand;
    private string? _lastSubBandKey; // tracks license sub-band division for boundary notifications

    /// <summary>
    /// Jump to a specific band. Uses band memory to restore last frequency
    /// for the current mode on that band.
    /// </summary>
    public void BandJump(Bands.BandNames targetBand)
    {
        if (Rig == null || SetRXFrequency == null || GetRXFrequency == null) return;

        // Save current frequency to band memory before jumping
        if (BandMemoryEnabled) SaveCurrentToBandMemory();

        string mode = Rig.Mode ?? "USB";
        ulong targetFreq;

        if (BandMemoryEnabled && BandMem != null)
        {
            targetFreq = BandMem.GetFrequency(targetBand, mode);
        }
        else
        {
            // No band memory — for channelized bands (60m), use Channel 1;
            // otherwise use band center.
            if (targetBand == Bands.BandNames.m60)
            {
                string country = License?.Country ?? "US";
                var alloc = SixtyMeterChannels.GetAllocation(country);
                if (alloc?.Channels.Length > 0)
                    targetFreq = (ulong)(alloc.Value.Channels[0].FrequencyMHz * 1_000_000.0 + 0.5);
                else
                    targetFreq = 5_332_000; // US Channel 1 fallback
            }
            else
            {
                var bandInfo = Bands.Query(targetBand);
                targetFreq = bandInfo != null ? (bandInfo.Low + bandInfo.High) / 2 : 0;
            }
        }

        if (targetFreq == 0) return;

        // 60m channel validation: snap off-channel frequencies to the nearest valid channel.
        // Catches stale band memory and ensures we never land between channels.
        if (targetBand == Bands.BandNames.m60)
        {
            string country = License?.Country ?? "US";
            if (!SixtyMeterChannels.IsValidSixtyMeterFrequency(country, targetFreq))
            {
                var alloc = SixtyMeterChannels.GetAllocation(country);
                if (alloc?.Channels.Length > 0)
                {
                    double freqMHz = targetFreq / 1_000_000.0;
                    var nearest = alloc.Value.Channels
                        .OrderBy(ch => System.Math.Abs(ch.FrequencyMHz - freqMHz))
                        .First();
                    targetFreq = (ulong)(nearest.FrequencyMHz * 1_000_000.0 + 0.5);
                }
            }
        }

        SetRXFrequency(targetFreq);
        _lastBand = targetBand;

        string bandName = BandDisplayName(targetBand);
        string freqStr = FormatFreqForSpeech(targetFreq);

        // 60m mode advisory: auto-correct mode if inappropriate for the target segment
        string modeAdvisory = "";
        if (targetBand == Bands.BandNames.m60 && Rig != null)
        {
            string country = License?.Country ?? "US";
            var advisory = SixtyMeterChannels.GetModeAdvisory(country, targetFreq, mode);
            if (advisory?.RequiredMode != null)
            {
                Rig.Mode = advisory.Value.RequiredMode;
                modeAdvisory = $", mode set to {advisory.Value.RequiredMode} for {advisory.Value.SegmentLabel}";
            }
        }

        Radios.ScreenReaderOutput.Speak($"{bandName} band, {freqStr}{modeAdvisory}", VerbosityLevel.Terse, interrupt: true);
        EarconPlayer.BandBoundaryBeep();

        // Save band memory
        if (BandMemoryEnabled) SaveBandMemory();
    }

    /// <summary>
    /// Navigate to the next (+1) or previous (-1) band in the FlexBands list.
    /// </summary>
    public void BandNavigate(int direction)
    {
        if (Rig == null || GetRXFrequency == null) return;

        ulong currentFreq = GetRXFrequency();
        var currentBandInfo = Bands.Query(currentFreq);

        int currentIndex = -1;
        if (currentBandInfo != null)
        {
            currentIndex = Array.IndexOf(FlexBands, currentBandInfo.Band);
        }

        // If not on a known band, start from the beginning or end
        if (currentIndex < 0)
        {
            currentIndex = direction > 0 ? -1 : FlexBands.Length;
        }

        int nextIndex = currentIndex + direction;
        if (nextIndex < 0 || nextIndex >= FlexBands.Length)
        {
            string edge = direction > 0 ? "Top" : "Bottom";
            Radios.ScreenReaderOutput.Speak($"{edge} of band list", VerbosityLevel.Terse, interrupt: true);
            return;
        }

        BandJump(FlexBands[nextIndex]);
    }

    /// <summary>
    /// Save the current frequency to band memory for the current band+mode.
    /// </summary>
    private void SaveCurrentToBandMemory()
    {
        if (BandMem == null || Rig == null || GetRXFrequency == null) return;

        ulong freq = GetRXFrequency();
        var bandInfo = Bands.Query(freq);
        if (bandInfo == null) return;

        string mode = Rig.Mode ?? "USB";
        BandMem.SetFrequency(bandInfo.Band, mode, freq);
    }

    /// <summary>
    /// Persist band memory to disk.
    /// </summary>
    private void SaveBandMemory()
    {
        if (BandMem == null || GetConfigDirectory == null || GetOperatorName == null) return;
        try
        {
            BandMem.Save(GetConfigDirectory(), GetOperatorName());
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"SaveBandMemory failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Check band boundaries after a frequency change. Speaks band entry
    /// notifications and license boundary crossings if enabled.
    /// Called from TuneFreq after frequency changes.
    /// </summary>
    /// <summary>
    /// Initialize band tracking from current frequency so the first tune
    /// doesn't trigger a false boundary notification.
    /// </summary>
    public void InitializeBandTracking(ulong currentFreq)
    {
        var bandInfo = Bands.Query(currentFreq);
        _lastBand = bandInfo?.Band;
        if (_lastBand != null)
            _lastSubBandKey = GetSubBandKey(currentFreq, _lastBand.Value);
    }

    public void CheckBandBoundary(ulong newFreq)
    {
        if (License == null || !License.BoundaryNotifications) return;

        var newBandInfo = Bands.Query(newFreq);
        var newBand = newBandInfo?.Band;

        if (_lastBand != null && newBand != null && newBand != _lastBand)
        {
            string bandName = BandDisplayName(newBand.Value);
            Radios.ScreenReaderOutput.Speak($"Entering {bandName} band", VerbosityLevel.Terse);
            EarconPlayer.BandBoundaryBeep();

            // Save band memory on band change
            SaveBandMemory();
        }

        // Check license sub-band boundaries within the same band
        if (newBand != null)
        {
            string? newSubKey = GetSubBandKey(newFreq, newBand.Value);
            if (newSubKey != _lastSubBandKey
                && (_lastBand == null || _lastBand == newBand)) // only within same band
            {
                // Beep always fires; speech only at Normal+ verbosity so Terse
                // users just hear the tone without tuning speech getting chatty.
                EarconPlayer.BandBoundaryBeep();
                if (newSubKey != null)
                    Radios.ScreenReaderOutput.Speak($"Entering {newSubKey}", VerbosityLevel.Chatty, interrupt: true);
                else if (_lastSubBandKey != null)
                    Radios.ScreenReaderOutput.Speak($"Leaving {_lastSubBandKey}", VerbosityLevel.Chatty, interrupt: true);
            }
            _lastSubBandKey = newSubKey;
        }

        _lastBand = newBand;
    }

    /// <summary>
    /// Get a key identifying which license sub-band division the frequency is in.
    /// Returns a descriptive string like "Extra CW" or "General Phone", or null.
    /// </summary>
    private string? GetSubBandKey(ulong freq, Bands.BandNames band)
    {
        // 60m: detect digital segment boundaries only.
        // Channels are point frequencies, not zones — they don't need boundary tracking.
        // The digital segment has real edges that matter for mode legality.
        if (band == Bands.BandNames.m60)
        {
            string country = License?.Country ?? "US";
            double freqMHz = freq / 1_000_000.0;
            var alloc = Radios.SixtyMeterChannels.GetAllocation(country);
            if (alloc?.Digi is { } digi && freqMHz >= digi.StartMHz && freqMHz <= digi.EndMHz)
                return "digital segment";
            return null;
        }

        if (License == null) return null;
        var bandInfo = Bands.Query(band, License.LicenseClass);
        if (bandInfo?.Divisions == null) return null;

        foreach (var div in bandInfo.Divisions)
        {
            if (freq >= div.Low && freq <= div.High)
            {
                string licStr = div.License != null && div.License.Length > 0
                    ? FormatLicense(div.License[0]) : "All";
                string modeStr = div.Mode != null && div.Mode.Length > 0
                    ? FormatMode(div.Mode[0]) : "";
                return string.IsNullOrEmpty(modeStr) ? licStr : $"{licStr} {modeStr}";
            }
        }
        return null;
    }

    private static string FormatLicense(Bands.Licenses lic) => lic switch
    {
        Bands.Licenses.novice => "Novice",
        Bands.Licenses.technition => "Technician",
        Bands.Licenses.general => "General",
        Bands.Licenses.advanced => "Advanced",
        Bands.Licenses.extra => "Extra",
        _ => "All"
    };

    private static string FormatMode(Bands.Modes mode) => mode switch
    {
        Bands.Modes.CW => "CW",
        Bands.Modes.MCW => "CW",
        Bands.Modes.PhoneCW => "Phone and CW",
        Bands.Modes.USB => "USB",
        Bands.Modes.Image => "Image",
        Bands.Modes.RTTYData => "RTTY and Data",
        Bands.Modes.M60Data => "Data",
        _ => ""
    };

    /// <summary>
    /// Check if the current frequency + filter is within the operator's
    /// licensed band segment. Returns true if TX is allowed, false if blocked.
    /// </summary>
    public bool CanTransmitHere()
    {
        if (License == null || !License.TxLockout) return true;
        if (Rig == null || GetRXFrequency == null) return true;

        ulong freq = GetRXFrequency();
        int filterLow = Rig.FilterLow;
        int filterHigh = Rig.FilterHigh;

        // Calculate signal extent (freq is carrier, filter offsets are relative)
        // For USB: signal is freq + filterLow to freq + filterHigh
        // For LSB: filter values are negative, so signal is freq + filterLow to freq + filterHigh
        long signalLow = (long)freq + filterLow;
        long signalHigh = (long)freq + filterHigh;
        if (signalLow < 0) signalLow = 0;
        if (signalHigh < 0) signalHigh = 0;

        var bandInfo = Bands.Query((ulong)signalLow);
        if (bandInfo == null)
        {
            // Not on any band — block TX
            return false;
        }

        // Check if the entire signal is within licensed divisions
        var licensedBand = Bands.Query(bandInfo.Band, License.LicenseClass);
        if (licensedBand?.Divisions == null)
        {
            // No divisions for this license on this band — block
            return false;
        }

        // Check that the entire signal extent falls within at least one licensed division
        foreach (var div in licensedBand.Divisions)
        {
            if ((ulong)signalLow >= div.Low && (ulong)signalHigh <= div.High)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Get a message explaining why TX is blocked at the current frequency.
    /// </summary>
    public string GetTxLockoutMessage()
    {
        return "Unable to transmit here. Select license options in Settings to change.";
    }

    #endregion

    #region Helpers

    private FrequencyDisplay.DisplayField? GetField(string key)
    {
        int pos = _window.FreqOut.GetFieldPosition(key);
        return _window.FreqOut.PositionToField(pos);
    }

    #endregion
}
