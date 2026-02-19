using System;
using System.Diagnostics;
using System.Windows.Input;
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

    public FreqOutHandlers(MainWindow window)
    {
        _window = window;
    }

    private FlexBase? Rig => _window.RigControl;

    /// <summary>
    /// Convert a WPF KeyEventArgs to a simple key character for digit/letter handlers.
    /// </summary>
    private static char KeyToChar(KeyEventArgs e)
    {
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
        JJTrace.Tracing.TraceLine($"AdjustFreq: key={key} ch='{ch}' handled={e.Handled}", System.Diagnostics.TraceLevel.Verbose);

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
                Radios.ScreenReaderOutput.Speak($"Step {mult} {unit}".Trim());
            }
            e.Handled = true;
            return;
        }

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
                else if (ch >= '0' && ch <= '9')
                {
                    EnterFreqDigit(ch, posInField, fieldLen);
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
                        Radios.ScreenReaderOutput.Speak("Step reset");
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
                else if (ch == 'F')
                {
                    _freqReadout = !_freqReadout;
                    Radios.ScreenReaderOutput.Speak(
                        _freqReadout ? "Frequency readout on" : "Frequency readout off", true);
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
                Radios.ScreenReaderOutput.Speak(FormatFreqForSpeech(newFreq), true);
            }
        }
    }

    /// <summary>
    /// Format a frequency in Hz to a spoken string like "14.250.000".
    /// Done locally to avoid NullRef through the VB.NET delegate chain.
    /// </summary>
    private static string FormatFreqForSpeech(ulong freqHz)
    {
        string s = freqHz.ToString();
        while (s.Length < 7) s = "0" + s;
        int len = s.Length;
        return s.Substring(0, len - 6) + "." + s.Substring(len - 6, 3) + "." + s.Substring(len - 3);
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
                        Radios.ScreenReaderOutput.Speak(FormatFreq(newFreq.ToString()));
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
                        Radios.ScreenReaderOutput.Speak($"Slice {slice}");
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

    private void CycleVFO(int direction)
    {
        if (Rig == null) return;
        int current = Rig.RXVFO;
        int total = Rig.TotalNumSlices;
        if (total <= 0) return;

        int next = current + direction;
        if (next >= total) next = 0;
        if (next < 0) next = total - 1;

        // Find next valid VFO
        int attempts = 0;
        while (!Rig.ValidVFO(next) && attempts < total)
        {
            next += direction;
            if (next >= total) next = 0;
            if (next < 0) next = total - 1;
            attempts++;
        }

        if (Rig.ValidVFO(next))
        {
            Rig.RXVFO = next;
            Radios.ScreenReaderOutput.Speak($"Slice {next}");
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
                // Cycle to next valid slice
                CycleVFO(1);
                e.Handled = true;
                break;
            case 'M':
                // Toggle mute on current slice
                if (Rig.ValidVFO(vfo))
                {
                    bool muted = !Rig.GetVFOAudio(vfo);
                    Rig.SetVFOAudio(vfo, muted);
                    Radios.ScreenReaderOutput.Speak(muted ? "Mute off" : "Mute on");
                }
                e.Handled = true;
                break;
            case 'T':
                if (Rig.CanTransmit && Rig.ValidVFO(vfo))
                {
                    Rig.TXVFO = vfo;
                    Radios.ScreenReaderOutput.Speak($"Slice {vfo} transmit");
                }
                e.Handled = true;
                break;
            case 'X':
                // Transceive: set both RX and TX to current slice
                if (Rig.ValidVFO(vfo))
                {
                    Rig.RXVFO = vfo;
                    if (Rig.CanTransmit)
                        Rig.TXVFO = vfo;
                    Radios.ScreenReaderOutput.Speak($"Slice {vfo} transceive");
                }
                e.Handled = true;
                break;
            case 'L':
                if (Rig.ValidVFO(vfo))
                {
                    Rig.SetVFOPan(vfo, FlexBase.MinPan);
                    Radios.ScreenReaderOutput.Speak("Pan left");
                }
                e.Handled = true;
                break;
            case 'C':
                if (Rig.ValidVFO(vfo))
                {
                    Rig.SetVFOPan(vfo, (FlexBase.MaxPan - FlexBase.MinPan) / 2);
                    Radios.ScreenReaderOutput.Speak("Pan center");
                }
                e.Handled = true;
                break;
            case 'R':
                if (Rig.ValidVFO(vfo))
                {
                    Rig.SetVFOPan(vfo, FlexBase.MaxPan);
                    Radios.ScreenReaderOutput.Speak("Pan right");
                }
                e.Handled = true;
                break;
            case '.':
                // Create/activate a new slice
                if (Rig.NewSlice())
                {
                    Radios.ScreenReaderOutput.Speak($"Slice {Rig.MyNumSlices - 1} activated", true);
                }
                else
                {
                    Radios.ScreenReaderOutput.Speak("All slices in use", true);
                }
                e.Handled = true;
                break;
            case ',':
                // Release/remove the current slice
                if (Rig.MyNumSlices <= 1)
                {
                    Radios.ScreenReaderOutput.Speak("Cannot release last slice", true);
                }
                else
                {
                    int toRemove = vfo;
                    // Find another slice to switch to
                    int switchTo = -1;
                    for (int i = 0; i < Rig.MyNumSlices; i++)
                    {
                        if (i != toRemove)
                        {
                            switchTo = i;
                            break;
                        }
                    }
                    if (switchTo >= 0)
                    {
                        // Move TX away from the slice being removed if needed
                        if (Rig.CanTransmit && toRemove == Rig.TXVFO)
                            Rig.TXVFO = switchTo;
                        Rig.RXVFO = switchTo;
                        if (Rig.RemoveSlice(toRemove))
                        {
                            Radios.ScreenReaderOutput.Speak($"Slice {toRemove} released, slice {switchTo} active", true);
                        }
                        else
                        {
                            Rig.RXVFO = toRemove; // revert
                            Radios.ScreenReaderOutput.Speak("Cannot release this slice", true);
                        }
                    }
                }
                e.Handled = true;
                break;
            default:
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
                }

                if (ch >= '0' && ch <= '9')
                {
                    int target = ch - '0';
                    if (Rig.ValidVFO(target))
                    {
                        Rig.RXVFO = target;
                        Radios.ScreenReaderOutput.Speak($"Slice {target} active");
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
        Radios.ScreenReaderOutput.Speak($"Volume {newVal}", true);
    }

    private void AdjustPan(int vfo, int delta)
    {
        if (Rig == null) return;
        int current = Rig.GetVFOPan(vfo);
        int newVal = Math.Clamp(current + delta, FlexBase.MinPan, FlexBase.MaxPan);
        Rig.SetVFOPan(vfo, newVal);
        Radios.ScreenReaderOutput.Speak($"Pan {newVal}");
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
                    Radios.ScreenReaderOutput.Speak(GetSplitVFOs() ? "Split on" : "Split off");
                }
                e.Handled = true;
                break;
            default:
                if (ch == 'S')
                {
                    SetSplitVFOs?.Invoke(true);
                    Radios.ScreenReaderOutput.Speak("Split on");
                    e.Handled = true;
                }
                else if (ch == 'T')
                {
                    // Show TX frequency
                    SetShowXmitFrequency?.Invoke(true);
                    Radios.ScreenReaderOutput.Speak("Showing transmit frequency");
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
                Radios.ScreenReaderOutput.Speak($"{fieldKey} {(toggled.Active ? "on" : "off")}");
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
                    Radios.ScreenReaderOutput.Speak("Copied RIT to XIT");
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
        Radios.ScreenReaderOutput.Speak($"{label} {sign}{updated.Value}");
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
            Radios.ScreenReaderOutput.Speak(newState == FlexBase.OffOnValues.on ? "VOX on" : "VOX off");
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
            Radios.ScreenReaderOutput.Speak($"S meter {reading}");
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
        int vfo = Rig.RXVFO;

        if (key == Key.Space || ch == 'M')
        {
            if (Rig.ValidVFO(vfo))
            {
                bool audioOn = Rig.GetVFOAudio(vfo);
                Rig.SetVFOAudio(vfo, !audioOn);
                Radios.ScreenReaderOutput.Speak(audioOn ? "Mute on" : "Mute off");
            }
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
            Radios.ScreenReaderOutput.Speak("Offset plus");
            e.Handled = true;
        }
        else if (ch == '-')
        {
            Rig.OffsetDirection = FlexBase.OffsetDirections.minus;
            Radios.ScreenReaderOutput.Speak("Offset minus");
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
            Radios.ScreenReaderOutput.Speak($"Offset {dir}");
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
                Radios.ScreenReaderOutput.Speak($"Memory {Rig.CurrentMemoryChannel}");
                e.Handled = true;
                break;
            case Key.Down:
                Rig.CurrentMemoryChannel--;
                Rig.SelectMemory();
                Radios.ScreenReaderOutput.Speak($"Memory {Rig.CurrentMemoryChannel}");
                e.Handled = true;
                break;
        }
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
