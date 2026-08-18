using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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

        public DiscoveringRadiosWindow()
        {
            // The title IS the message. It was "JJ Flexible", which meant the
            // operator heard the application's name - which they already knew,
            // having just launched it - and then the actual message from the
            // body text. A window that exists for one second to say one thing
            // should say that thing in its name.
            Title = "Discovering radios";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Visible for a sighted operator, silent to a screen reader: the
            // title already said it, and static text is read as dialog body on
            // open, so a plain TextBlock here would say it a second time.
            Content = new Controls.DecorativeText
            {
                Text = "Discovering radios",
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
        /// True when the operator pressed Escape rather than letting discovery
        /// settle. The caller may want to know it is opening the picker early.
        /// </summary>
        public bool Skipped => _skipped;

        /// <summary>
        /// Focus the window itself - there is no control to focus, by design.
        /// </summary>
        protected override void FocusFirstControl()
        {
            Focus();
            System.Windows.Input.Keyboard.Focus(this);
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
                // Settled: something answered, and nothing new has arrived for
                // a beat. Waiting the full ceiling once the answer is in would
                // just be a delay the operator can hear and cannot explain.
                if (_anySeen
                    && _lastSighting != DateTime.MinValue
                    && (DateTime.UtcNow - _lastSighting).TotalMilliseconds >= QuietMs)
                {
                    Tracing.TraceLine(
                        "DiscoveringRadios: settled early, radios answered.",
                        TraceLevel.Info);
                    break;
                }

                await System.Threading.Tasks.Task.Delay(PollMs);
                if (!IsLoaded) return;   // Escape closed us mid-wait.
            }

            if (!_anySeen)
            {
                // Nothing answered. That is a real answer and the picker will
                // say so - the point of waiting was to be able to say it ONCE,
                // instead of guessing and then retracting.
                Tracing.TraceLine(
                    "DiscoveringRadios: ceiling reached, nothing found.",
                    TraceLevel.Info);
            }

            try { DialogResult = true; } catch (InvalidOperationException) { }
            Close();
        }

        /// <summary>
        /// Escape skips the wait. Recorded so the caller can tell a deliberate
        /// skip from a settle.
        /// </summary>
        protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape) _skipped = true;
            base.OnKeyDown(e);
        }
    }
}
