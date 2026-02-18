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
            Key.Space => ' ',
            _ => '\0'
        };
    }

    private static Key RawKey(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

    #region AdjustFreq

    /// <summary>
    /// Frequency field handler — digit-by-digit tuning.
    /// Up/Down tune by position, digits enter frequency, K rounds to KHz.
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

        // Tune step: position maps to power of 10
        // Field is 12 chars: "  7.074 000 " → positions map to GHz down to Hz
        // Position 0-1 = padding, 2 = 10GHz, etc. Simplified: step = 10^(fieldLen-1-posInField)
        ulong step = 1;
        for (int i = 0; i < (fieldLen - 1 - posInField); i++)
            step *= 10;

        switch (key)
        {
            case Key.Up:
                TuneFreq(step);
                e.Handled = true;
                break;

            case Key.Down:
                TuneFreq(unchecked((ulong)(-(long)step)));
                e.Handled = true;
                break;

            default:
                if (ch >= '0' && ch <= '9')
                {
                    EnterFreqDigit(ch, posInField, fieldLen);
                    e.Handled = true;
                }
                else if (ch == 'K')
                {
                    RoundToKHz();
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
                    TuneFreq(step);
                    e.Handled = true;
                }
                else if (ch == 'D')
                {
                    TuneFreq(unchecked((ulong)(-(long)step)));
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
            // Announce new frequency for screen reader
            if (FormatFreq != null)
                Radios.ScreenReaderOutput.Speak(FormatFreq(newFreq.ToString()));
        }
    }

    private void EnterFreqDigit(char digit, int posInField, int fieldLen)
    {
        if (Rig == null || FreqInt64 == null || SetRXFrequency == null) return;
        string currentText = _window.FreqOut.Read("Freq").Trim();
        // Strip formatting (spaces, dots)
        string clean = currentText.Replace(" ", "").Replace(".", "");
        if (posInField >= 0 && posInField < clean.Length)
        {
            char[] chars = clean.ToCharArray();
            chars[posInField] = digit;
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

    #region AdjustRigField (Slice Indicators)

    /// <summary>
    /// Slice indicator handler — mute, active, transmit, pan, volume.
    /// </summary>
    public void AdjustRigField(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        var key = RawKey(e);
        char ch = KeyToChar(e);

        // Determine which VFO this slice indicator represents
        if (!int.TryParse(field.Key, out int vfo)) return;
        if (!Rig.ValidVFO(vfo)) return;

        switch (ch)
        {
            case ' ':
            case 'M':
            case 'S':
                // Toggle mute
                bool muted = !Rig.GetVFOAudio(vfo);
                Rig.SetVFOAudio(vfo, muted);
                Radios.ScreenReaderOutput.Speak(muted ? "Unmuted" : "Muted");
                e.Handled = true;
                break;
            case 'A':
                Rig.RXVFO = vfo;
                Radios.ScreenReaderOutput.Speak($"Slice {vfo} active");
                e.Handled = true;
                break;
            case 'T':
                if (Rig.CanTransmit)
                {
                    Rig.TXVFO = vfo;
                    Radios.ScreenReaderOutput.Speak($"Slice {vfo} transmit");
                }
                e.Handled = true;
                break;
            case 'X':
                // Transceive: set both RX and TX to this slice
                Rig.RXVFO = vfo;
                if (Rig.CanTransmit)
                    Rig.TXVFO = vfo;
                Radios.ScreenReaderOutput.Speak($"Slice {vfo} transceive");
                e.Handled = true;
                break;
            case 'L':
                Rig.SetVFOPan(vfo, FlexBase.MinPan);
                Radios.ScreenReaderOutput.Speak("Pan left");
                e.Handled = true;
                break;
            case 'C':
                Rig.SetVFOPan(vfo, (FlexBase.MaxPan - FlexBase.MinPan) / 2);
                Radios.ScreenReaderOutput.Speak("Pan center");
                e.Handled = true;
                break;
            case 'R':
                Rig.SetVFOPan(vfo, FlexBase.MaxPan);
                Radios.ScreenReaderOutput.Speak("Pan right");
                e.Handled = true;
                break;
            default:
                switch (key)
                {
                    case Key.Up:
                        AdjustGain(vfo, FlexBase.GainIncrement);
                        e.Handled = true;
                        break;
                    case Key.Down:
                        AdjustGain(vfo, -FlexBase.GainIncrement);
                        e.Handled = true;
                        break;
                    case Key.PageUp:
                        AdjustPan(vfo, FlexBase.PanIncrement);
                        e.Handled = true;
                        break;
                    case Key.PageDown:
                        AdjustPan(vfo, -FlexBase.PanIncrement);
                        e.Handled = true;
                        break;
                }

                if (ch >= '0' && ch <= '9')
                {
                    // Copy this VFO's settings to another
                    int target = ch - '0';
                    if (target != vfo && Rig.ValidVFO(target))
                    {
                        Rig.CopyVFO(vfo, target);
                        e.Handled = true;
                    }
                }
                else if (ch == '.')
                {
                    // Allocate/free slice — toggle
                    e.Handled = true;
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
        Radios.ScreenReaderOutput.Speak($"Volume {newVal}");
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
    /// VOX field handler — toggle VOX.
    /// </summary>
    public void AdjustVox(FrequencyDisplay.DisplayField field, KeyEventArgs e)
    {
        if (Rig == null) return;
        var key = RawKey(e);

        if (key == Key.Space || key == Key.Up || key == Key.Down)
        {
            // Toggle VOX - not exposed as simple property, use speech feedback
            Radios.ScreenReaderOutput.Speak("VOX toggle");
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
