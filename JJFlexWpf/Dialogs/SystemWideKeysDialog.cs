using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using JJTrace;
using Radios;
using WinFormsKeys = System.Windows.Forms.Keys;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Where the operator chooses the three system-wide chords (#307): the JJ
    /// key, push to talk and the transmit lock as they are reached from
    /// another program. Tools menu, System-wide keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The picker is the deliverable, not the chord choice.</b> Noel,
    /// 2026-10-05: <i>"As long as a user can select the two modifier keys and
    /// another key then cool done ... if it doesn't work, user can change
    /// it."</i> The defaults are provisional; a collision with a contest
    /// logger, a screen reader or anything else is handled by the operator
    /// moving the chord, not by us predicting it.
    /// </para>
    /// <para>
    /// <b>Two modifiers, enforced.</b> A system-wide chord is taken from every
    /// program on the machine and an accidental press of one of them keys a
    /// transmitter, so the capture refuses anything with fewer than two of
    /// Ctrl, Alt and Shift, out loud, and keeps waiting.
    /// </para>
    /// <para>
    /// <b>The conflict report has four layers and says which one answered</b>,
    /// because no single layer sees everything. First, this program's own key
    /// table — the same <c>FindBindingConflicts</c> lookup the Hotkey Editor
    /// uses, asked across every scope since a system-wide key fires in all of
    /// them — plus the three fixed in-window keys. Second,
    /// <see cref="HotkeyOwnershipProbe"/>: has another program registered the
    /// chord with Windows. Third, the blind spot stated rather than hidden:
    /// that probe cannot see programs that hook the keyboard, which includes
    /// screen readers and this one. Fourth, the only test that can: press it
    /// with another program focused and listen. A clear second layer is
    /// worded as "no other program has registered it", never as "free".
    /// </para>
    /// <para>
    /// <b>Changes take effect on OK</b>, live, with no restart: the hook reads
    /// a new snapshot. Cancel and Escape leave both the file and the hook as
    /// they were.
    /// </para>
    /// </remarks>
    public sealed class SystemWideKeysDialog : JJFlexDialog
    {
        /// <summary>
        /// This program's own bindings of a chord, as "command in scope"
        /// phrases, from the live key registry. Wired by the host; null when
        /// there is no registry to ask, and the report then says so rather
        /// than claiming the table was clear.
        /// </summary>
        public static Func<WinFormsKeys, IReadOnlyList<string>>? InAppConflictsSource { get; set; }

        private readonly SystemWideKeysConfig _config;
        private readonly string _configDirectory;
        private SystemWideKeySet _draft;

        private readonly CheckBox _enabled = new();
        private readonly Dictionary<SystemWideRole, Button> _changeButtons = new();
        private readonly Dictionary<SystemWideRole, TextBlock> _reports = new();
        private readonly TextBlock _status = new();
        private SystemWideRole? _capturing;

        /// <summary>Open on the saved configuration. The dialog sweep's constructor.</summary>
        public SystemWideKeysDialog()
            : this(SystemWideKeysConfig.Load(RadioConfig.AppDataRoot), RadioConfig.AppDataRoot) { }

        public SystemWideKeysDialog(SystemWideKeysConfig config, string configDirectory)
        {
            _config = config ?? new SystemWideKeysConfig();
            _configDirectory = configDirectory ?? string.Empty;
            _draft = _config.ToKeySet();

            Title = Lexicon.Get("settings.systemwide_keys.title");
            Width = 620;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(16) };

            var intro = new TextBlock
            {
                Text = Lexicon.Get("settings.systemwide_keys.intro"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            };
            root.Children.Add(intro);

            _enabled.Content = Lexicon.Get("settings.systemwide_keys.enabled");
            _enabled.IsChecked = _draft.Enabled;
            _enabled.Margin = new Thickness(0, 0, 0, 12);
            AutomationProperties.SetName(_enabled, Lexicon.Get("settings.systemwide_keys.enabled"));
            JJFlexHelp.SetText(_enabled, Lexicon.Get("settings.systemwide_keys.enabled_help"));
            root.Children.Add(_enabled);

            foreach (var role in SystemWideKeySet.AllRoles)
                root.Children.Add(BuildRole(role));

            _status.TextWrapping = TextWrapping.Wrap;
            _status.Margin = new Thickness(0, 8, 0, 8);
            AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
            root.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var reset = MakeButton(Lexicon.Get("settings.systemwide_keys.reset"), ResetToDefaults);
            buttons.Children.Add(reset);
            var ok = MakeButton(Lexicon.Get("settings.systemwide_keys.ok"), SaveAndClose);
            ok.IsDefault = true;
            buttons.Children.Add(ok);
            var cancel = MakeButton(Lexicon.Get("settings.systemwide_keys.cancel"), () => CloseWithResult(false));
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(0);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = root;
        }

        /// <summary>Open modally over the given owner.</summary>
        public static void Show(Window? owner)
        {
            var dialog = new SystemWideKeysDialog();
            if (owner != null) dialog.Owner = owner;
            dialog.ShowModalDialog();
        }

        // ── layout ───────────────────────────────────────────────────────

        private UIElement BuildRole(SystemWideRole role)
        {
            var group = new GroupBox
            {
                Header = SystemWideKeys.RoleName(role),
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(8),
            };
            var inner = new StackPanel();

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var change = MakeButton(RoleLabel(role), () => BeginCapture(role));
            change.MinWidth = 320;
            change.HorizontalContentAlignment = HorizontalAlignment.Left;
            JJFlexHelp.SetText(change, Lexicon.Get("settings.systemwide_keys.change_help"));
            _changeButtons[role] = change;
            row.Children.Add(change);

            var check = MakeButton(Lexicon.Get("settings.systemwide_keys.check", ("role", SystemWideKeys.RoleName(role))),
                () => ReportConflicts(role, announce: true));
            check.Margin = new Thickness(0);
            JJFlexHelp.SetText(check, Lexicon.Get("settings.systemwide_keys.check_help"));
            row.Children.Add(check);
            inner.Children.Add(row);

            var report = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            _reports[role] = report;
            inner.Children.Add(report);

            group.Content = inner;
            return group;
        }

        private static Button MakeButton(string label, Action onClick)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 96,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(8, 0, 8, 0),
            };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) =>
            {
                try { onClick(); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("SystemWideKeysDialog: " + label + " failed: " + ex.Message, TraceLevel.Error);
                }
            };
            return button;
        }

        private string RoleLabel(SystemWideRole role) =>
            Lexicon.Get("settings.systemwide_keys.role_label",
                ("role", SystemWideKeys.RoleName(role)),
                ("chord", ChordName(_draft.For(role))));

        private static string ChordName(WinFormsKeys chord) =>
            chord == WinFormsKeys.None
                ? Lexicon.Get("settings.systemwide_keys.unassigned")
                : KeyManifest.FormatKey(chord);

        private void RefreshRoleLabel(SystemWideRole role)
        {
            var button = _changeButtons[role];
            string label = RoleLabel(role);
            button.Content = label;
            AutomationProperties.SetName(button, label);
        }

        // ── capture ──────────────────────────────────────────────────────

        private void BeginCapture(SystemWideRole role)
        {
            _capturing = role;
            Say(Lexicon.Get("settings.systemwide_keys.press_new_key", ("role", SystemWideKeys.RoleName(role))));
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (_capturing is SystemWideRole role)
            {
                // While capturing, every key is ours — including Escape, which
                // the base class would otherwise read as "close the dialog".
                e.Handled = true;

                var raw = e.Key == Key.System ? e.SystemKey : e.Key;
                if (raw is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
                    or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.Apps)
                {
                    return;   // a modifier on its way down; keep waiting for the key
                }

                if (raw == Key.Escape)
                {
                    _capturing = null;
                    Say(Lexicon.Get("settings.systemwide_keys.capture_cancelled"));
                    return;
                }

                if (raw == Key.Delete)
                {
                    _capturing = null;
                    _draft = _draft.With(role, WinFormsKeys.None);
                    RefreshRoleLabel(role);
                    _reports[role].Text = string.Empty;
                    Say(Lexicon.Get("settings.systemwide_keys.removed", ("role", SystemWideKeys.RoleName(role))));
                    return;
                }

                var chord = WpfKeyConverter.ToWinFormsKeys(e);
                var verdict = SystemWideChord.Judge(chord);
                switch (verdict)
                {
                    case SystemWideChordVerdict.NoKey:
                    case SystemWideChordVerdict.ModifierOnly:
                        Say(Lexicon.Get("settings.systemwide_keys.key_unusable"));
                        return;
                    case SystemWideChordVerdict.FewerThanTwoModifiers:
                        Say(Lexicon.Get("settings.systemwide_keys.needs_two_modifiers",
                            ("chord", KeyManifest.FormatKey(chord))));
                        return;
                }

                var taken = _draft.RoleOf(chord);
                if (taken != null && taken.Value != role)
                {
                    Say(Lexicon.Get("settings.systemwide_keys.duplicate",
                        ("chord", KeyManifest.FormatKey(chord)),
                        ("other", SystemWideKeys.RoleName(taken.Value))));
                    return;
                }

                _capturing = null;
                _draft = _draft.With(role, chord);
                RefreshRoleLabel(role);
                string assigned = Lexicon.Get("settings.systemwide_keys.assigned",
                    ("role", SystemWideKeys.RoleName(role)), ("chord", KeyManifest.FormatKey(chord)));
                string report = BuildReport(role, chord);
                _reports[role].Text = report;
                Say(assigned + " " + report);
                return;
            }

            base.OnPreviewKeyDown(e);
        }

        // ── the four-layer conflict report ───────────────────────────────

        private void ReportConflicts(SystemWideRole role, bool announce)
        {
            var chord = _draft.For(role);
            string report = chord == WinFormsKeys.None
                ? Lexicon.Get("settings.systemwide_keys.report.unassigned", ("role", SystemWideKeys.RoleName(role)))
                : BuildReport(role, chord);
            _reports[role].Text = report;
            if (announce) Say(report);
        }

        /// <summary>
        /// One paragraph, assembled in the order the operator needs it:
        /// what this program says, what Windows says, what Windows cannot
        /// see, and the one test that can.
        /// </summary>
        private string BuildReport(SystemWideRole role, WinFormsKeys chord)
        {
            string name = KeyManifest.FormatKey(chord);
            var sb = new StringBuilder();

            // Layer 1: this program's own keys, every scope.
            var inApp = InAppBindings(chord, role);
            if (inApp == null)
                sb.Append(Lexicon.Get("settings.systemwide_keys.report.in_app_unknown"));
            else if (inApp.Count == 0)
                sb.Append(Lexicon.Get("settings.systemwide_keys.report.in_app_clear", ("chord", name)));
            else
                sb.Append(Lexicon.Get("settings.systemwide_keys.report.in_app_conflict",
                    ("chord", name), ("commands", string.Join(" and ", inApp))));
            sb.Append(' ');

            // Layer 2: RegisterHotKey as a detector.
            var verdict = HotkeyOwnershipProbe.Probe(chord, out int error);
            switch (verdict)
            {
                case HotkeyOwnershipProbe.Verdict.OwnedByAnotherProgram:
                    sb.Append(Lexicon.Get("settings.systemwide_keys.report.registered_elsewhere", ("chord", name)));
                    break;
                case HotkeyOwnershipProbe.Verdict.NoRegisterHotKeyOwner:
                    sb.Append(Lexicon.Get("settings.systemwide_keys.report.no_registered_owner", ("chord", name)));
                    break;
                default:
                    sb.Append(Lexicon.Get("settings.systemwide_keys.report.could_not_check", ("chord", name)));
                    break;
            }
            Tracing.TraceLine("SystemWideKeysDialog: RegisterHotKey probe of " + name + " says " + verdict
                + (error != 0 ? " (error " + error + ")" : ""), TraceLevel.Info);
            sb.Append(' ');

            // Layer 3: the blind spot, always.
            sb.Append(Lexicon.Get("settings.systemwide_keys.report.blind_spot"));
            sb.Append(' ');

            // Layer 4: the only test that sees a hook-based owner.
            sb.Append(Lexicon.Get("settings.systemwide_keys.report.press_it", ("chord", name)));
            return sb.ToString();
        }

        /// <summary>
        /// Everything inside this program that already means the chord: the
        /// registry's bindings across every scope, and the three fixed
        /// in-window keys. Null when the registry cannot be asked.
        /// </summary>
        private static List<string>? InAppBindings(WinFormsKeys chord, SystemWideRole role)
        {
            var source = InAppConflictsSource;
            if (source == null) return null;

            List<string> found;
            try { found = source(chord).ToList(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("SystemWideKeysDialog: in-app conflict lookup failed: " + ex.Message, TraceLevel.Error);
                return null;
            }

            // The in-window twins are not registry rows; name them by hand,
            // except the one this role is the twin OF — Ctrl+J for the
            // leader is not a conflict, it is the same idea with one door.
            if (chord == (WinFormsKeys.J | WinFormsKeys.Control) && role != SystemWideRole.Leader)
                found.Add(Lexicon.Get("leader.help.layer_name"));
            if (chord == (WinFormsKeys.Space | WinFormsKeys.Control) && role != SystemWideRole.PushToTalk)
                found.Add(Lexicon.Get("settings.systemwide_keys.role.push_to_talk"));
            if (chord == (WinFormsKeys.Space | WinFormsKeys.Shift) && role != SystemWideRole.TransmitLock)
                found.Add(Lexicon.Get("settings.systemwide_keys.role.transmit_lock"));
            return found;
        }

        // ── OK, reset ────────────────────────────────────────────────────

        private void ResetToDefaults()
        {
            _draft = SystemWideKeySet.Defaults with { Enabled = _enabled.IsChecked == true };
            foreach (var role in SystemWideKeySet.AllRoles)
            {
                RefreshRoleLabel(role);
                _reports[role].Text = string.Empty;
            }
            Say(Lexicon.Get("settings.systemwide_keys.reset_done"));
        }

        private void SaveAndClose()
        {
            _draft = _draft with { Enabled = _enabled.IsChecked == true };
            _config.FromKeySet(_draft);
            bool saved = _config.Save(_configDirectory);

            // The file may have repaired something on the way out (it cannot,
            // from this dialog, but Sanitize is the authority); apply what was
            // actually written.
            var applied = _config.ToKeySet();
            SystemWideKeys.Apply(applied);

            string outcome = !saved
                ? Lexicon.Get("settings.systemwide_keys.save_failed")
                : applied.Enabled
                    ? Lexicon.Get("settings.systemwide_keys.saved")
                    : Lexicon.Get("settings.systemwide_keys.saved_off");
            Say(outcome);
            CloseWithResult(true);
        }

        private void Say(string sentence)
        {
            _status.Text = sentence;
            ScreenReaderOutput.Speak(sentence, Radios.Speech.SpeechIntent.Interrupt,
                VerbosityLevel.Critical, subject: Radios.Speech.SpeechSubject.SystemWideKeyPicker);
        }
    }
}
