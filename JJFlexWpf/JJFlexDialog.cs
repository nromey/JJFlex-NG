using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace JJFlexWpf
{
    /// <summary>
    /// Base class for all JJFlexRadio WPF dialogs.
    /// Provides standard behavior: ESC to close, focus management,
    /// accessibility defaults, and consistent styling.
    /// </summary>
    public class JJFlexDialog : Window
    {
        /// <summary>
        /// Callback invoked after a dialog closes to announce focus-return context.
        /// Set by MainWindow to speak compact status (e.g., "Slice A, 14.175, USB").
        /// </summary>
        public static Action? FocusReturnCallback { get; set; }

        /// <summary>
        /// Where every dialog gets its owner window. Installed once by the host
        /// at startup (the WinForms shell, in ApplicationEvents) and asked by
        /// each dialog as it is constructed; returns <c>0</c> while the shell
        /// has no window handle yet, and the dialog is then simply unowned.
        ///
        /// <para><b>This replaced a guess.</b> Until Sprint 45 Track A the
        /// constructor used <c>Process.MainWindowHandle</c>, which means "the
        /// first visible, unowned top-level window of the process". On the menu
        /// route that happened to be the shell. On the launch route the shell
        /// is not yet visible, and the front door now constructs the radio
        /// picker while "Searching for radios" is still on screen - so the
        /// heuristic would have returned the searching window, and Windows
        /// destroys owned windows with their owner: closing the searching
        /// window would have taken the picker down with it. Who owns a dialog
        /// is a decision the host makes, not a property the dialog infers from
        /// whatever happens to be visible.</para>
        /// </summary>
        public static Func<nint>? OwnerHandleProvider { get; set; }

        private static nint ResolveOwnerHandle()
        {
            var provider = OwnerHandleProvider;
            if (provider != null)
            {
                try { return provider(); }
                catch { return nint.Zero; }
            }

            // No host installed a provider - the test harnesses realise dialogs
            // without one. The old heuristic is kept for exactly that case, so
            // their behaviour is unchanged.
            try { return System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; }
            catch { return nint.Zero; }
        }

        public JJFlexDialog()
        {
            // Load shared dialog styles
            var styles = new ResourceDictionary();
            styles.Source = new Uri("pack://application:,,,/JJFlexWpf;component/Styles/DialogStyles.xaml");
            Resources.MergedDictionaries.Add(styles);

            // Center on parent window
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // Standard dialog chrome
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;

            // Tab wraps at both ends. WPF's default (Continue) stops dead at
            // the edges of the tab order, so Shift+Tab from the first control
            // went nowhere and content late in the order was effectively
            // unreachable backward — found at the keyboard 2026-08-10 in the
            // radio selector, but inherited by every dialog built on this
            // base. A dialog is a cycle, not a corridor.
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);

            // Owned by the shell, for modality and centering. MainWindow is a
            // UserControl hosted in an ElementHost, so Application.Current.
            // MainWindow is null and WPF cannot find the owner itself; the host
            // says who it is through OwnerHandleProvider (see its remarks for
            // why this is no longer inferred from the process's window list).
            try
            {
                nint owner = ResolveOwnerHandle();
                if (owner != nint.Zero)
                    new WindowInteropHelper(this).Owner = owner;
            }
            catch { /* non-critical — dialog still works, just without modality lock */ }

            // Wire up standard events
            PreviewKeyDown += JJFlexDialog_PreviewKeyDown;
            Loaded += JJFlexDialog_Loaded;

            // The stranded-focus sentinel runs for the LIFE of the dialog,
            // not just its edges — see StartStrandedFocusSentinel.
            Loaded += (_, _) => StartStrandedFocusSentinel();
            Closed += (_, _) => StopStrandedFocusSentinel();
        }

        /// <summary>
        /// ESC closes the dialog with DialogResult = false.
        /// Subclasses can override for custom key handling but should call base.
        /// </summary>
        private void JJFlexDialog_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseWithResult(false);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Close the dialog, recording <paramref name="result"/> when the window
        /// is modal and simply closing when it is not. Use this instead of
        /// assigning <see cref="Window.DialogResult"/> directly.
        ///
        /// <para><b>Why it exists (#159).</b> WPF's DialogResult setter throws
        /// InvalidOperationException on any window that was not opened with
        /// ShowDialog(). A dialog cannot know how it was opened: ConnectingDialog
        /// is legitimately shown non-modally, and the Tier 1 accessibility suite
        /// realises every dialog with Show(). Several dialogs assigned
        /// DialogResult from their Loaded handler as an early exit — a WinForms
        /// idiom ported literally — and on 2026-08-20 and again on 2026-08-21
        /// the resulting throw during window realisation aborted the whole
        /// suite, the second time leaving Export Log and ATU Memories windows
        /// stranded on the operator's screen mid-session. The try/catch is the
        /// same guard the Escape handler above has carried since
        /// ConnectingDialog went non-modal; this puts it in one named place.</para>
        /// </summary>
        protected void CloseWithResult(bool result)
        {
            try { DialogResult = result; } catch (InvalidOperationException) { }
            Close();
        }

        /// <summary>
        /// On load: sync AutomationProperties.Name with Title,
        /// then focus the first interactive control.
        /// </summary>
        private void JJFlexDialog_Loaded(object sender, RoutedEventArgs e)
        {
            // Set automation name from title for screen readers
            // A dialog announcing itself is real speech arriving, which is
            // exactly what a progress voice exists to stand in for until it
            // does. Stop it HERE — at the announcement — rather than anywhere
            // earlier.
            //
            // It was in DiscoveringRadiosWindow's constructor for a few hours
            // on 2026-08-25 and that was wrong in the way that matters: the
            // window is CONSTRUCTED, then discovery runs for five and a half
            // seconds on the same thread, and only THEN is it shown. Measured
            // that morning — constructed at 1295 ms, spoke at 6899 ms. So the
            // voice fell silent one second in and the operator got the entire
            // wait in silence anyway, with zero repeats. Noel heard exactly
            // that: one line, then nothing.
            //
            // Here it also covers every other dialog for free, which is right:
            // whatever a progress voice was covering, a dialog opening has
            // superseded it.
            Radios.ProgressVoice.Stop("dialog announced: " + (Title ?? "(untitled)"));

            if (!string.IsNullOrEmpty(Title))
            {
                AutomationProperties.SetName(this, Title);

                // Speak the title explicitly - NVDA may read the focused control
                // instead of the window title.
                //
                // QUEUED, not interrupting. This one line is inherited by 74
                // dialogs, so as an interrupt it meant that opening ANY dialog
                // anywhere destroyed whatever was being spoken at that instant.
                // Confirmed victims included "The radio disconnected", the
                // update-check error, "Opening SmartLink login" and the connect
                // sequence itself - which is why a successful connect emitted
                // eight utterances and the operator heard about two.
                //
                // A dialog opening is the START of a series, never a supersession
                // of one. Surveyed and re-bucketed 2026-08-18.
                Radios.ScreenReaderOutput.Speak(
                    Title, Radios.Speech.SpeechIntent.Queue, Radios.VerbosityLevel.Terse);
            }

            // Focus first interactive control
            FocusFirstControl();
        }

        /// <summary>
        /// Take the foreground once the window is actually on screen.
        ///
        /// **Not at Loaded.** Loaded fires while ShowDialog is still bringing
        /// the window up, before it is visible, and SetForegroundWindow on a
        /// window that is not yet shown fails silently - which is exactly the
        /// silent refusal this exists to defeat. Reported 2026-08-18: the
        /// discovering window took focus correctly, the picker opening behind
        /// it did not, and the operator had to Alt-Tab to find it.
        ///
        /// The window-to-window transition is the hard case. When one of our
        /// windows closes and the next opens there is a moment with nothing of
        /// ours on screen, so the foreground escapes to whatever was behind us.
        /// ContentRendered runs after that has settled and after we are
        /// visible, which is the first point SetForegroundWindow can succeed.
        ///
        /// Activate() as well: the grab makes us the foreground WINDOW, and
        /// Activate makes WPF treat us as the active one - which is what makes
        /// keyboard focus inside the dialog stick rather than being restored to
        /// whatever WPF last remembered.
        /// </summary>
        protected override void OnContentRendered(System.EventArgs e)
        {
            base.OnContentRendered(e);

            Radios.WindowActivation.EnsureForeground(
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
            try { Activate(); } catch { }

            // Focus again now that we are genuinely active. The pass in Loaded
            // set logical focus on a window Windows had not activated yet, so
            // keyboard focus never landed - and on Alt-Tab the operator arrived
            // on whatever WPF fell back to, which in the reported case was the
            // network identity card at the BOTTOM of the dialog.
            FocusFirstControl();

            // Bank the sentinel's baseline now, not two seconds from now.
            // The #529 reclaim rule only fires for a foreground that was
            // verifiably ours earlier in this dialog's life; a theft in the
            // first interval would otherwise look like "never had it".
            StrandedFocusTick();
        }

        /// <summary>
        /// On close: schedule deferred focus-return context announcement.
        /// Uses ApplicationIdle priority so it fires after focus settles back to main window.
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);

            // Deferred context announcement — fires after focus returns to main window
            if (FocusReturnCallback != null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, FocusReturnCallback);
            }
        }

        /// <summary>
        /// Finds and focuses the first focusable interactive control in the dialog.
        /// Skips labels, group boxes, and other non-interactive elements.
        /// </summary>
        protected virtual void FocusFirstControl()
        {
            // MoveFocus will find the first focusable element in tab order
            MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }

        // ── Stranded-focus sentinel (#395 follow-on, 2026-08-30) ─────────
        //
        // Every repair this base class had fires at an EDGE — activation at
        // ContentRendered, the focus-return landing at close. A dialog left
        // OPEN and unattended has no edge, and on 2026-08-30 the operator hit
        // exactly that three times: the foreground escaped while Settings
        // (once) or the radio picker (twice) sat open, keys reached nothing,
        // the screen reader fell silent, and nothing in the application ever
        // looked again. One session recovered only when he stumbled into an
        // OS-level escape about 195 seconds later; the others only when the
        // connect scope's failsafe happened to run its landing at 120.
        //
        // So: while a dialog is open, look every couple of seconds. The
        // DECISION lives in Radios.StrandedFocusSentinel, pure and pinned by
        // Radios.Tests — most importantly the rule that a FOREIGN foreground
        // is never repaired over ON ITS OWN: an operator reading email while
        // the picker waits is a choice, and stealing the foreground back on
        // a timer would be worse than the outage. The two provable black
        // holes are repaired — no foreground window anywhere, or a foreground
        // of our own process whose thread has no focus window — and the
        // repair is to reactivate THIS dialog, the thing the operator left
        // open.
        //
        // Sprint 44 Track Q (#529) added the one foreign case that is NOT a
        // choice. On 2026-09-02 the Select Radio dialog sat open, idle, and
        // the foreground went to another process with no keystroke and no
        // click from the operator — Windows lets any process take it once
        // the foreground has been idle past the lock timeout — and this
        // sentinel watched it happen for the whole outage and, correctly
        // under its own rule, did nothing. The sample now carries the OS's
        // last-input time, and only a foreign foreground that arrived while
        // the operator was idle, over a MODAL of ours, from a window that is
        // not a security prompt, is taken back — and taken back with an
        // explanation spoken a beat later, because a silent recovery leaves
        // a blind operator with an unexplained outage and an unexplained
        // outage reads as a crash. That is the whole difference between this
        // and the 2026-08-30 stranded-keyboard class: same tick, same
        // debounce, one more piece of evidence.
        //
        // Per-instance on purpose: dialogs live on more than one thread (the
        // Connecting window pumps its own), so a shared static timer would
        // race. A dialog whose owner chain disabled it (a modal child is up)
        // stands down and lets the child's own sentinel do the looking.

        private System.Windows.Threading.DispatcherTimer? _strandedFocusTimer;
        private readonly Radios.StrandedFocusSentinel _strandedFocus = new();

        private void StartStrandedFocusSentinel()
        {
            if (_strandedFocusTimer != null)
            {
                _strandedFocusTimer.Start();
                return;
            }
            _strandedFocusTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = System.TimeSpan.FromMilliseconds(
                    Radios.StrandedFocusSentinel.CheckIntervalMs)
            };
            _strandedFocusTimer.Tick += (_, _) => StrandedFocusTick();
            _strandedFocusTimer.Start();
        }

        private void StopStrandedFocusSentinel()
        {
            _strandedFocusTimer?.Stop();
        }

        private void StrandedFocusTick()
        {
            try
            {
                if (!IsVisible) return;

                var own = new WindowInteropHelper(this).Handle;
                if (own == nint.Zero) return;

                // A modal child has this dialog Win32-disabled: the child owns
                // the operator, and its own sentinel is the one on watch.
                if (!NativeFocusProbe.IsWindowEnabled(own)) return;

                var fg = NativeFocusProbe.GetForegroundWindow();
                var sample = SampleDesktop(fg);
                switch (_strandedFocus.Decide(sample))
                {
                    case Radios.StrandedFocusSentinel.Verdict.ReactivateOverBlackHole:
                        JJTrace.Tracing.TraceLine(
                            $"JJFlexDialog: keyboard focus is stranded (no window taking input) "
                            + $"while '{Title}' sits open - reactivating it (#395 sentinel)",
                            System.Diagnostics.TraceLevel.Warning);
                        Reactivate(own);
                        break;

                    case Radios.StrandedFocusSentinel.Verdict.ReclaimFromForeignThief:
                        ReclaimFromThief(own, fg, sample);
                        break;

                    case Radios.StrandedFocusSentinel.Verdict.StandDownThiefPersists:
                        {
                            var thief = Radios.DesktopWindowCensus.Describe(fg);
                            JJTrace.Tracing.TraceLine(
                                $"JJFlexDialog: '{thief.ProcessName}' (class {thief.ClassName}, "
                                + $"'{thief.Title}') keeps taking the foreground from '{Title}' and "
                                + $"the operator has not touched anything since the last reclaim - "
                                + $"standing down until they do (#529 watchdog, "
                                + $"cap {Radios.StrandedFocusSentinel.MaxReclaimsPerIdleStretch})",
                                System.Diagnostics.TraceLevel.Warning);
                        }
                        break;
                }
            }
            catch (System.Exception ex)
            {
                JJTrace.Tracing.TraceLine(
                    $"JJFlexDialog: stranded-focus sentinel tick failed: {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
            }
        }

        private void Reactivate(nint own)
        {
            Radios.WindowActivation.EnsureForeground(own);
            try { Activate(); } catch { /* best effort */ }
            if (!IsKeyboardFocusWithin) FocusFirstControl();
        }

        /// <summary>
        /// The #529 repair: take the foreground back from another process
        /// that took it from an idle operator, record who it was, and
        /// explain — after the reader has announced the window it landed on.
        /// </summary>
        private void ReclaimFromThief(nint own, nint fg, in Radios.StrandedFocusSentinel.Sample sample)
        {
            var thief = Radios.DesktopWindowCensus.Describe(fg);
            long idleMs = sample.NowMs - sample.LastInputMs;
            long heldAgoMs = sample.NowMs - _strandedFocus.LastOursMs;
            JJTrace.Tracing.TraceLine(
                $"JJFlexDialog: the foreground was TAKEN from '{Title}' by pid {thief.ProcessId} "
                + $"'{thief.ProcessName}' (class {thief.ClassName}, title '{thief.Title}') - "
                + $"operator idle {idleMs / 1000}s, we last held it {heldAgoMs / 1000}s ago, "
                + $"our modal is up - reclaiming and announcing (#529 watchdog)",
                System.Diagnostics.TraceLevel.Warning);

            Radios.DesktopWindowCensus.NoteTheft(
                new Radios.ForegroundTheft(System.DateTime.Now, thief, Title ?? ""));

            Reactivate(own);
            ScheduleReclaimAnnouncement(thief);
        }

        private System.Windows.Threading.DispatcherTimer? _reclaimAnnounce;

        /// <summary>
        /// Speak the explanation a beat AFTER the grab. A screen reader
        /// flushes its queue on a foreground change and then announces the
        /// new window itself; a sentence spoken at the moment of the grab is
        /// destroyed by it (see memory: speech flushes on window change).
        /// Queued and Critical: it must follow the reader's own announcement
        /// in order, and it must be heard at any verbosity — the operator has
        /// just lived through an outage nothing explained.
        /// </summary>
        private void ScheduleReclaimAnnouncement(Radios.DesktopWindowRecord thief)
        {
            string text = Radios.DesktopWindowCensusSpeech.ReclaimAnnouncement(thief, Title ?? "");
            if (_reclaimAnnounce == null)
            {
                _reclaimAnnounce = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = System.TimeSpan.FromMilliseconds(
                        Radios.StrandedFocusSentinel.ReclaimAnnounceDelayMs)
                };
            }
            var timer = _reclaimAnnounce;
            timer.Stop();
            System.EventHandler? handler = null;
            handler = (_, _) =>
            {
                timer.Stop();
                timer.Tick -= handler;
                Radios.ScreenReaderOutput.Speak(text,
                    Radios.Speech.SpeechIntent.Queue,
                    Radios.VerbosityLevel.Critical,
                    subject: Radios.Speech.SpeechSubject.KeyboardReclaimed);
            };
            timer.Tick += handler;
            timer.Start();
        }

        /// <summary>
        /// Everything one tick knows, for <see cref="Radios.StrandedFocusSentinel.Decide"/>.
        /// The input clock and the tick clock are both milliseconds since
        /// boot, so "last input" lands on the same line as "now".
        /// </summary>
        private Radios.StrandedFocusSentinel.Sample SampleDesktop(nint fg)
        {
            var observation = ObserveDesktopFocus(fg);
            long now = System.Environment.TickCount64;
            int idle = Radios.DesktopWindowCensus.MillisecondsSinceLastInput();
            bool known = idle >= 0;

            bool foreignIsProtected = false;
            if (observation == Radios.StrandedFocusSentinel.Observation.ForeignForeground)
            {
                // A sign-in flow of ours legitimately hands the keyboard to
                // a browser or a credential prompt; and the system prompts —
                // by window class or by owning process (the lock screen
                // arrives with no input at all) — are never ours to take
                // from, whoever raised them.
                foreignIsProtected = Radios.WindowFocusForcer.SignInWindowOpen
                    || Radios.DesktopWindowCensus.IsProtectedForegroundWindow(fg);
            }

            return new Radios.StrandedFocusSentinel.Sample(
                observation,
                NowMs: now,
                LastInputMs: known ? now - idle : 0,
                InputEvidenceKnown: known,
                OurModalIsUp: OurModalIsUp(),
                ForeignIsProtected: foreignIsProtected);
        }

        /// <summary>
        /// True when this dialog is modal in Windows' own terms: its owner is
        /// disabled. That is the exact picture measured on 2026-09-02 — the
        /// selector enabled, the shell behind it enabled=False — and it is
        /// what makes a reclaim defensible: the operator has nothing else of
        /// ours to be using while this window is up.
        /// </summary>
        private bool OurModalIsUp()
        {
            var owner = new WindowInteropHelper(this).Owner;
            return owner != nint.Zero && !NativeFocusProbe.IsWindowEnabled(owner);
        }

        /// <summary>
        /// One look at the desktop, classified for the sentinel. Cross-thread
        /// correct: the focus question is asked of the FOREGROUND window's
        /// thread via GetGUIThreadInfo, not of whichever thread this dialog
        /// happens to run on.
        /// </summary>
        private static Radios.StrandedFocusSentinel.Observation ObserveDesktopFocus(nint fg)
        {
            if (fg == nint.Zero)
                return Radios.StrandedFocusSentinel.Observation.NoForegroundAnywhere;

            uint tid = NativeFocusProbe.GetWindowThreadProcessId(fg, out uint pid);
            if (pid != (uint)System.Environment.ProcessId)
                return Radios.StrandedFocusSentinel.Observation.ForeignForeground;

            var info = NativeFocusProbe.GuiThreadInfo.Create();
            if (NativeFocusProbe.GetGUIThreadInfo(tid, ref info)
                && info.hwndFocus == nint.Zero)
                return Radios.StrandedFocusSentinel.Observation.OursWithDeadFocus;

            return Radios.StrandedFocusSentinel.Observation.Healthy;
        }

        private static class NativeFocusProbe
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            internal static extern nint GetForegroundWindow();

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            [return: System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.Bool)]
            internal static extern bool IsWindowEnabled(nint hWnd);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            [return: System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.Bool)]
            internal static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);

            [System.Runtime.InteropServices.StructLayout(
                System.Runtime.InteropServices.LayoutKind.Sequential)]
            internal struct GuiThreadInfo
            {
                public int cbSize;
                public int flags;
                public nint hwndActive;
                public nint hwndFocus;
                public nint hwndCapture;
                public nint hwndMenuOwner;
                public nint hwndMoveSize;
                public nint hwndCaret;
                public Rect rcCaret;

                internal static GuiThreadInfo Create() => new()
                {
                    cbSize = System.Runtime.InteropServices.Marshal
                        .SizeOf<GuiThreadInfo>()
                };
            }

            [System.Runtime.InteropServices.StructLayout(
                System.Runtime.InteropServices.LayoutKind.Sequential)]
            internal struct Rect
            {
                public int Left, Top, Right, Bottom;
            }
        }

        /// <summary>
        /// Creates a standard OK/Cancel button panel.
        /// Call this from subclass constructors or XAML code-behind to add
        /// a consistent button row at the bottom of the dialog.
        /// </summary>
        /// <param name="okText">Text for the OK button (default "OK")</param>
        /// <param name="cancelText">Text for the Cancel button (default "Cancel")</param>
        /// <param name="onOk">Action to run when OK is clicked. If it returns without
        /// setting DialogResult, the dialog sets DialogResult = true and closes.</param>
        /// <param name="onCancel">Optional action for Cancel. Default just closes.</param>
        /// <returns>A StackPanel containing the buttons, ready to add to your layout.</returns>
        protected StackPanel CreateButtonPanel(
            Action? onOk = null,
            Action? onCancel = null,
            string? okText = null,
            string? cancelText = null)
        {
            okText ??= Radios.Lexicon.Get("connect.dialog.ok");
            cancelText ??= Radios.Lexicon.Get("connect.dialog.cancel");

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            string okAccessName = okText.Replace("_", "");
            var okButton = new Button
            {
                Content = okText,
                MinWidth = 80,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true  // Enter key triggers this
            };
            AutomationProperties.SetName(okButton, okAccessName);
            SetAccessKeyProperty(okButton, okText);
            okButton.Click += (s, e) =>
            {
                onOk?.Invoke();
                // If the onOk handler didn't set DialogResult, record it now.
                // Via CloseWithResult, so a dialog realised with Show() closes
                // instead of throwing — see that method's comment (#159).
                if (DialogResult == null)
                {
                    CloseWithResult(true);
                }
            };

            string cancelAccessName = cancelText.Replace("_", "");
            var cancelButton = new Button
            {
                Content = cancelText,
                MinWidth = 80,
                Height = 28,
                IsCancel = true  // ESC also triggers this (backup to PreviewKeyDown)
            };
            AutomationProperties.SetName(cancelButton, cancelAccessName);
            SetAccessKeyProperty(cancelButton, cancelText);
            cancelButton.Click += (s, e) =>
            {
                onCancel?.Invoke();
                if (DialogResult == null)
                {
                    CloseWithResult(false);
                }
            };

            panel.Children.Add(okButton);
            panel.Children.Add(cancelButton);

            return panel;
        }

        /// <summary>
        /// Creates a button panel with OK, Cancel, and Apply buttons.
        /// </summary>
        protected StackPanel CreateButtonPanelWithApply(
            Action? onOk = null,
            Action? onApply = null,
            Action? onCancel = null,
            string? okText = null,
            string? applyText = null,
            string? cancelText = null)
        {
            applyText ??= Radios.Lexicon.Get("connect.dialog.apply");

            var panel = CreateButtonPanel(onOk, onCancel, okText, cancelText);

            // Insert Apply button before Cancel
            string applyAccessName = applyText.Replace("_", "");
            var applyButton = new Button
            {
                Content = applyText,
                MinWidth = 80,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0)
            };
            AutomationProperties.SetName(applyButton, applyAccessName);
            SetAccessKeyProperty(applyButton, applyText);
            applyButton.Click += (s, e) =>
            {
                onApply?.Invoke();
                // Apply doesn't close — user stays in dialog
            };

            // Insert before the Cancel button (last child)
            panel.Children.Insert(panel.Children.Count - 1, applyButton);

            return panel;
        }

        /// <summary>
        /// Extract the access key letter from underscore-prefixed text (e.g. "_OK" → "Alt+O")
        /// and set both AccessKey and AcceleratorKey for screen reader compatibility.
        /// NVDA reads AcceleratorKey, JAWS reads the underscore access key from Content.
        /// </summary>
        private static void SetAccessKeyProperty(System.Windows.UIElement element, string text)
        {
            int idx = text.IndexOf('_');
            if (idx >= 0 && idx < text.Length - 1)
            {
                char key = char.ToUpper(text[idx + 1]);
                string combo = $"Alt+{key}";
                AutomationProperties.SetAccessKey(element, combo);
                AutomationProperties.SetAcceleratorKey(element, combo);
            }
        }

        /// <summary>
        /// Helper to show this dialog modally and return the result.
        /// Wraps ShowDialog() with standard error handling.
        /// </summary>
        public bool? ShowModalDialog()
        {
            // #331. Every modal of ours claims the operator's attention while
            // it is up, and the claim is made HERE because this is the one
            // chokepoint every WPF dialog in the app goes through — one place
            // to state the rule instead of one per dialog, none of which would
            // be remembered by the next author.
            //
            // What the claim buys: ConnectingForm's 200 ms focus-reclaim timer
            // stands down and drops its TopMost, so a dialog raised during a
            // connect can actually be reached. Before this, a modal error box
            // raised by a mid-connect disconnect sat underneath a top-most
            // window that re-activated itself five times a second — which for a
            // blind operator is an application that is unusable and
            // unexplainable at the same time.
            Radios.WindowFocusForcer.PushAttentionWindow();
            try
            {
                return ShowDialog();
            }
            catch (InvalidOperationException)
            {
                // Can happen if window was already closed or owner is invalid
                return false;
            }
            finally
            {
                Radios.WindowFocusForcer.PopAttentionWindow();
            }
        }
    }
}
