using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// The parametric equaliser's editing model (Sprint 49 Track I, #1240):
    /// a cursor on a band and a field, and the operations the editor window and
    /// the noise layer's equaliser keys both use, so the equaliser is
    /// configurable entirely by keyboard and screen reader, with a window or
    /// without one (#488, #489). Every edit writes the WHOLE setting through
    /// the side's writer; every result is a sentence (DRAFT PROSE keys).
    /// Windowless and testable.
    /// </summary>
    public sealed class ParametricEqEditorModel
    {
        /// <summary>A band's fields, in the order Tab walks them.</summary>
        public enum Field { Kind, Frequency, Gain, Q }

        private static readonly Field[] FieldOrder = { Field.Kind, Field.Frequency, Field.Gain, Field.Q };

        private readonly Func<ParametricEqSetup> _read;
        private readonly Func<ParametricEqSetup, TxChangeResult?> _write;

        public ParametricEqEditorModel(DenoiseDirection side, Func<ParametricEqSetup> read, Func<ParametricEqSetup, TxChangeResult?> write)
        {
            Side = side;
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _write = write ?? throw new ArgumentNullException(nameof(write));
        }

        /// <summary>A model on its own setting, writing nowhere: for a detached editor.</summary>
        public static ParametricEqEditorModel Detached(DenoiseDirection side)
        {
            var held = new ParametricEqSetup();
            return new ParametricEqEditorModel(side, () => held.Clone(), s => { held = s.Clone(); return TxChangeResult.Now; });
        }

        public DenoiseDirection Side { get; }

        /// <summary>The band the cursor is on; -1 when there are none.</summary>
        public int Band { get; private set; }

        public Field Current { get; private set; } = Field.Kind;

        /// <summary>The setting now: a copy.</summary>
        public ParametricEqSetup Setting => _read();

        public int BandCount => _read().Bands.Count;

        /// <summary>The timing the last write answered with (transmit: held for the unkey or not).</summary>
        public TxChangeResult? LastTiming { get; private set; }

        // ── naming ──

        public static string KindName(string kind) => Lexicon.Get(kind switch
        {
            ParametricEqKinds.Peaking => "audio.eq.kind.peaking",
            ParametricEqKinds.LowShelf => "audio.eq.kind.low_shelf",
            ParametricEqKinds.HighShelf => "audio.eq.kind.high_shelf",
            ParametricEqKinds.HighPass => "audio.eq.kind.high_pass",
            ParametricEqKinds.LowPass => "audio.eq.kind.low_pass",
            ParametricEqKinds.Notch => "audio.eq.kind.notch",
            _ => "audio.eq.kind.unknown",
        });

        public static string FieldName(Field f) => Lexicon.Get(f switch
        {
            Field.Kind => "audio.eq.field.kind",
            Field.Frequency => "audio.eq.field.frequency",
            Field.Gain => "audio.eq.field.gain",
            _ => "audio.eq.field.q",
        });

        public static string FrequencyText(double hz) =>
            hz >= 1000 ? Lexicon.Get("audio.eq.kilohertz", ("value", (hz / 1000).ToString("0.##", CultureInfo.InvariantCulture)))
                : Lexicon.Get("audio.eq.hertz", ("value", Math.Round(hz).ToString("0", CultureInfo.InvariantCulture)));

        public static string GainText(double db) => Lexicon.Get("audio.eq.decibels", ("value", db.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture)));

        public static string QText(double q) => q.ToString("0.##", CultureInfo.InvariantCulture);

        public static string FieldValue(ParametricEqBandSetup b, Field f) => f switch
        {
            Field.Kind => KindName(b.Kind),
            Field.Frequency => FrequencyText(b.FrequencyHz),
            Field.Gain => ParametricEqKinds.UsesGain(b.Kind) ? GainText(b.GainDb) : Lexicon.Get("audio.eq.gain_not_used"),
            _ => QText(b.Q),
        };

        /// <summary>A band as one line, assembled from its own fields: what the band list reads and what the layer says.</summary>
        public static string BandRow(ParametricEqBandSetup b, int index) =>
            ParametricEqKinds.UsesGain(b.Kind)
                ? Lexicon.Get("audio.eq.band_row", ("number", index + 1), ("kind", KindName(b.Kind)), ("frequency", FrequencyText(b.FrequencyHz)),
                    ("gain", GainText(b.GainDb)), ("q", QText(b.Q)))
                : Lexicon.Get("audio.eq.band_row_no_gain", ("number", index + 1), ("kind", KindName(b.Kind)), ("frequency", FrequencyText(b.FrequencyHz)),
                    ("q", QText(b.Q)));

        // ── steps ──

        public const double MinFrequencyHz = 20, MaxFrequencyHz = 20000, MinGainDb = -24, MaxGainDb = 24;

        /// <summary>A sixth of an octave a step: fine enough to place a band by ear, coarse enough to cross the voice band in a few dozen presses.</summary>
        public static double StepFrequency(double hz, int direction) =>
            Math.Clamp(Math.Round(hz * Math.Pow(2, Math.Sign(direction) / 6.0)), MinFrequencyHz, MaxFrequencyHz);

        public static double StepGain(double db, int direction) => Math.Clamp(Math.Round((db + 0.5 * Math.Sign(direction)) * 2) / 2, MinGainDb, MaxGainDb);

        public static double StepQ(double q, int direction) =>
            Math.Clamp(Math.Round(q * Math.Pow(2, Math.Sign(direction) / 4.0), 2), JJPortaudio.BiquadCoefficients.MinQ, JJPortaudio.BiquadCoefficients.MaxQ);

        // ── operations: each returns what to say ──

        private string Write(ParametricEqSetup s, string said)
        {
            var timing = _write(s);
            LastTiming = timing;
            if (timing == null) return Lexicon.Get("audio.eq.no_radio");
            if (timing.Timing == TxChangeTiming.Refused) return Lexicon.Get("audio.eq.refused");
            return said;
        }

        private ParametricEqBandSetup? CurrentBand(ParametricEqSetup s) =>
            Band >= 0 && Band < s.Bands.Count ? s.Bands[Band] : null;

        /// <summary>Move to the next or previous band; at an end it stays and says so.</summary>
        public string SelectBand(int delta)
        {
            var s = _read();
            if (s.Bands.Count == 0) { Band = -1; return Lexicon.Get("audio.eq.no_bands"); }
            int next = Math.Clamp(Band + Math.Sign(delta), 0, s.Bands.Count - 1);
            bool moved = next != Band;
            Band = next;
            return moved ? BandRow(s.Bands[Band], Band) : Lexicon.Get(delta > 0 ? "audio.eq.last_band" : "audio.eq.first_band", ("band", BandRow(s.Bands[Band], Band)));
        }

        /// <summary>Put the cursor on a band by its index (the editor's list).</summary>
        public void Select(int index)
        {
            int count = BandCount;
            Band = count == 0 ? -1 : Math.Clamp(index, 0, count - 1);
        }

        /// <summary>Move to the next or previous field of the band, wrapping, and say it with its value.</summary>
        public string SelectField(int delta)
        {
            int at = Array.IndexOf(FieldOrder, Current);
            Current = FieldOrder[((at + Math.Sign(delta == 0 ? 1 : delta)) % FieldOrder.Length + FieldOrder.Length) % FieldOrder.Length];
            return SayField();
        }

        public string SayField()
        {
            var b = CurrentBand(_read());
            if (b == null) return Lexicon.Get("audio.eq.no_bands");
            return Lexicon.Get("audio.eq.field_value", ("field", FieldName(Current)), ("value", FieldValue(b, Current)));
        }

        public string SayBand()
        {
            var s = _read();
            var b = CurrentBand(s);
            return b == null ? Lexicon.Get("audio.eq.no_bands") : BandRow(b, Band);
        }

        /// <summary>Step the field the cursor is on up or down. The kind cycles through the shapes.</summary>
        public string Adjust(int direction)
        {
            var s = _read();
            var b = CurrentBand(s);
            if (b == null) return Lexicon.Get("audio.eq.no_bands");
            switch (Current)
            {
                case Field.Kind:
                    var kinds = ParametricEqKinds.All;
                    int k = kinds.ToList().IndexOf(b.Kind);
                    b.Kind = kinds[((k < 0 ? 0 : k) + Math.Sign(direction) + kinds.Count) % kinds.Count];
                    break;
                case Field.Frequency: b.FrequencyHz = (float)StepFrequency(b.FrequencyHz, direction); break;
                case Field.Gain:
                    if (!ParametricEqKinds.UsesGain(b.Kind)) return Lexicon.Get("audio.eq.gain_not_used");
                    b.GainDb = (float)StepGain(b.GainDb, direction); break;
                default: b.Q = (float)StepQ(b.Q, direction); break;
            }
            s.PresetId = "";
            return Write(s, Lexicon.Get("audio.eq.field_value", ("field", FieldName(Current)), ("value", FieldValue(b, Current))));
        }

        /// <summary>Set one field of the current band to a value (the editor's fields).</summary>
        public string Set(Field field, double value)
        {
            var s = _read();
            var b = CurrentBand(s);
            if (b == null) return Lexicon.Get("audio.eq.no_bands");
            switch (field)
            {
                case Field.Frequency: b.FrequencyHz = (float)Math.Clamp(value, MinFrequencyHz, MaxFrequencyHz); break;
                case Field.Gain: b.GainDb = (float)Math.Clamp(value, MinGainDb, MaxGainDb); break;
                case Field.Q: b.Q = (float)Math.Clamp(value, JJPortaudio.BiquadCoefficients.MinQ, JJPortaudio.BiquadCoefficients.MaxQ); break;
                default:
                    int i = (int)Math.Round(value);
                    if (i >= 0 && i < ParametricEqKinds.All.Count) b.Kind = ParametricEqKinds.All[i];
                    break;
            }
            s.PresetId = "";
            return Write(s, Lexicon.Get("audio.eq.field_value", ("field", FieldName(field)), ("value", FieldValue(b, field))));
        }

        /// <summary>Add a band after the current one: a bell at 1 kHz, 0 dB, Q 1, which changes nothing until it is moved.</summary>
        public string AddBand()
        {
            var s = _read();
            if (s.Bands.Count >= JJPortaudio.ParametricEq.MaxBands) return Lexicon.Get("audio.eq.band_limit", ("count", JJPortaudio.ParametricEq.MaxBands));
            var band = new ParametricEqBandSetup { Kind = ParametricEqKinds.Peaking, FrequencyHz = 1000, GainDb = 0, Q = 1 };
            int at = Band < 0 ? s.Bands.Count : Band + 1;
            s.Bands.Insert(at, band);
            s.PresetId = "";
            string said = Write(s, Lexicon.Get("audio.eq.band_added", ("band", BandRow(band, at))));
            if (LastTiming != null && LastTiming.Timing != TxChangeTiming.Refused) { Band = at; Current = Field.Kind; }
            return said;
        }

        /// <summary>Remove the current band.</summary>
        public string RemoveBand()
        {
            var s = _read();
            var b = CurrentBand(s);
            if (b == null) return Lexicon.Get("audio.eq.no_bands");
            string row = BandRow(b, Band);
            s.Bands.RemoveAt(Band);
            s.PresetId = "";
            string said = Write(s, Lexicon.Get("audio.eq.band_removed", ("band", row)));
            if (LastTiming != null && LastTiming.Timing != TxChangeTiming.Refused)
                Band = s.Bands.Count == 0 ? -1 : Math.Min(Band, s.Bands.Count - 1);
            return said;
        }

        /// <summary>The output gain, half a decibel a step.</summary>
        public string AdjustOutputGain(int direction)
        {
            var s = _read();
            s.OutputGainDb = (float)StepGain(s.OutputGainDb, direction);
            return Write(s, Lexicon.Get("audio.eq.output_gain", ("value", GainText(s.OutputGainDb))));
        }

        public string SetOutputGain(double db)
        {
            var s = _read();
            s.OutputGainDb = (float)Math.Clamp(db, MinGainDb, MaxGainDb);
            return Write(s, Lexicon.Get("audio.eq.output_gain", ("value", GainText(s.OutputGainDb))));
        }

        /// <summary>Put a whole setting back (the editor's Cancel), unchanged in on or off.</summary>
        public void Restore(ParametricEqSetup snapshot) => _write(snapshot.Clone());
    }

    /// <summary>
    /// The parametric equaliser's editor (Sprint 49 Track I, #1240): the side
    /// it edits, on or off, a preset, the band list, each band's fields on
    /// <see cref="ValueFieldControl"/> (no sliders, #490), add and remove,
    /// the output gain. Close keeps; Cancel and Escape put back the setting it
    /// opened with. Every word is a DRAFT PROSE key for Codex. Its logic is the
    /// windowless <see cref="ParametricEqEditorModel"/>; the window itself is
    /// the harness walk's to press.
    /// </summary>
    public sealed class ParametricEqEditorDialog : JJFlexDialog
    {
        private readonly ParametricEqEditorModel _model;
        private readonly ParametricEqSetup _snapshot;
        private readonly Action<bool>? _setOn;
        private readonly Action<string>? _loadPreset;
        private readonly ListBox _bands = new() { MinHeight = 140 };
        private readonly ComboBox _kind = new();
        private readonly ValueFieldControl _frequency = new();
        private readonly ValueFieldControl _gain = new();
        private readonly ValueFieldControl _q = new();
        private readonly ValueFieldControl _output = new();
        private readonly CheckBox _on = new();
        private readonly ComboBox _preset = new();
        private readonly IReadOnlyList<ParametricEqPreset> _presets;
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private bool _filling;

        /// <summary>A detached editor (no radio, nothing applied): the dialog harness's road.</summary>
        public ParametricEqEditorDialog() : this(ParametricEqEditorModel.Detached(DenoiseDirection.Transmit), null, null) { }

        public ParametricEqEditorDialog(ParametricEqEditorModel model, Action<bool>? setOn, Action<string>? loadPreset)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _setOn = setOn;
            _loadPreset = loadPreset;
            _snapshot = model.Setting;
            _presets = ParametricEqPresets.For(model.Side == DenoiseDirection.Transmit);
            string side = Lexicon.Get(model.Side == DenoiseDirection.Transmit ? "audio.eq.side.transmit" : "audio.eq.side.receive");
            Title = Lexicon.Get("audio.eq.editor.title", ("side", side));
            Width = 640; Height = 620;
            ResizeMode = ResizeMode.CanResize;

            var root = new DockPanel { Margin = new Thickness(12) };
            var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            buttons.Children.Add(Button("audio.eq.editor.close", () => { DialogResult = true; }, isDefault: true));
            var cancel = Button("audio.eq.editor.cancel", () => { _model.Restore(_snapshot); DialogResult = false; });
            cancel.IsCancel = true;
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            var stack = new StackPanel();
            var sideText = new TextBlock { Text = Lexicon.Get("audio.eq.editor.side", ("side", side)), Margin = new Thickness(0, 0, 0, 6) };
            AutomationProperties.SetName(sideText, sideText.Text);
            stack.Children.Add(sideText);

            _on.Content = Lexicon.Get("audio.eq.editor.on");
            AutomationProperties.SetName(_on, (string)_on.Content);
            _on.IsChecked = _snapshot.Enabled;
            _on.Checked += (_, _) => { if (!_filling) { _setOn?.Invoke(true); Refresh(); } };
            _on.Unchecked += (_, _) => { if (!_filling) { _setOn?.Invoke(false); Refresh(); } };
            stack.Children.Add(_on);

            foreach (var p in _presets) _preset.Items.Add(Lexicon.Get(p.NameKey));
            AutomationProperties.SetName(_preset, Lexicon.Get("audio.eq.editor.preset"));
            stack.Children.Add(Labelled("audio.eq.editor.preset", _preset));
            stack.Children.Add(Button("audio.eq.editor.load_preset", () =>
            {
                if (_preset.SelectedIndex < 0 || _preset.SelectedIndex >= _presets.Count) return;
                _loadPreset?.Invoke(_presets[_preset.SelectedIndex].Id);
                _model.Select(0);
                Refresh();
            }));

            AutomationProperties.SetName(_bands, Lexicon.Get("audio.eq.editor.bands"));
            _bands.SelectionChanged += (_, _) => { if (!_filling) { _model.Select(_bands.SelectedIndex); FillFields(); } };
            stack.Children.Add(Labelled("audio.eq.editor.bands", _bands));

            var bandButtons = new WrapPanel();
            bandButtons.Children.Add(Button("audio.eq.editor.add_band", () => Say(_model.AddBand())));
            bandButtons.Children.Add(Button("audio.eq.editor.remove_band", () => Say(_model.RemoveBand())));
            stack.Children.Add(bandButtons);

            foreach (var k in ParametricEqKinds.All) _kind.Items.Add(ParametricEqEditorModel.KindName(k));
            AutomationProperties.SetName(_kind, ParametricEqEditorModel.FieldName(ParametricEqEditorModel.Field.Kind));
            _kind.SelectionChanged += (_, _) => { if (!_filling && _kind.SelectedIndex >= 0) Say(_model.Set(ParametricEqEditorModel.Field.Kind, _kind.SelectedIndex)); };
            stack.Children.Add(Labelled("audio.eq.field.kind", _kind));

            _frequency.Setup(ParametricEqEditorModel.FieldName(ParametricEqEditorModel.Field.Frequency), (int)ParametricEqEditorModel.MinFrequencyHz,
                (int)ParametricEqEditorModel.MaxFrequencyHz, 50, 1000, 0, Lexicon.Get("audio.unit.hertz"));
            _frequency.FineStep = 1;
            _frequency.ValueChanged += (_, v) => { if (!_filling) Say(_model.Set(ParametricEqEditorModel.Field.Frequency, v)); };
            stack.Children.Add(_frequency);

            _gain.Setup(ParametricEqEditorModel.FieldName(ParametricEqEditorModel.Field.Gain), -240, 240, 5, 0, 1, Lexicon.Get("audio.unit.decibels"));
            _gain.ValueChanged += (_, v) => { if (!_filling) Say(_model.Set(ParametricEqEditorModel.Field.Gain, v / 10.0)); };
            stack.Children.Add(_gain);

            _q.Setup(ParametricEqEditorModel.FieldName(ParametricEqEditorModel.Field.Q), 10, 3000, 10, 100, 2);
            _q.ValueChanged += (_, v) => { if (!_filling) Say(_model.Set(ParametricEqEditorModel.Field.Q, v / 100.0)); };
            stack.Children.Add(_q);

            _output.Setup(Lexicon.Get("audio.eq.editor.output_gain"), -240, 240, 5, (int)Math.Round(_snapshot.OutputGainDb * 10), 1, Lexicon.Get("audio.unit.decibels"));
            _output.ValueChanged += (_, v) => { if (!_filling) Say(_model.SetOutputGain(v / 10.0)); };
            stack.Children.Add(_output);

            AutomationProperties.SetName(_status, Lexicon.Get("audio.eq.editor.status"));
            stack.Children.Add(_status);

            root.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
            _model.Select(0);
            Refresh();
        }

        private void Say(string sentence)
        {
            _status.Text = sentence;
            Refresh();
        }

        /// <summary>Rebuild the band list from the model, keeping the cursor.</summary>
        private void Refresh()
        {
            _filling = true;
            try
            {
                var s = _model.Setting;
                _on.IsChecked = s.Enabled;
                _bands.Items.Clear();
                for (int i = 0; i < s.Bands.Count; i++)
                {
                    string row = ParametricEqEditorModel.BandRow(s.Bands[i], i);
                    var item = new ListBoxItem { Content = row };
                    AutomationProperties.SetName(item, row);
                    _bands.Items.Add(item);
                }
                _bands.SelectedIndex = _model.Band;
            }
            finally { _filling = false; }
            FillFields();
        }

        private void FillFields()
        {
            _filling = true;
            try
            {
                var s = _model.Setting;
                bool any = _model.Band >= 0 && _model.Band < s.Bands.Count;
                foreach (UIElement e in new UIElement[] { _kind, _frequency, _gain, _q }) e.IsEnabled = any;
                if (!any) return;
                var b = s.Bands[_model.Band];
                _kind.SelectedIndex = Math.Max(0, ParametricEqKinds.All.ToList().IndexOf(b.Kind));
                _frequency.Value = (int)Math.Round(b.FrequencyHz);
                _gain.Value = (int)Math.Round(b.GainDb * 10);
                _gain.IsEnabled = ParametricEqKinds.UsesGain(b.Kind);
                _q.Value = (int)Math.Round(b.Q * 100);
                _output.Value = (int)Math.Round(s.OutputGainDb * 10);
            }
            finally { _filling = false; }
        }

        private static FrameworkElement Labelled(string key, Control control)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            var label = new Label { Content = Lexicon.Get(key), Target = control, Padding = new Thickness(0) };
            AutomationProperties.SetName(control, Lexicon.Get(key));
            panel.Children.Add(label);
            panel.Children.Add(control);
            return panel;
        }

        private static Button Button(string key, Action click, bool isDefault = false)
        {
            string text = Lexicon.Get(key);
            var b = new Button { Content = text, MinWidth = 110, Height = 28, Margin = new Thickness(0, 0, 8, 6), IsDefault = isDefault };
            AutomationProperties.SetName(b, text);
            b.Click += (_, _) => click();
            return b;
        }
    }
}
