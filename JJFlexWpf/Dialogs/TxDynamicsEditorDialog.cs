using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// The transmit compressor and limiter's editor (Sprint 49 Track CL,
    /// #1278): every parameter of both stages, reachable and readable by
    /// keyboard, each on a <see cref="ValueFieldControl"/> (no sliders, #490).
    /// Sections are real groups, moved between with F6 and Shift+F6. Close
    /// keeps; Cancel and Escape put back the setting it opened with. Every
    /// word is a DRAFT PROSE key for Codex. Its logic is the windowless
    /// <see cref="TxDynamicsEditorModel"/> and the noise controller's own
    /// rule book; the window itself is the harness walk's to press.
    /// </summary>
    public sealed class TxDynamicsEditorDialog : JJFlexDialog
    {
        private readonly TxDynamicsEditorModel _model;
        private readonly NoiseController? _noise;
        private readonly string _source;
        private readonly StackPanel _sections = new();
        private readonly CheckBox _compressorOn = new(), _limiterOn = new(), _handoff = new();
        private readonly ComboBox _preset = new(), _mode = new();
        private readonly List<(ValueFieldControl Field, DynamicsLever Lever)> _fields = new();
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Focusable = true };
        private bool _filling;
        private bool _kept;

        /// <summary>
        /// The window is closing: unless Close was pressed, put back everything
        /// the editor opened with (both stages and the speech processor
        /// setting), after withdrawing what it asked for while keyed and is
        /// still waiting (repair 2, B5). True when something was withdrawn or
        /// put back.
        /// </summary>
        internal static bool Dismissed(TxDynamicsEditorModel model, bool kept)
        {
            if (kept) { model.Kept(); return false; }
            return model.Restore();
        }

        /// <summary>A detached editor (no radio, nothing applied): the dialog harness's road.</summary>
        public TxDynamicsEditorDialog() : this(TxDynamicsEditorModel.Detached(), null, "detached") { }

        public TxDynamicsEditorDialog(TxDynamicsEditorModel model, NoiseController? noise, string source)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _noise = noise;
            _source = source ?? "";
            Title = Lexicon.Get("audio.dynamics.editor.title");
            Width = 660; Height = 680;
            ResizeMode = ResizeMode.CanResize;

            var root = new DockPanel { Margin = new Thickness(12) };
            var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            // Close KEEPS. Every other way out puts back the setting the editor
            // opened with: Cancel, Escape (the base dialog closes on it in its
            // own preview handler, before anything here sees the key), the
            // title bar's close and Alt+F4. So the put-back hangs on the
            // window closing, never on one button (repair 1, B5).
            buttons.Children.Add(Button("audio.dynamics.editor.close", () => { _kept = true; DialogResult = true; }, isDefault: true));
            var cancel = Button("audio.dynamics.editor.cancel", () => { DialogResult = false; });
            cancel.IsCancel = true;
            buttons.Children.Add(cancel);
            Closing += (_, _) => Dismissed(_model, _kept);
            root.Children.Add(buttons);

            // ── The compressor ──
            var compressor = Section("audio.dynamics.editor.section_compressor");
            Toggle(_compressorOn, "audio.dynamics.editor.compressor_on", "audio.dynamics_help.compressor_on",
                on => _noise?.SetCompressorOn(on, _source), compressor);
            foreach (var p in NoiseController.CompressorPresets) _preset.Items.Add(NoiseController.CompressorPresetName(p));
            compressor.Children.Add(Labelled("audio.dynamics.editor.preset", "audio.dynamics_help.preset", _preset));
            compressor.Children.Add(Button("audio.dynamics.editor.load_preset", () =>
            {
                int i = _preset.SelectedIndex;
                if (i < 0 || i >= NoiseController.CompressorPresets.Count) return;
                _noise?.LoadCompressorPreset(NoiseController.CompressorPresets[i].Id, _source);
                Refresh();
            }));
            _mode.Items.Add(Lexicon.Get("audio.compressor.mode_name_single"));
            _mode.Items.Add(Lexicon.Get("audio.compressor.mode_name_split"));
            _mode.SelectionChanged += (_, _) => { if (!_filling && _mode.SelectedIndex >= 0) { _noise?.SetCompressorSplitBand(_mode.SelectedIndex == 1, _source); Refresh(); } };
            compressor.Children.Add(Labelled("audio.dynamics.editor.mode", "audio.dynamics_help.mode", _mode));
            foreach (var lever in NoiseController.CompressorLevers.Where(l => !l.SplitBandOnly)) compressor.Children.Add(Field(lever));
            // SAID, as Home's button and the menu's item say it, and shown on the reading line.
            compressor.Children.Add(Button("audio.dynamics.editor.say_compressor", () => { _noise?.SayCompressorNow(); Status(_noise?.CompressorNowSentence()); }));
            Toggle(_handoff, "audio.dynamics.editor.handoff", "audio.dynamics_help.handoff",
                on => _noise?.SetProcessorHandoffSetting(on, _source), compressor);

            // ── The split band's crossovers and its three bands ──
            var split = Section("audio.dynamics.editor.section_split");
            foreach (var lever in NoiseController.CompressorLevers.Where(l => l.SplitBandOnly && !l.Key.Contains('.'))) split.Children.Add(Field(lever));
            foreach (var (band, headerKey) in new[]
                     {
                         ("low", "audio.dynamics.editor.section_low"),
                         ("mid", "audio.dynamics.editor.section_mid"),
                         ("high", "audio.dynamics.editor.section_high"),
                     })
            {
                var section = Section(headerKey);
                foreach (var lever in NoiseController.CompressorLevers.Where(l => l.Key.StartsWith(band + ".", StringComparison.Ordinal)))
                    section.Children.Add(Field(lever));
            }

            // ── The limiter ──
            var limiter = Section("audio.dynamics.editor.section_limiter");
            Toggle(_limiterOn, "audio.dynamics.editor.limiter_on", "audio.dynamics_help.limiter_on",
                on => _noise?.SetLimiterOn(on, _source), limiter);
            foreach (var lever in NoiseController.LimiterLevers) limiter.Children.Add(Field(lever));
            limiter.Children.Add(Button("audio.dynamics.editor.say_limiter", () => { _noise?.SayLimiterNow(); Status(_noise?.LimiterNowSentence()); }));

            AutomationProperties.SetName(_status, Lexicon.Get("audio.dynamics.editor.status"));
            var statusGroup = Section("audio.dynamics.editor.section_status");
            statusGroup.Children.Add(_status);

            root.Children.Add(new ScrollViewer { Content = _sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
            PreviewKeyDown += OnPreviewKeyDown;
            Refresh();
        }

        // F6 and Shift+F6 between the sections (the Windows convention; a
        // dialog runs in focus mode, where a screen reader's heading keys type
        // letters).
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F6) return;
            var modifiers = Keyboard.Modifiers;
            if (modifiers != ModifierKeys.None && modifiers != ModifierKeys.Shift) return;
            SectionNavigator.Move(_sections, forward: modifiers == ModifierKeys.None);
            e.Handled = true;
        }

        private StackPanel Section(string headerKey)
        {
            var panel = new StackPanel { Margin = new Thickness(4) };
            var group = new GroupBox { Header = Lexicon.Get(headerKey), Content = panel, Margin = new Thickness(0, 0, 0, 8) };
            AutomationProperties.SetName(group, (string)group.Header);
            _sections.Children.Add(group);
            return panel;
        }

        private void Toggle(CheckBox box, string labelKey, string helpKey, Action<bool> set, Panel into)
        {
            box.Content = Lexicon.Get(labelKey);
            AutomationProperties.SetName(box, (string)box.Content);
            JJFlexHelp.SetText(box, Lexicon.Get(helpKey));
            box.Checked += (_, _) => { if (!_filling) { set(true); Refresh(); } };
            box.Unchecked += (_, _) => { if (!_filling) { set(false); Refresh(); } };
            into.Children.Add(box);
        }

        private ValueFieldControl Field(DynamicsLever lever)
        {
            var field = new ValueFieldControl();
            var (min, max, step, fine) = TxDynamicsEditorModel.FieldRange(lever);
            field.Setup(Lexicon.Get(lever.LabelKey), min, max, step, min, lever.Decimals, lever.UnitKey == null ? "" : Lexicon.Get(lever.UnitKey));
            if (fine > 0) field.FineStep = fine;
            JJFlexHelp.SetText(field, Lexicon.Get(lever.HelpKey));
            field.ValueChanged += (_, v) => { if (!_filling) _model.Set(lever, v); };
            _fields.Add((field, lever));
            return field;
        }

        private void Status(string? sentence)
        {
            _status.Text = sentence ?? "";
            AutomationProperties.SetName(_status, _status.Text.Length > 0 ? _status.Text : Lexicon.Get("audio.dynamics.editor.status"));
        }

        /// <summary>Fill every control from the setting now.</summary>
        private void Refresh()
        {
            _filling = true;
            try
            {
                var (c, l) = _model.Setting;
                _compressorOn.IsChecked = c.Enabled;
                _limiterOn.IsChecked = l.Enabled;
                _handoff.IsChecked = _noise?.ProcessorHandoffSetting ?? AudioOutputConfig.RadioProcessorOffWithCompressorInForce;
                _mode.SelectedIndex = TxCompressorModes.IsSplit(c.CompressorMode) ? 1 : 0;
                int preset = -1;
                for (int i = 0; i < NoiseController.CompressorPresets.Count; i++)
                    if (string.Equals(NoiseController.CompressorPresets[i].Id, c.PresetId, StringComparison.OrdinalIgnoreCase)) preset = i;
                _preset.SelectedIndex = preset;
                foreach (var (field, lever) in _fields) field.Value = TxDynamicsEditorModel.ToField(lever, TxDynamicsEditorModel.ValueOf(lever, c, l));
            }
            finally { _filling = false; }
        }

        private static FrameworkElement Labelled(string key, string helpKey, Control control)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            var label = new Label { Content = Lexicon.Get(key), Target = control, Padding = new Thickness(0) };
            AutomationProperties.SetName(control, Lexicon.Get(key));
            JJFlexHelp.SetText(control, Lexicon.Get(helpKey));
            panel.Children.Add(label);
            panel.Children.Add(control);
            return panel;
        }

        private static Button Button(string key, Action click, bool isDefault = false)
        {
            string text = Lexicon.Get(key);
            var b = new Button { Content = text, MinWidth = 110, Height = 28, Margin = new Thickness(0, 4, 8, 6), IsDefault = isDefault };
            AutomationProperties.SetName(b, text);
            b.Click += (_, _) => click();
            return b;
        }
    }
}
