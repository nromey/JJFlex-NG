using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
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
        public NoiseController? Noise { get; set; }

        private StackPanel? _noiseFavouriteChecks;
        private ListBox? _noiseFavouriteOrder;
        private TextBox? _noiseFavouritePosition;
        private CheckBox? _noiseSayDelays;
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

            AudioTabPanel.Children.Add(group);
            FillNoiseSettings();
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

            var delaysLabel = new Label { Content = Lexicon.Get("audio.noise_advanced.delays_label") };
            panel.Children.Add(delaysLabel);
            _advancedDelays = new ListBox { Height = 140 };
            delaysLabel.Target = _advancedDelays;
            AutomationProperties.SetName(_advancedDelays, Lexicon.Get("audio.noise_advanced.delays"));
            JJFlexHelp.SetText(_advancedDelays, Lexicon.Get("audio.noise_help.advanced.delays"));
            panel.Children.Add(_advancedDelays);

            var reset = new Button { Content = Lexicon.Get("audio.noise_advanced.defaults_button"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(reset, Lexicon.Get("audio.noise_advanced.defaults"));
            reset.Click += (s, e) => { _advancedCrossFade.IsChecked = true; };
            panel.Children.Add(reset);

            void Gate()
            {
                bool open = _advancedUnderstood.IsChecked == true;
                _audioConfig.AdvancedSettingsUnderstood = open;
                _advancedCrossFade.IsEnabled = open;
                reset.IsEnabled = open;
            }
            _advancedUnderstood.Checked += (s, e) => Gate();
            _advancedUnderstood.Unchecked += (s, e) => Gate();
            Gate();

            var tab = new TabItem { Header = Lexicon.Get("audio.noise_advanced.tab"), Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            AutomationProperties.SetName(tab, Lexicon.Get("audio.noise_advanced.tab_name"));
            tab.GotFocus += (s, e) => FillMeasuredDelays();
            SettingsTabs.Items.Add(tab);
            FillMeasuredDelays();
        }

        /// <summary>One line per computer engine: the live figure where it is running, the expected one for a helper engine.</summary>
        private void FillMeasuredDelays()
        {
            if (_advancedDelays == null) return;
            _advancedDelays.Items.Clear();
            if (Noise == null) { _advancedDelays.Items.Add(Lexicon.Get("audio.noise_advanced.delays_none")); return; }
            foreach (var e in NoiseEngines().Where(e => e.Side == DenoiseSide.Pc))
                _advancedDelays.Items.Add(Lexicon.Get("audio.noise_advanced.delay_line", ("engine", Noise.NameOf(e.Id)), ("delay", Noise.DelayText(e.Id))));
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
                ShowSelectedPosition();
            }
            finally { _noiseSettingsFilling = false; }
        }

        private void AddFavourite(string id)
        {
            string? model = Noise?.CurrentModel(id);
            if (_audioConfig.NoiseFavourites.Any(f => f.SameAs(id, model))) return;
            _audioConfig.NoiseFavourites.Add(new NoiseFavourite { EngineId = id, Model = model });
            FillNoiseSettings();
        }

        private void RemoveFavourites(string id)
        {
            _audioConfig.NoiseFavourites.RemoveAll(f => f.EngineId == id);
            FillNoiseSettings();
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
