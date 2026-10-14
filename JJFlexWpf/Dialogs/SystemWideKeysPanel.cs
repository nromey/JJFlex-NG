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
    /// Where the operator chooses the three system-wide chords (#307) and the
    /// two switches over them: the JJ key, push to talk and the transmit lock
    /// as they are reached from another program. Hosted by Settings, under
    /// System-wide keys; the Tools menu item of that name deep-links there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a panel and not a dialog (Noel, 2026-10-05).</b> <i>"I'd have
    /// the ptt toggle stuff in a settings tab somewhere, no need for two
    /// nested dialogs do you?"</i> — and the code gave a sharper reason than
    /// nesting: Track B taught the Keys dialog to LIST these rows under
    /// "System-wide" while the only place to CHANGE them was a separate
    /// dialog under Tools, so an operator found the row and had nowhere to go
    /// from it. Now the configuration lives in Settings, the Tools item opens
    /// Settings at this tab exactly as "Diagnostic Log" does, and the Keys
    /// dialog's Change Key on a system-wide row goes to the same place.
    /// </para>
    /// <para>
    /// <b>Two switches, not one (Item 4, recommended in #689 and built as
    /// recommended).</b> The two halves fail differently. The system-wide
    /// push to talk is the only binding in the product that puts RF on the
    /// air from another application; the system-wide JJ key, at worst, eats
    /// a character in a logger. The failure that makes an operator reach for
    /// a switch is the JJ key's, and with ONE switch the thing they would lose
    /// is the push to talk that was the entire point. The transmit lock
    /// travels with push to talk because toggling it keys the transmitter.
    /// Both default on.
    /// </para>
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
    /// <b>Changes take effect on the host's OK or Apply</b>, live, with no
    /// restart: <see cref="Commit"/> saves the file and the hook reads a new
    /// snapshot. Cancel and Escape leave both the file and the hook as they
    /// were. The host must route its <c>OnPreviewKeyDown</c> through
    /// <see cref="HandleCaptureKey"/> so a capture in progress — Escape
    /// included — is this panel's and not the dialog's close.
    /// </para>
    /// </remarks>
    public sealed class SystemWideKeysPanel : UserControl
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
        private readonly SystemWideKeySet _loaded;
        private SystemWideKeySet _draft;

        private readonly CheckBox _leaderEnabled = new();
        private readonly CheckBox _transmitEnabled = new();
        private readonly Dictionary<SystemWideRole, Button> _changeButtons = new();
        private readonly Dictionary<SystemWideRole, TextBlock> _reports = new();
        private readonly TextBlock _status = new();
        private SystemWideRole? _capturing;

        /// <summary>On the saved configuration.</summary>
        public SystemWideKeysPanel()
            : this(SystemWideKeysConfig.Load(RadioConfig.AppDataRoot), RadioConfig.AppDataRoot) { }

        public SystemWideKeysPanel(SystemWideKeysConfig config, string configDirectory)
        {
            _config = config ?? new SystemWideKeysConfig();
            _configDirectory = configDirectory ?? string.Empty;
            _draft = _config.ToKeySet();
            _loaded = _draft;

            var root = new StackPanel();

            var intro = new TextBlock
            {
                Text = Lexicon.Get("settings.systemwide_keys.intro"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            };
            root.Children.Add(intro);

            BuildToggle(_leaderEnabled, "settings.systemwide_keys.leader_enabled", "settings.systemwide_keys.leader_enabled_help", _draft.LeaderEnabled);
            root.Children.Add(_leaderEnabled);
            BuildToggle(_transmitEnabled, "settings.systemwide_keys.transmit_enabled", "settings.systemwide_keys.transmit_enabled_help", _draft.TransmitKeysEnabled);
            _transmitEnabled.Margin = new Thickness(0, 0, 0, 12);
            root.Children.Add(_transmitEnabled);

            foreach (var role in SystemWideKeySet.AllRoles)
                root.Children.Add(BuildRole(role));

            _status.TextWrapping = TextWrapping.Wrap;
            _status.Margin = new Thickness(0, 8, 0, 8);
            AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
            root.Children.Add(_status);

            var reset = MakeButton(Lexicon.Get("settings.systemwide_keys.reset"), ResetToDefaults);
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            reset.Margin = new Thickness(0);
            JJFlexHelp.SetText(reset, Lexicon.Get("settings.systemwide_keys.reset_help"));
            root.Children.Add(reset);

            Content = root;
        }

        /// <summary>True while a key capture is waiting for the operator's chord.</summary>
        public bool Capturing => _capturing != null;

        /// <summary>
        /// True when the draft differs from what was loaded, so a host can say
        /// whether anything here changed when it commits.
        /// </summary>
        public bool IsDirty => Draft() != _loaded;

        // ── layout ───────────────────────────────────────────────────────

        private static void BuildToggle(CheckBox box, string key, string helpKey, bool checkedNow)
        {
            box.Content = Lexicon.Get(key);
            box.IsChecked = checkedNow;
            box.Margin = new Thickness(0, 0, 0, 6);
            AutomationProperties.SetName(box, Lexicon.Get(key));
            JJFlexHelp.SetText(box, Lexicon.Get(helpKey));
        }

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
                    Tracing.TraceLine("SystemWideKeysPanel: " + label + " failed: " + ex.Message, TraceLevel.Error);
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

        /// <summary>
        /// The host's tunnelling key handler calls this first. Returns true
        /// when a capture consumed the key — including Escape, which cancels
        /// the capture rather than closing the host.
        /// </summary>
        public bool HandleCaptureKey(KeyEventArgs e)
        {
            if (_capturing is not SystemWideRole role) return false;

            // While capturing, every key is ours.
            e.Handled = true;

            var raw = e.Key == Key.System ? e.SystemKey : e.Key;
            if (raw is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.Apps)
            {
                return true;   // a modifier on its way down; keep waiting for the key
            }

            if (raw == Key.Escape)
            {
                _capturing = null;
                Say(Lexicon.Get("settings.systemwide_keys.capture_cancelled"));
                return true;
            }

            if (raw == Key.Delete)
            {
                _capturing = null;
                _draft = _draft.With(role, WinFormsKeys.None);
                RefreshRoleLabel(role);
                _reports[role].Text = string.Empty;
                Say(Lexicon.Get("settings.systemwide_keys.removed", ("role", SystemWideKeys.RoleName(role))));
                return true;
            }

            var chord = WpfKeyConverter.ToWinFormsKeys(e);
            var verdict = SystemWideChord.Judge(chord);
            switch (verdict)
            {
                case SystemWideChordVerdict.NoKey:
                case SystemWideChordVerdict.ModifierOnly:
                    Say(Lexicon.Get("settings.systemwide_keys.key_unusable"));
                    return true;
                case SystemWideChordVerdict.FewerThanTwoModifiers:
                    Say(Lexicon.Get("settings.systemwide_keys.needs_two_modifiers",
                        ("chord", KeyManifest.FormatKey(chord))));
                    return true;
            }

            // Every role's chord counts here, switched off or not: a chord
            // parked on a disabled role would collide the day it is switched
            // back on.
            var taken = _draft.RoleOf(chord);
            if (taken != null && taken.Value != role)
            {
                Say(Lexicon.Get("settings.systemwide_keys.duplicate",
                    ("chord", KeyManifest.FormatKey(chord)),
                    ("other", SystemWideKeys.RoleName(taken.Value))));
                return true;
            }

            _capturing = null;
            _draft = _draft.With(role, chord);
            RefreshRoleLabel(role);
            string assigned = Lexicon.Get("settings.systemwide_keys.assigned",
                ("role", SystemWideKeys.RoleName(role)), ("chord", KeyManifest.FormatKey(chord)));
            string report = BuildReport(role, chord);
            _reports[role].Text = report;
            Say(assigned + " " + report);
            return true;
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
            Tracing.TraceLine("SystemWideKeysPanel: RegisterHotKey probe of " + name + " says " + verdict
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
                Tracing.TraceLine("SystemWideKeysPanel: in-app conflict lookup failed: " + ex.Message, TraceLevel.Error);
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

        // ── reset, commit ────────────────────────────────────────────────

        private void ResetToDefaults()
        {
            _draft = Draft() with
            {
                Leader = SystemWideKeySet.DefaultLeader,
                PushToTalk = SystemWideKeySet.DefaultPushToTalk,
                TransmitLock = SystemWideKeySet.DefaultTransmitLock,
            };
            foreach (var role in SystemWideKeySet.AllRoles)
            {
                RefreshRoleLabel(role);
                _reports[role].Text = string.Empty;
            }
            Say(Lexicon.Get("settings.systemwide_keys.reset_done"));
        }

        /// <summary>The draft with the two switches read from their boxes.</summary>
        private SystemWideKeySet Draft() => _draft with
        {
            LeaderEnabled = _leaderEnabled.IsChecked == true,
            TransmitKeysEnabled = _transmitEnabled.IsChecked == true,
        };

        /// <summary>
        /// Save what is on screen and put it in force, live. Returns the one
        /// sentence to tell the operator what happened, or null when nothing
        /// here changed — a host that lists what it applied adds it, a host
        /// that speaks for itself may ignore it.
        /// </summary>
        public string? Commit()
        {
            _capturing = null;
            var draft = Draft();
            if (draft == _loaded) return null;

            _config.FromKeySet(draft);
            bool saved = _config.Save(_configDirectory);

            // The file may have repaired something on the way out (it cannot,
            // from this panel, but Sanitize is the authority); apply what was
            // actually written.
            var applied = _config.ToKeySet();
            SystemWideKeys.Apply(applied);
            _draft = applied;
            foreach (var role in SystemWideKeySet.AllRoles) RefreshRoleLabel(role);

            string outcome = !saved
                ? Lexicon.Get("settings.systemwide_keys.save_failed")
                : applied.Enabled
                    ? Lexicon.Get("settings.systemwide_keys.saved")
                    : Lexicon.Get("settings.systemwide_keys.saved_off");
            _status.Text = outcome;
            return outcome;
        }

        private void Say(string sentence)
        {
            _status.Text = sentence;
            ScreenReaderOutput.Speak(sentence, Radios.Speech.SpeechIntent.Interrupt,
                VerbosityLevel.Critical, subject: Radios.Speech.SpeechSubject.SystemWideKeyPicker);
        }
    }
}
