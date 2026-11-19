using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Settings, Audio: the "Noise reduction" group (Sprint 49 Track E, E10).
    /// The favourites checklist (every engine, both subjects, each with its
    /// availability marker), the favourites in bracket order with a typed
    /// position and Move up and Move down (Noel, 2026-11-07 21:16:
    /// arrangeable), and "Say delays when choosing an engine" (ruled,
    /// departure D3).
    /// </summary>
    /// <remarks>
    /// Edits the dialog's own audio config, which is the live one while a
    /// radio is connected and is saved on OK like the rest of the tab.
    /// </remarks>
    public partial class SettingsDialog
    {
        /// <summary>The noise controller, for engine names, markers and models; null builds the list from the catalog alone.</summary>
        /// <remarks>
        /// Assigned after the constructor has built the tabs, so assigning it
        /// refills the Measured delays list (follow-up round 5): until then the
        /// list held the no-controller line, and only a focus event inside the
        /// tab replaced it, so it read one thing until the guard was ticked and
        /// another after. The guard locks editing, never reading.
        /// </remarks>
        public NoiseController? Noise
        {
            get => _noise;
            set { _noise = value; FillMeasuredDelays(); }
        }
        private NoiseController? _noise;

        private StackPanel? _noiseFavouriteChecks;
        private ListBox? _noiseFavouriteOrder;
        private TextBox? _noiseFavouritePosition;
        private CheckBox? _noiseSayDelays;
        private CheckBox? _noiseSwitchSpeech, _noiseSwitchBeep, _noiseSwitchMute;
        private bool _noiseSettingsFilling;

        private void InitializeNoiseSettings()
        {
            if (AudioTabPanel == null) return;
            var group = new GroupBox { Header = Lexicon.Get("audio.noise_settings.group"), Margin = new Thickness(0, 4, 0, 8) };
            AutomationProperties.SetName(group, Lexicon.Get("audio.noise_settings.group"));
            var panel = new StackPanel { Margin = new Thickness(4) };
            group.Content = panel;

            panel.Children.Add(new TextBlock { Text = Lexicon.Get("audio.noise_settings.favourites_heading"), Margin = new Thickness(0, 0, 0, 2) });
            _noiseFavouriteChecks = new StackPanel();
            panel.Children.Add(_noiseFavouriteChecks);

            var orderLabel = new Label { Content = Lexicon.Get("audio.noise_settings.order_label") };
            panel.Children.Add(orderLabel);
            _noiseFavouriteOrder = new ListBox { Height = 110 };
            orderLabel.Target = _noiseFavouriteOrder;
            AutomationProperties.SetName(_noiseFavouriteOrder, Lexicon.Get("audio.noise_settings.order"));
            JJFlexHelp.SetText(_noiseFavouriteOrder, Lexicon.Get("audio.noise_help.settings.order"));
            _noiseFavouriteOrder.SelectionChanged += (s, e) => ShowSelectedPosition();
            panel.Children.Add(_noiseFavouriteOrder);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            var positionLabel = new Label { Content = Lexicon.Get("audio.noise_settings.position_label") };
            _noiseFavouritePosition = new TextBox { Width = 50 };
            positionLabel.Target = _noiseFavouritePosition;
            AutomationProperties.SetName(_noiseFavouritePosition, Lexicon.Get("audio.noise_settings.position"));
            JJFlexHelp.SetText(_noiseFavouritePosition, Lexicon.Get("audio.noise_help.settings.position"));
            _noiseFavouritePosition.KeyDown += (s, e) => { if (e.Key == Key.Return) { ApplyTypedPosition(); e.Handled = true; } };
            _noiseFavouritePosition.LostFocus += (s, e) => ApplyTypedPosition();
            row.Children.Add(positionLabel);
            row.Children.Add(_noiseFavouritePosition);
            row.Children.Add(NoiseButton("audio.noise_settings.move_up_button", "audio.noise_settings.move_up", () => MoveSelected(-1)));
            row.Children.Add(NoiseButton("audio.noise_settings.move_down_button", "audio.noise_settings.move_down", () => MoveSelected(+1)));
            row.Children.Add(NoiseButton("audio.noise_settings.rename_button", "audio.noise_settings.rename", RenameSelected));
            panel.Children.Add(row);

            _noiseSayDelays = new CheckBox { Content = Lexicon.Get("audio.noise_settings.say_delays"), Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetName(_noiseSayDelays, Lexicon.Get("audio.noise_settings.say_delays"));
            JJFlexHelp.SetText(_noiseSayDelays, Lexicon.Get("audio.noise_help.settings.say_delays"));
            _noiseSayDelays.Checked += (s, e) => { if (!_noiseSettingsFilling) _audioConfig.NoiseSayDelays = true; };
            _noiseSayDelays.Unchecked += (s, e) => { if (!_noiseSettingsFilling) _audioConfig.NoiseSayDelays = false; };
            panel.Children.Add(_noiseSayDelays);

            // The three engine-change switches on receive (#873, ruled 2026-11-08
            // 12:57; Sprint 49 Track P). Here rather than on the Advanced tab: they
            // are everyday choices, and Advanced sits behind a warning (the draft's
            // judgement; where they live was not ruled).
            _noiseSwitchSpeech = SwitchCheck("audio.noise_settings.switch_speech", "audio.noise_help.settings.switch_speech",
                on => _audioConfig.NoiseSwitchSpeech = on);
            _noiseSwitchBeep = SwitchCheck("audio.noise_settings.switch_beep", "audio.noise_help.settings.switch_beep",
                on => _audioConfig.NoiseSwitchBeep = on);
            _noiseSwitchMute = SwitchCheck("audio.noise_settings.switch_mute", "audio.noise_help.settings.switch_mute",
                on => { _audioConfig.NoiseSwitchMute = on; PcDenoiseSlot.SwitchMuteEnabled = on; });
            panel.Children.Add(_noiseSwitchSpeech);
            panel.Children.Add(_noiseSwitchBeep);
            panel.Children.Add(_noiseSwitchMute);

            AudioTabPanel.Children.Add(group);
            FillNoiseSettings();
        }

        /// <summary>One of the three engine-change switches: named, with its Ctrl+F1 text, writing through <paramref name="set"/> unless the group is being filled.</summary>
        private CheckBox SwitchCheck(string labelKey, string helpKey, Action<bool> set)
        {
            var check = new CheckBox { Content = Lexicon.Get(labelKey), Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetName(check, Lexicon.Get(labelKey));
            JJFlexHelp.SetText(check, Lexicon.Get(helpKey));
            check.Checked += (s, e) => { if (!_noiseSettingsFilling) set(true); };
            check.Unchecked += (s, e) => { if (!_noiseSettingsFilling) set(false); };
            return check;
        }

        // ────────────────────────────────────────────────────────────────
        //  The Advanced tab (#873, Sprint 49 Track E, E14). Last, and shut
        //  until its one checkbox is ticked. Only fields backed by a value
        //  the code really has: the cross-fade switch, and the measured
        //  delays, read-only. The helper's queue depths are NOT here: see the
        //  Track E report (they are fixed per instance by Track B's design,
        //  and per model by ruling).
        // ────────────────────────────────────────────────────────────────

        private CheckBox? _advancedUnderstood;
        private CheckBox? _advancedCrossFade;
        private ListBox? _advancedDelays;
        private ValueFieldControl? _advancedDepthStandard, _advancedDepthPremium;

        private void InitializeAdvancedTab()
        {
            if (SettingsTabs == null) return;
            var panel = new StackPanel { Margin = new Thickness(8) };
            _advancedUnderstood = new CheckBox { Content = Lexicon.Get("audio.noise_advanced.understood"), IsChecked = _audioConfig.AdvancedSettingsUnderstood, Margin = new Thickness(0, 0, 0, 8) };
            AutomationProperties.SetName(_advancedUnderstood, Lexicon.Get("audio.noise_advanced.understood"));
            JJFlexHelp.SetText(_advancedUnderstood, Lexicon.Get("audio.noise_help.advanced.understood"));
            panel.Children.Add(_advancedUnderstood);

            _advancedCrossFade = new CheckBox { Content = Lexicon.Get("audio.noise_advanced.crossfade"), IsChecked = _audioConfig.NoiseCrossFade, Margin = new Thickness(0, 2, 0, 8) };
            AutomationProperties.SetName(_advancedCrossFade, Lexicon.Get("audio.noise_advanced.crossfade"));
            JJFlexHelp.SetText(_advancedCrossFade, Lexicon.Get("audio.noise_help.advanced.crossfade"));
            _advancedCrossFade.Checked += (s, e) => { _audioConfig.NoiseCrossFade = true; PcDenoiseSlot.CrossFadeEnabled = true; };
            _advancedCrossFade.Unchecked += (s, e) => { _audioConfig.NoiseCrossFade = false; PcDenoiseSlot.CrossFadeEnabled = false; };
            panel.Children.Add(_advancedCrossFade);

            // X6 (#873; ruled yes 2026-11-19, after Noel 2026-11-06 11:52): the
            // helper's receive queue depths, typed fields, behind the guard. No
            // deadline margin: the helper has none apart from this depth (an
            // answer is late when it is not back k frames later).
            _advancedDepthStandard = DepthField("audio.noise_advanced.depth_standard_label", _audioConfig.HelperQueueDepthStandard,
                v => ApplyHelperDepths(v, _audioConfig.HelperQueueDepthPremium));
            _advancedDepthPremium = DepthField("audio.noise_advanced.depth_premium_label", _audioConfig.HelperQueueDepthPremium,
                v => ApplyHelperDepths(_audioConfig.HelperQueueDepthStandard, v));
            panel.Children.Add(_advancedDepthStandard);
            panel.Children.Add(_advancedDepthPremium);

            var delaysLabel = new Label { Content = Lexicon.Get("audio.noise_advanced.delays_label") };
            panel.Children.Add(delaysLabel);
            _advancedDelays = new ListBox { Height = 140 };
            delaysLabel.Target = _advancedDelays;
            AutomationProperties.SetName(_advancedDelays, Lexicon.Get("audio.noise_advanced.delays"));
            JJFlexHelp.SetText(_advancedDelays, Lexicon.Get("audio.noise_help.advanced.delays"));
            panel.Children.Add(_advancedDelays);

            var reset = new Button { Content = Lexicon.Get("audio.noise_advanced.defaults_button"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(reset, Lexicon.Get("audio.noise_advanced.defaults"));
            reset.Click += (s, e) =>
            {
                _advancedCrossFade.IsChecked = true;
                // The fields' Value shows a figure and deliberately raises no
                // ValueChanged, so the reset writes the saved and live depths
                // itself (repair 1, B1): all three, or a risky depth stays in force
                // behind a display that says the default.
                _advancedDepthStandard.Value = WdspPipelineDepth.NnrStandard;
                _advancedDepthPremium.Value = WdspPipelineDepth.NnrPremium;
                ApplyHelperDepths(WdspPipelineDepth.NnrStandard, WdspPipelineDepth.NnrPremium);
            };
            panel.Children.Add(reset);

            void Gate()
            {
                bool open = _advancedUnderstood.IsChecked == true;
                _audioConfig.AdvancedSettingsUnderstood = open;
                _advancedCrossFade.IsEnabled = open;
                _advancedDepthStandard.IsEnabled = open;
                _advancedDepthPremium.IsEnabled = open;
                reset.IsEnabled = open;
            }
            _advancedUnderstood.Checked += (s, e) => Gate();
            _advancedUnderstood.Unchecked += (s, e) => Gate();
            Gate();

            var tab = new TabItem { Header = Lexicon.Get("audio.noise_advanced.tab"), Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            AutomationProperties.SetName(tab, Lexicon.Get("audio.noise_advanced.tab_name"));
            RefillWhenTheTabTakesFocus(tab, FillMeasuredDelays);
            SettingsTabs.Items.Add(tab);
            FillMeasuredDelays();
        }

        /// <summary>
        /// The helper's receive queue depths, saved and in force together: the
        /// settings and <see cref="WdspPipelineDepth"/>'s receive overrides, which
        /// the next helper engine built reads. The one way the fields and the
        /// reset change them (repair 1, B1).
        /// </summary>
        private void ApplyHelperDepths(int standard, int premium)
        {
            _audioConfig.HelperQueueDepthStandard = standard;
            _audioConfig.HelperQueueDepthPremium = premium;
            WdspPipelineDepth.SetReceiveDepths(standard, premium);
        }

        /// <summary>A helper queue depth field: 1 to 6 frames, its Ctrl+F1 text, writing through <paramref name="set"/> as it changes.</summary>
        private static ValueFieldControl DepthField(string labelKey, int value, Action<int> set)
        {
            var field = new ValueFieldControl { Margin = new Thickness(0, 2, 0, 8) };
            field.Setup(Lexicon.Get(labelKey), WdspPipelineDepth.MinReceiveDepth, WdspPipelineDepth.MaxReceiveDepth, 1,
                Math.Clamp(value, WdspPipelineDepth.MinReceiveDepth, WdspPipelineDepth.MaxReceiveDepth));
            JJFlexHelp.SetText(field, Lexicon.Get("audio.noise_help.advanced.depth"));
            field.ValueChanged += (s, v) => set(v);
            return field;
        }

        /// <summary>One line per computer engine: the live figure where it is running, the expected one for a helper engine.</summary>
        private void FillMeasuredDelays()
        {
            if (_advancedDelays == null) return;
            FillRowsKeepingPlace(_advancedDelays, MeasuredDelayRows(Noise));
        }

        /// <summary>
        /// Refresh the list when the Advanced tab ITSELF takes focus, never when
        /// focus moves inside it (Sprint 49 Track N2). GotFocus bubbles: a row of
        /// the Measured delays list taking focus reached the tab's handler, the
        /// refill cleared the list under that row, and focus was left on the list
        /// with no row, so Down, Home and End reached no figure (the integration
        /// walk at f3beb60b).
        /// </summary>
        internal static void RefillWhenTheTabTakesFocus(TabItem tab, Action refill) =>
            tab.GotFocus += (s, e) => { if (ReferenceEquals(e.OriginalSource, tab)) refill(); };

        /// <summary>
        /// Put these rows in the list. Rows that already read the same are left
        /// alone, so the row under the keyboard is never destroyed by a refill
        /// that changed nothing; a refill that did change something keeps the
        /// selected position (Sprint 49 Track N2).
        /// </summary>
        internal static void FillRowsKeepingPlace(ListBox list, IReadOnlyList<string> rows)
        {
            if (list.Items.Count == rows.Count && list.Items.Cast<object>().Select(o => o as string).SequenceEqual(rows)) return;
            int selected = list.SelectedIndex;
            list.Items.Clear();
            foreach (string row in rows) list.Items.Add(row);
            if (selected >= 0 && rows.Count > 0) list.SelectedIndex = Math.Min(selected, rows.Count - 1);
        }

        /// <summary>
        /// The Measured delays rows, one per computer engine: its own delay and
        /// its family's shared delay (#873, Sprint 49 Track P; Noel 2026-11-04
        /// 19:57), each row whole, from <see cref="NoiseController.SharedDelayRow"/>.
        /// No radio is needed: the figures are the computer's. Only with no
        /// controller at all is there one line saying there is nothing to show.
        /// </summary>
        internal static IReadOnlyList<string> MeasuredDelayRows(NoiseController? noise)
        {
            if (noise == null) return new[] { Lexicon.Get("audio.noise_advanced.delays_none") };
            return noise.Engines().Where(e => e.Side == DenoiseSide.Pc)
                .Select(e => noise.SharedDelayRow(e.Id))
                .ToList();
        }

        private static Button NoiseButton(string buttonKey, string nameKey, Action act)
        {
            var b = new Button { Content = Lexicon.Get(buttonKey), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 2, 6, 2) };
            AutomationProperties.SetName(b, Lexicon.Get(nameKey));
            b.Click += (s, e) => act();
            return b;
        }

        private IEnumerable<DenoiseEngineEntry> NoiseEngines() =>
            (Noise?.Catalog ?? new DenoiseEngineCatalog()).InSurfaceOrder().Where(e => e.Serves(DenoiseDirection.Receive));

        private string NoiseEngineName(string id) =>
            Noise?.MarkedName(id) ?? ((Noise?.Catalog ?? new DenoiseEngineCatalog()).Find(id) is { } e ? Lexicon.Get(e.LabelKey) : id);

        private string FavouriteText(NoiseFavourite f, int index) =>
            Lexicon.Get("audio.noise_settings.order_item", ("position", index + 1),
                ("name", Noise?.FavouriteName(f) ?? NoiseEngineName(f.EngineId)));

        /// <summary>Fill every control from the config, silently.</summary>
        private void FillNoiseSettings()
        {
            if (_noiseFavouriteChecks == null || _noiseFavouriteOrder == null || _noiseSayDelays == null) return;
            _noiseSettingsFilling = true;
            try
            {
                int selected = _noiseFavouriteOrder.SelectedIndex;
                _noiseFavouriteChecks.Children.Clear();
                foreach (var e in NoiseEngines())
                {
                    string id = e.Id;
                    var check = new CheckBox
                    {
                        Content = NoiseEngineName(id),
                        IsChecked = _audioConfig.NoiseFavourites.Any(f => f.EngineId == id),
                        Margin = new Thickness(2),
                    };
                    AutomationProperties.SetName(check, Lexicon.Get("audio.noise_settings.favourite_check", ("engine", NoiseEngineName(id))));
                    check.Checked += (s, a) => { if (!_noiseSettingsFilling) AddFavourite(id); };
                    check.Unchecked += (s, a) => { if (!_noiseSettingsFilling) RemoveFavourites(id); };
                    _noiseFavouriteChecks.Children.Add(check);
                }
                _noiseFavouriteOrder.Items.Clear();
                for (int i = 0; i < _audioConfig.NoiseFavourites.Count; i++)
                    _noiseFavouriteOrder.Items.Add(FavouriteText(_audioConfig.NoiseFavourites[i], i));
                if (_noiseFavouriteOrder.Items.Count > 0)
                    _noiseFavouriteOrder.SelectedIndex = Math.Clamp(selected, 0, _noiseFavouriteOrder.Items.Count - 1);
                _noiseSayDelays.IsChecked = _audioConfig.NoiseSayDelays;
                if (_noiseSwitchSpeech != null) _noiseSwitchSpeech.IsChecked = _audioConfig.NoiseSwitchSpeech;
                if (_noiseSwitchBeep != null) _noiseSwitchBeep.IsChecked = _audioConfig.NoiseSwitchBeep;
                if (_noiseSwitchMute != null) _noiseSwitchMute.IsChecked = _audioConfig.NoiseSwitchMute;
                ShowSelectedPosition();
            }
            finally { _noiseSettingsFilling = false; }
        }

        private void AddFavourite(string id)
        {
            string? model = Noise?.CurrentModel(id);
            if (_audioConfig.NoiseFavourites.Any(f => f.SameAs(id, model))) return;
            _audioConfig.NoiseFavourites.Add(new NoiseFavourite { EngineId = id, Model = model });
            FavouritesChanged();
        }

        private void RemoveFavourites(string id)
        {
            _audioConfig.NoiseFavourites.RemoveAll(f => f.EngineId == id);
            FavouritesChanged();
        }

        /// <summary>The favourites are the shared delay's set (#873): refill, hand the slots their new plans, and refresh the delays list.</summary>
        private void FavouritesChanged()
        {
            FillNoiseSettings();
            Noise?.PublishSharedDelays();
            FillMeasuredDelays();
        }

        private void ShowSelectedPosition()
        {
            if (_noiseFavouritePosition == null || _noiseFavouriteOrder == null) return;
            _noiseFavouritePosition.Text = _noiseFavouriteOrder.SelectedIndex < 0 ? "" : (_noiseFavouriteOrder.SelectedIndex + 1).ToString();
        }

        private void MoveTo(int from, int position)
        {
            var list = _audioConfig.NoiseFavourites;
            if (from < 0 || from >= list.Count) return;
            var item = list[from];
            list.RemoveAt(from);
            int to = Math.Clamp(position - 1, 0, list.Count);
            list.Insert(to, item);
            FillNoiseSettings();
            _noiseFavouriteOrder!.SelectedIndex = to;
            ScreenReaderOutput.Speak(Lexicon.Get("audio.noise_settings.moved", ("name", Noise?.FavouriteName(item) ?? NoiseEngineName(item.EngineId)), ("position", to + 1)),
                VerbosityLevel.Terse, interrupt: true);
        }

        private void MoveSelected(int by)
        {
            if (_noiseFavouriteOrder == null) return;
            int at = _noiseFavouriteOrder.SelectedIndex;
            if (at < 0) return;
            MoveTo(at, at + 1 + by);
        }

        private void ApplyTypedPosition()
        {
            if (_noiseFavouriteOrder == null || _noiseFavouritePosition == null || _noiseSettingsFilling) return;
            int at = _noiseFavouriteOrder.SelectedIndex;
            if (at < 0 || !int.TryParse(_noiseFavouritePosition.Text.Trim(), out int position) || position == at + 1) return;
            MoveTo(at, position);
        }

        private void RenameSelected()
        {
            if (_noiseFavouriteOrder == null) return;
            int at = _noiseFavouriteOrder.SelectedIndex;
            if (at < 0 || at >= _audioConfig.NoiseFavourites.Count) return;
            var fav = _audioConfig.NoiseFavourites[at];
            string? name = EvidenceRenameDialog.Ask(this, Lexicon.Get("audio.noise_settings.favourite_noun"),
                Noise?.FavouriteName(new NoiseFavourite { EngineId = fav.EngineId, Model = fav.Model }) ?? fav.EngineId, fav.Name);
            if (name == null) return;
            fav.Name = name.Trim();
            FillNoiseSettings();
        }
    }
}
