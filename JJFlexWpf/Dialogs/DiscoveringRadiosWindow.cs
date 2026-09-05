using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using JJTrace;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Holds the operator for the moment discovery needs, so that the radio
    /// selector opens onto a SETTLED list instead of one that assembles itself
    /// while they listen to it.
    ///
    /// **The problem this exists to remove.** A sighted operator never
    /// experiences discovery churn: rows appear, text updates, and they glance
    /// once at the end. Every one of those updates is an event a screen reader
    /// may voice, so the churn that is invisible on screen IS the experience
    /// through speech. On 2026-08-18 that came out as "no radios online" then
    /// "1 radio online" a second later - the dialog correcting itself out loud,
    /// having reported a guess in the grammar of a fact.
    ///
    /// Rewording the churn ("discovering", "checking") only narrates it more
    /// accurately. Settling first removes it: there is nothing to narrate,
    /// because nothing changes after the operator arrives.
    ///
    /// **Deliberately not a progress bar.** There is one line, it does not
    /// update, and it is spoken once. A window that reports its own progress
    /// would reintroduce exactly the churn it exists to absorb.
    ///
    /// Escape skips the wait - the picker can still be met mid-discovery by
    /// anyone who would rather not wait, and it degrades to the old behaviour
    /// rather than to a wrong one.
    ///
    /// **Shown with <see cref="ShowAndWaitForSettle"/>, and it does not close
    /// itself** (Sprint 45 Track A). It used to be a ShowDialog that closed on
    /// settle, and the picker was constructed only after it had gone - so for
    /// 155 ms the process owned no visible window and the foreground fell to
    /// File Explorer, which the screen reader began to announce. Now the wait
    /// returns with this window still on screen, the caller brings the picker
    /// up over it, and <see cref="WindowHandoff"/> closes this one once the
    /// picker has rendered. The foreground goes from one window of ours to the
    /// next and never through the desktop.
    /// </summary>
    public sealed class DiscoveringRadiosWindow : JJFlexDialog
    {
        /// <summary>Hard ceiling on the wait, however discovery is going.</summary>
        ///
        /// Two seconds is the operator's own estimate of when local churn
        /// settles, and it is a CEILING rather than a fixed delay - a radio
        /// that answers promptly ends the wait well before this.
        private const int MaxWaitMs = 2000;

        /// <summary>Quiet period after the last sighting before we call it settled.</summary>
        ///
        /// A LAN radio re-announces roughly once a second, so this is
        /// deliberately shorter than that: we want "nothing NEW has arrived",
        /// not "the radio has gone quiet", which it never does.
        private const int QuietMs = 400;

        private const int PollMs = 100;

        private DateTime _lastSighting = DateTime.MinValue;
        private volatile bool _anySeen;
        private bool _skipped;

        /// <summary>The pump behind <see cref="ShowAndWaitForSettle"/> -
        /// non-null exactly while the caller is waiting on us.</summary>
        private DispatcherFrame? _waitFrame;

        /// <summary>True once <see cref="ShowAndWaitForSettle"/> has been
        /// called: from then on the CALLER closes this window, never us.</summary>
        private bool _handoffMode;

        /// <summary>True once the wait has been ended, by settle, ceiling or
        /// skip. Anything that arrives after that is ignored.</summary>
        private bool _waitEnded;

        /// <param name="lead">
        /// Something to say BEFORE "Searching for radios" - typically what just
        /// happened, such as a disconnect.
        ///
        /// It has to be carried by this window rather than spoken before it,
        /// because a screen reader flushes on window change: an utterance made
        /// a moment earlier is destroyed by this window opening, whether it was
        /// queued OR interrupting. That killed the disconnect announcement
        /// three separate ways on 2026-08-18 before the mechanism was the
        /// problem rather than the timing.
        ///
        /// Folded into the TITLE rather than spoken separately, so it arrives
        /// as part of the window's own announcement and cannot be cut by it.
        /// </param>
        public DiscoveringRadiosWindow(string? lead = null)
        {

            // The title IS the message. It was "JJ Flexible", which meant the
            // operator heard the application's name - which they already knew,
            // having just launched it - and then the actual message from the
            // body text. A window that exists for one second to say one thing
            // should say that thing in its name.
            Title = string.IsNullOrWhiteSpace(lead)
                ? "Searching for radios"
                : lead + ". Searching for radios";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Visible for a sighted operator, silent to a screen reader: the
            // title already said it, and static text is read as dialog body on
            // open, so a plain TextBlock here would say it a second time.
            Content = new Controls.DecorativeText
            {
                Text = "Searching for radios",
                Margin = new Thickness(24, 18, 24, 18),
                FontSize = 14,
            };

            // The window itself is the focus target. Its only child is
            // DecorativeText, which is deliberately absent from the UIA tree,
            // so FocusFirstControl found nothing focusable and NVDA had no
            // focused object to describe - it announced "unknown". Reported
            // 2026-08-18.
            //
            // A window that exists to say one sentence does not need a control
            // to say it: the title carries the message, and focusing the window
            // gives the screen reader something real to report.
            Focusable = true;

            Loaded += async (_, _) => await WaitForSettleAsync();
        }

        /// <summary>
        /// True when the operator pressed Escape (or closed the window) rather
        /// than letting discovery settle. The caller may want to know it is
        /// opening the picker early.
        /// </summary>
        public bool Skipped => _skipped;

        /// <summary>
        /// Show the window and return when discovery has settled, the ceiling
        /// has passed, or the operator has skipped - WITH THE WINDOW STILL ON
        /// SCREEN. The caller brings the next window up and then closes this
        /// one; <see cref="WindowHandoff.CloseAfterSuccessorShown"/> does both
        /// halves of that in the right order.
        ///
        /// <para>Pumps a dispatcher frame, exactly as ShowDialog does, so the
        /// UI stays live throughout. Not modal: the shell stays enabled for the
        /// two seconds at most this holds, on purpose - a disabled shell cannot
        /// be activated, so if anything DID go wrong in the hand-off the
        /// foreground would fall past it to another application, whereas an
        /// enabled shell is a window of ours for it to land on.</para>
        /// </summary>
        public void ShowAndWaitForSettle()
        {
            if (_waitFrame != null)
                throw new InvalidOperationException("ShowAndWaitForSettle is already running.");

            _handoffMode = true;
            var frame = new DispatcherFrame();
            _waitFrame = frame;

            // Closed by anything at all while we pump - an exception, a caller
            // giving up - and the wait is over too. Nobody may sit in a frame
            // for a window that no longer exists.
            EventHandler onClosed = (_, _) => frame.Continue = false;
            Closed += onClosed;
            try
            {
                Show();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            finally
            {
                Closed -= onClosed;
                _waitFrame = null;
            }
        }

        /// <summary>
        /// Focus the window itself - there is no control to focus, by design.
        /// </summary>
        protected override void FocusFirstControl()
        {
            Focus();
            System.Windows.Input.Keyboard.Focus(this);
        }

        /// <summary>
        /// While the caller is waiting on us, a close from the operator -
        /// Escape (the base class turns it into Close()), the X, Alt+F4 - is a
        /// SKIP, not a close. The window stays until the picker is up; closing
        /// it here would open the very gap this window's hand-off exists to
        /// remove. One funnel for every way of asking to leave early.
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (_waitFrame != null && _waitFrame.Continue)
            {
                e.Cancel = true;
                _skipped = true;
                SettleReached("skipped by the operator, picker opening early.");
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            Radios.FlexBase.RadioFound -= OnRadioFound;
            base.OnClosed(e);
        }

        private void OnRadioFound(object sender, Radios.FlexBase.RigData r)
        {
            // Raised from the discovery thread. Nothing here touches the UI -
            // the poll loop below reads these and it owns the dispatcher.
            _anySeen = true;
            _lastSighting = DateTime.UtcNow;
        }

        private async System.Threading.Tasks.Task WaitForSettleAsync()
        {
            Radios.FlexBase.RadioFound += OnRadioFound;

            var deadline = DateTime.UtcNow.AddMilliseconds(MaxWaitMs);
            while (DateTime.UtcNow < deadline)
            {
                if (_waitEnded) return;   // Skipped - nothing more to decide.

                // Settled: something answered, and nothing new has arrived for
                // a beat. Waiting the full ceiling once the answer is in would
                // just be a delay the operator can hear and cannot explain.
                if (_anySeen
                    && _lastSighting != DateTime.MinValue
                    && (DateTime.UtcNow - _lastSighting).TotalMilliseconds >= QuietMs)
                {
                    SettleReached("settled early, radios answered.");
                    return;
                }

                await System.Threading.Tasks.Task.Delay(PollMs);
                if (!IsLoaded) return;   // Closed mid-wait.
            }

            // Nothing answered, or it never went quiet. Either is a real
            // answer and the picker will say so - the point of waiting was to
            // be able to say it ONCE, instead of guessing and then retracting.
            SettleReached(_anySeen
                ? "ceiling reached, radios still announcing."
                : "ceiling reached, nothing found.");
        }

        /// <summary>
        /// The wait is over. In hand-off mode that means releasing the caller's
        /// frame and STAYING PUT; otherwise - shown some other way, as the
        /// accessibility harness and any leftover ShowDialog caller do - it
        /// means what it always meant, which is closing.
        /// </summary>
        private void SettleReached(string how)
        {
            if (_waitEnded) return;
            _waitEnded = true;
            Tracing.TraceLine("DiscoveringRadios: " + how, TraceLevel.Info);

            if (_waitFrame != null)
            {
                _waitFrame.Continue = false;
                return;
            }
            if (_handoffMode) return;   // The frame already exited; the caller owns the close.

            CloseWithResult(true);
        }
    }
}
