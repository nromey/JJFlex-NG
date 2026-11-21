using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace JJBrailleTrial.Model;

/// <summary>
/// What kind of field a Home field is, from the braille Home design section 3.5.
/// The letter is what the view string carries.
/// </summary>
public enum FieldKind
{
    Toggle,
    Value,
    Choice,
    Readout,
    Action,
}

/// <summary>Which braille view the reader side is asked to show (design section 3.1).</summary>
public enum BrailleView
{
    Field,
    Home,
    Off,
}

/// <summary>One field as the view string publishes it: ordinal, kind and braille text.</summary>
public sealed record TrialField(int Ordinal, FieldKind Kind, string Label, string Text, int SlotWidth);

/// <summary>
/// The trial's Home line, with no window. It owns the five fields, the caret, the
/// view string (design section 4.3), what a route does (section 3.5) and the
/// tuning arithmetic. Everything a test needs to reach is here; the window only
/// shows it and forwards keys and messages.
/// </summary>
public sealed class TrialLine
{
    public const int FrequencyOrdinal = 1;
    public const int ModeOrdinal = 2;
    public const int MeterOrdinal = 3;
    public const int VoxOrdinal = 4;
    public const int TuningOrdinal = 5;

    public const string FormatTag = "JJFV1";

    /// <summary>Lowest and highest frequency the trial allows. Both keep the
    /// frequency at ten characters, so no field ever moves on the screen.</summary>
    public const long MinHz = 10_000_000;
    public const long MaxHz = 54_000_000;

    /// <summary>What Up and Down step by in modern tuning. Deliberately not 100 Hz,
    /// so the switch to classic tuning is audible in the step.</summary>
    public const long ModernStepHz = 1_000;

    // Slot widths on the screen, fixed so the caret's field never depends on a
    // value. "VOX off" is 7, "Tuning classic" is 14.
    private static readonly int[] SlotWidths = { 0, 10, 3, 2, 7, 14 };

    private static readonly int[] MeterWalk = { 5, 6, 7, 8, 9, 8, 7, 6, 5, 4, 3, 4 };

    private string _lastBody = "";
    private int _walkIndex;
    private int _walkMeterIndex;

    public long FrequencyHz { get; private set; } = 14_225_000;
    public string Mode { get; } = "USB";
    public int SUnits { get; private set; } = 7;
    public bool VoxOn { get; private set; }
    public bool ClassicTuning { get; private set; }

    /// <summary>Changes only when the field list, order or label style changes. Constant in the trial.</summary>
    public int Layout { get; } = 1;

    public BrailleView View { get; private set; } = BrailleView.Home;

    /// <summary>Whether clickable cells are to be marked (opt-in, off by default).</summary>
    public bool MarkClickable { get; } = false;

    public int CaretOrdinal { get; private set; } = FrequencyOrdinal;

    /// <summary>Character within the caret field's text, 0-based.</summary>
    public int CaretChar { get; private set; }

    /// <summary>Goes up whenever the published view string changes in any part.</summary>
    public int Revision { get; private set; }

    public IReadOnlyList<TrialField> Fields => new[]
    {
        new TrialField(FrequencyOrdinal, FieldKind.Value, "frequency", FormatFrequency(FrequencyHz), SlotWidths[FrequencyOrdinal]),
        new TrialField(ModeOrdinal, FieldKind.Choice, "mode", Mode, SlotWidths[ModeOrdinal]),
        new TrialField(MeterOrdinal, FieldKind.Readout, "S meter", "S" + SUnits.ToString(CultureInfo.InvariantCulture), SlotWidths[MeterOrdinal]),
        new TrialField(VoxOrdinal, FieldKind.Toggle, "VOX", VoxOn ? "VOX on" : "VOX off", SlotWidths[VoxOrdinal]),
        new TrialField(TuningOrdinal, FieldKind.Toggle, "tuning", ClassicTuning ? "Tuning classic" : "Tuning modern", SlotWidths[TuningOrdinal]),
    };

    public TrialField GetField(int ordinal)
    {
        foreach (var f in Fields)
        {
            if (f.Ordinal == ordinal)
            {
                return f;
            }
        }
        throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "No such field.");
    }

    public static bool IsKnownOrdinal(int ordinal) => ordinal >= FrequencyOrdinal && ordinal <= TuningOrdinal;

    public static char KindLetter(FieldKind kind) => kind switch
    {
        FieldKind.Toggle => 'T',
        FieldKind.Value => 'V',
        FieldKind.Choice => 'C',
        FieldKind.Readout => 'R',
        FieldKind.Action => 'A',
        _ => '?',
    };

    public static string ViewWord(BrailleView view) => view switch
    {
        BrailleView.Field => "field",
        BrailleView.Home => "home",
        _ => "off",
    };

    /// <summary>"14.225.000": megahertz, kilohertz, hertz, dot separated.</summary>
    public static string FormatFrequency(long hz)
    {
        long mhz = hz / 1_000_000;
        long khz = hz / 1_000 % 1_000;
        long h = hz % 1_000;
        return string.Create(CultureInfo.InvariantCulture, $"{mhz}.{khz:000}.{h:000}");
    }

    // ------------------------------------------------------------------ screen

    /// <summary>The line the text box shows: each field padded to its slot, one space between.</summary>
    public string DisplayText
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var f in Fields)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(f.Text.PadRight(f.SlotWidth));
            }
            return sb.ToString();
        }
    }

    /// <summary>Index in <see cref="DisplayText"/> where a field's slot starts.</summary>
    public static int FieldStart(int ordinal)
    {
        int pos = 0;
        for (int o = FrequencyOrdinal; o < ordinal; o++)
        {
            pos += SlotWidths[o] + 1;
        }
        return pos;
    }

    /// <summary>
    /// The field and character a text-box caret index is on. The space after a
    /// field belongs to the field on its left, as in design section 3.5, and a
    /// caret in a field's padding sits on that field's last character.
    /// </summary>
    public (int Ordinal, int Char) PositionToField(int caretIndex)
    {
        int ordinal = FrequencyOrdinal;
        for (int o = TuningOrdinal; o >= FrequencyOrdinal; o--)
        {
            if (caretIndex >= FieldStart(o))
            {
                ordinal = o;
                break;
            }
        }
        int ch = Math.Max(0, caretIndex - FieldStart(ordinal));
        int len = GetField(ordinal).Text.Length;
        if (ch >= len)
        {
            ch = len - 1;
        }
        return (ordinal, ch);
    }

    /// <summary>The text-box caret index for the program's caret.</summary>
    public int CaretIndex => FieldStart(CaretOrdinal) + CaretChar;

    /// <summary>Follow a caret the keyboard moved. Returns true if it moved.</summary>
    public bool SetCaretFromIndex(int caretIndex)
    {
        var (o, c) = PositionToField(caretIndex);
        if (o == CaretOrdinal && c == CaretChar)
        {
            return false;
        }
        CaretOrdinal = o;
        CaretChar = c;
        return true;
    }

    // ------------------------------------------------------------- view string

    /// <summary>
    /// The view string, design section 4.3: a header line, then one line per
    /// field; tab between parts, line feed between lines.
    ///
    /// Header: JJFV1, revision, layout, view, caret field ordinal, clickable flag,
    /// and a SEVENTH part the design does not have: the caret's character within
    /// its field. The brief asks the reader to keep its cursor at the program's
    /// caret, which the six-part header cannot say for a digit inside the
    /// frequency; see the README. A reader that knows only six parts ignores it.
    /// </summary>
    public string BuildViewString()
    {
        // Everything except the revision decides whether the revision moves,
        // so the revision never counts itself.
        string rest = HeaderAfterRevision() + FieldLines();
        if (!string.Equals(rest, _lastBody, StringComparison.Ordinal))
        {
            Revision++;
            _lastBody = rest;
        }
        return FormatTag + "\t" + Revision.ToString(CultureInfo.InvariantCulture) + "\t" + rest;
    }

    private string HeaderAfterRevision()
    {
        var inv = CultureInfo.InvariantCulture;
        return string.Join('\t',
            Layout.ToString(inv),
            ViewWord(View),
            CaretOrdinal.ToString(inv),
            MarkClickable ? "1" : "0",
            CaretChar.ToString(inv));
    }

    private string FieldLines()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        foreach (var f in Fields)
        {
            sb.Append('\n').Append(f.Ordinal.ToString(inv)).Append('\t').Append(KindLetter(f.Kind)).Append('\t').Append(f.Text);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ routes

    /// <summary>
    /// Do what a route does, design section 3.5, for a decision the decoder
    /// accepted. Returns the words to speak and a plain description for the trace.
    /// </summary>
    public RouteOutcome ApplyRoute(RouteDecision decision)
    {
        if (!decision.Accepted)
        {
            return new RouteOutcome(false, null, "dropped: " + decision.Reason);
        }
        var field = GetField(decision.Ordinal);
        switch (decision.Ordinal)
        {
            case FrequencyOrdinal:
                {
                    int ch = decision.CharPos ?? 0;
                    ch = SnapToDigit(ch);
                    CaretOrdinal = FrequencyOrdinal;
                    CaretChar = ch;
                    string words = "frequency, " + DescribeDigit(ch);
                    return new RouteOutcome(true, words, "caret to frequency character " + ch.ToString(CultureInfo.InvariantCulture));
                }
            case VoxOrdinal:
                VoxOn = !VoxOn;
                return new RouteOutcome(true, GetField(VoxOrdinal).Text, "toggled VOX, now " + (VoxOn ? "on" : "off") + "; caret not moved");
            case TuningOrdinal:
                ClassicTuning = !ClassicTuning;
                return new RouteOutcome(true, GetField(TuningOrdinal).Text, "toggled tuning, now " + (ClassicTuning ? "classic" : "modern") + "; caret not moved");
            default:
                CaretOrdinal = field.Ordinal;
                CaretChar = 0;
                return new RouteOutcome(true, field.Label + ", " + field.Text, "caret to " + field.Label);
        }
    }

    /// <summary>Space on the caret's field: a toggle flips, anything else says so.</summary>
    public RouteOutcome PressSpace()
    {
        if (CaretOrdinal == VoxOrdinal || CaretOrdinal == TuningOrdinal)
        {
            var decision = new RouteDecision(true, "", CaretOrdinal, null, FieldKind.Toggle);
            var outcome = ApplyRoute(decision);
            return outcome with { TraceText = "space: " + outcome.TraceText };
        }
        return new RouteOutcome(false, "Space toggles only VOX and tuning", "space: not a toggle, nothing done");
    }

    // ------------------------------------------------------------------ tuning

    /// <summary>Place value in hertz of a frequency character, or 0 for a dot or out of range.</summary>
    public long PlaceValue(int ch)
    {
        string text = FormatFrequency(FrequencyHz);
        if (ch < 0 || ch >= text.Length || !char.IsDigit(text[ch]))
        {
            return 0;
        }
        long place = 1;
        for (int i = text.Length - 1; i > ch; i--)
        {
            if (char.IsDigit(text[i]))
            {
                place *= 10;
            }
        }
        return place;
    }

    /// <summary>
    /// Up (+1) or Down (-1). Classic tuning steps by the place value of the
    /// digit under the caret and needs the caret on a frequency digit; modern
    /// tuning steps by <see cref="ModernStepHz"/> wherever the caret is.
    /// </summary>
    public TuneOutcome Tune(int direction)
    {
        long step;
        string how;
        if (ClassicTuning)
        {
            if (CaretOrdinal != FrequencyOrdinal || PlaceValue(CaretChar) == 0)
            {
                return new TuneOutcome(false, "Put the caret on a frequency digit to tune in classic mode",
                    "classic tune refused: caret on " + GetField(CaretOrdinal).Label + " character " + CaretChar.ToString(CultureInfo.InvariantCulture));
            }
            step = PlaceValue(CaretChar);
            how = "classic, step " + step.ToString(CultureInfo.InvariantCulture) + " Hz from the digit under the caret";
        }
        else
        {
            step = ModernStepHz;
            how = "modern, step " + step.ToString(CultureInfo.InvariantCulture) + " Hz";
        }
        long next = FrequencyHz + (direction >= 0 ? step : -step);
        if (next < MinHz || next > MaxHz)
        {
            return new TuneOutcome(false, "Limit", "tune refused at the limit: " + how);
        }
        long before = FrequencyHz;
        FrequencyHz = next;
        string text = FormatFrequency(FrequencyHz);
        return new TuneOutcome(true, text,
            (direction >= 0 ? "up" : "down") + ", " + how + ": " + FormatFrequency(before) + " to " + text);
    }

    /// <summary>One step of the simulated walk: 100 Hz, up for twenty steps then down for twenty, and a new meter reading.</summary>
    public string WalkStep()
    {
        int phase = _walkIndex % 40;
        long delta = phase < 20 ? 100 : -100;
        _walkIndex++;
        long next = Math.Clamp(FrequencyHz + delta, MinHz, MaxHz);
        FrequencyHz = next;
        _walkMeterIndex = (_walkMeterIndex + 1) % MeterWalk.Length;
        SUnits = MeterWalk[_walkMeterIndex];
        return FormatFrequency(FrequencyHz) + " S" + SUnits.ToString(CultureInfo.InvariantCulture);
    }

    public BrailleView SwitchView()
    {
        View = View == BrailleView.Home ? BrailleView.Field : BrailleView.Home;
        return View;
    }

    // ----------------------------------------------------------------- words

    /// <summary>Words for the caret's current place, used when the keyboard moves it.</summary>
    public string DescribeCaret(bool fieldChanged)
    {
        var f = GetField(CaretOrdinal);
        if (CaretOrdinal == FrequencyOrdinal)
        {
            return (fieldChanged ? "frequency, " : "") + DescribeDigit(CaretChar);
        }
        return f.Label + ", " + f.Text;
    }

    private int SnapToDigit(int ch)
    {
        string text = FormatFrequency(FrequencyHz);
        if (ch < 0 || ch >= text.Length)
        {
            return 0;
        }
        if (!char.IsDigit(text[ch]) && ch + 1 < text.Length)
        {
            return ch + 1;
        }
        return ch;
    }

    private string DescribeDigit(int ch)
    {
        string text = FormatFrequency(FrequencyHz);
        if (ch < 0 || ch >= text.Length || !char.IsDigit(text[ch]))
        {
            return "dot";
        }
        int n = 0;
        for (int i = 0; i <= ch; i++)
        {
            if (char.IsDigit(text[i]))
            {
                n++;
            }
        }
        return "digit " + n.ToString(CultureInfo.InvariantCulture) + ", " + PlaceName(PlaceValue(ch));
    }

    public static string PlaceName(long place) => place switch
    {
        10_000_000 => "10 megahertz",
        1_000_000 => "1 megahertz",
        100_000 => "100 kilohertz",
        10_000 => "10 kilohertz",
        1_000 => "1 kilohertz",
        100 => "100 hertz",
        10 => "10 hertz",
        1 => "1 hertz",
        _ => "",
    };
}

/// <summary>The decoder's verdict on a Route message.</summary>
/// <param name="CharPos">0-based character within the field, or null for "not known".</param>
public sealed record RouteDecision(bool Accepted, string Reason, int Ordinal, int? CharPos, FieldKind? Kind);

/// <summary>What a route did. <paramref name="Spoken"/> is null when nothing is said.</summary>
public sealed record RouteOutcome(bool Done, string? Spoken, string TraceText);

public sealed record TuneOutcome(bool Done, string Spoken, string TraceText);
