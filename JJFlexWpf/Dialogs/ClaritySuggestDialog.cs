using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JJPortaudio;
using Radios;
using Radios.Speech;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// JJ Clarity's "listen to my voice and suggest" pass (Sprint 49 Track J
    /// phase 2, J7, #736): it measures the newest off-air take, says what it
    /// found and proposes, lets the operator hear their own voice before and
    /// after, and changes NOTHING unless Keep is pressed (never auto-apply,
    /// #262). Every word is a DRAFT PROSE key for Codex. Its logic is the
    /// windowless <see cref="ClaritySuggestFlow"/>; the window itself is the
    /// harness walk's to press.
    /// </summary>
    /// <remarks>
    /// <para><b>Every way out but Keep changes nothing</b>, because nothing is
    /// written until Keep: Discard, Escape, the title bar's close and Alt+F4
    /// all leave the transmit setup exactly as it was. Adjust hands the offer
    /// to JJ Clarity's editor, which takes it back on every exit but its own
    /// Close (its put-back hangs on its window closing).</para>
    /// <para><b>It never keys.</b> It reads recordings already made; the only
    /// sound it makes is playing the two renders when asked.</para>
    /// </remarks>
    public sealed class ClaritySuggestDialog : JJFlexDialog
    {
        /// <summary>The source every change this window makes carries.</summary>
        public const string Source = "clarity suggest";

        private readonly NoiseController? _noise;
        private readonly FlexBase? _rig;
        private readonly ClaritySuggestFlow? _flow;
        private readonly string _folder;
        private readonly TextBox _report = new()
        {
            IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 220,
        };
        private readonly Button _before, _after, _keep, _adjust, _check, _discard;
        private readonly CancellationTokenSource _cancel = new();
        private ClaritySuggestFlow.Outcome? _outcome;
        private (string Before, string After)? _files;
        private bool _closing;

        /// <summary>Set when Adjust was pressed: the offer to open in JJ Clarity's editor once this window has closed.</summary>
        public ClaritySettings? AdjustRequested { get; private set; }

        /// <summary>Set when "Check your audio" was pressed: open it once this window has closed, for a new take and Compare.</summary>
        public bool CheckYourAudioRequested { get; private set; }

        /// <summary>A detached window (nothing read, nothing run): the dialog harness's road.</summary>
        public ClaritySuggestDialog() : this(null, null, null, ClaritySuggestFlow.DefaultFolder) { }

        /// <summary>The app's window: the pass on the real takes and the configured chain's twin.</summary>
        public ClaritySuggestDialog(NoiseController noise, FlexBase? rig)
            : this(noise, rig, new ClaritySuggestFlow(new ClaritySuggestFlow.Sources
            {
                Twin = () => ChainAuditionTwin.ForApp(() => ChainAuditionTwin.ReadLive(rig)),
            }), ClaritySuggestFlow.DefaultFolder) { }

        internal ClaritySuggestDialog(NoiseController? noise, FlexBase? rig, ClaritySuggestFlow? flow, string folder)
        {
            _noise = noise;
            _rig = rig;
            _flow = flow;
            _folder = folder;
            Title = Lexicon.Get("audio.clarity_suggest.title");
            Width = 640; Height = 520;
            ResizeMode = ResizeMode.CanResize;

            var root = new DockPanel { Margin = new Thickness(12) };
            var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            _before = Button("audio.clarity_suggest.hear_before", "audio.clarity_help.suggest.hear_before", () => Play(before: true));
            _after = Button("audio.clarity_suggest.hear_after", "audio.clarity_help.suggest.hear_after", () => Play(before: false));
            _keep = Button("audio.clarity_suggest.keep", "audio.clarity_help.suggest.keep", Keep);
            _adjust = Button("audio.clarity_suggest.adjust", "audio.clarity_help.suggest.adjust", () =>
            {
                if (_outcome?.Offer is not { } offer) return;
                AdjustRequested = offer.Proposed;
                Close();
            });
            _check = Button("audio.clarity_suggest.open_check", "audio.clarity_help.suggest.open_check", () => { CheckYourAudioRequested = true; Close(); });
            _discard = Button("audio.clarity_suggest.discard", "audio.clarity_help.suggest.discard", Close);
            _discard.IsCancel = true;
            foreach (var b in new[] { _before, _after, _keep, _adjust, _check, _discard }) buttons.Children.Add(b);
            root.Children.Add(buttons);

            string label = Lexicon.Get("audio.clarity_suggest.report_label");
            AutomationProperties.SetName(_report, label);
            JJFlexHelp.SetText(_report, Lexicon.Get("audio.clarity_help.suggest.report"));
            var reportPanel = new DockPanel();
            var heading = new Label { Content = label, Target = _report, Padding = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(heading, Dock.Top);
            reportPanel.Children.Add(heading);
            reportPanel.Children.Add(_report);
            root.Children.Add(reportPanel);
            Content = root;

            Sync();
            Loaded += (_, _) => Begin();
            Closing += (_, _) => End();
        }

        private static Button Button(string key, string helpKey, Action click)
        {
            string text = Lexicon.Get(key);
            var b = new Button { Content = text, MinWidth = 110, Height = 28, Margin = new Thickness(0, 4, 8, 6) };
            AutomationProperties.SetName(b, text);
            JJFlexHelp.SetText(b, Lexicon.Get(helpKey));
            b.Click += (_, _) => click();
            return b;
        }

        /// <summary>Which buttons can act now: the two renders and Keep and Adjust only with an offer.</summary>
        private void Sync()
        {
            bool offer = _outcome?.CanKeep == true && _noise != null;
            _before.IsEnabled = _after.IsEnabled = offer && _files != null;
            _keep.IsEnabled = _adjust.IsEnabled = offer;
        }

        /// <summary>The window is up: clear last time's renders and run the pass on a worker.</summary>
        private void Begin()
        {
            if (_flow == null) return;
            ChainAuditionFolder.Clear(_folder);
            Say(Lexicon.Get("audio.clarity_suggest.working"));
            _report.Text = Lexicon.Get("audio.clarity_suggest.working");
            var token = _cancel.Token;
            Task.Run(() => _flow.Run(token, null), token).ContinueWith(task =>
            {
                if (_closing) return;
                if (task.IsCanceled || task.Exception?.GetBaseException() is OperationCanceledException) return;
                if (task.IsFaulted)
                {
                    System.Diagnostics.Trace.WriteLine("ClaritySuggestDialog: the pass failed: " + task.Exception?.GetBaseException().GetType().Name
                        + ": " + task.Exception?.GetBaseException().Message);
                    ShowReport(new[] { Lexicon.Get("audio.clarity_suggest.failed"), Lexicon.Get("audio.clarity_suggest.changes_nothing") });
                    return;
                }
                _outcome = task.Result;
                if (_outcome.CanKeep)
                {
                    try { _files = ClaritySuggestFlow.WriteRenders(_outcome, _folder); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine("ClaritySuggestDialog: the renders could not be written: " + ex.GetType().Name);
                        _files = null;
                    }
                }
                ShowReport(ClaritySuggestFlow.Report(_outcome));
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Show and say the pass's sentences, and put the reader on them.</summary>
        private void ShowReport(IReadOnlyList<string> sentences)
        {
            _report.Text = string.Join(Environment.NewLine, sentences);
            Sync();
            Say(string.Join(" ", sentences));
            _report.Focus();
            _report.CaretIndex = 0;
        }

        private static void Say(string text) =>
            ScreenReaderOutput.Speak(text, SpeechIntent.Queue, VerbosityLevel.Terse, subject: SpeechSubject.ClaritySuggest);

        /// <summary>Play one render, or stop it when it is the one playing.</summary>
        private void Play(bool before)
        {
            if (_files is not { } files) return;
            string path = before ? files.Before : files.After;
            if (RecordingPlayer.IsPlaying && string.Equals(RecordingPlayer.CurrentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                RecordingPlayer.Stop();
                return;
            }
            RecordingPlayer.Play(path, Lexicon.Get(before ? "audio.clarity_suggest.playing_before" : "audio.clarity_suggest.playing_after"), _rig);
        }

        /// <summary>Keep: the offer becomes JJ Clarity's setting (the controller says so, and that it waits for the unkey when keyed). The one thing here that changes anything.</summary>
        private void Keep()
        {
            if (_noise == null || _outcome?.Offer is not { } offer) return;
            StopOurPlayback();
            _noise.KeepClaritySuggestion(offer.Proposed, Source);
            // Kept once: nothing is left to keep or adjust in this pass.
            _outcome = null;
            _report.Text = Lexicon.Get("audio.clarity_suggest.next_step");
            Sync();
            Say(_report.Text);
            if (_keep.IsKeyboardFocusWithin || _adjust.IsKeyboardFocusWithin) _check.Focus();
        }

        private void StopOurPlayback()
        {
            if (_files is not { } files || !RecordingPlayer.IsPlaying) return;
            string playing = RecordingPlayer.CurrentPath ?? "";
            if (string.Equals(playing, files.Before, StringComparison.OrdinalIgnoreCase)
                || string.Equals(playing, files.After, StringComparison.OrdinalIgnoreCase))
                RecordingPlayer.StopWithoutSpeaking();
        }

        /// <summary>The window is closing, by any road: stop the worker and a render of ours that is playing, and empty the renders' folder. Nothing is put back, because nothing was changed except by Keep.</summary>
        private void End()
        {
            _closing = true;
            try { _cancel.Cancel(); } catch (ObjectDisposedException) { }
            StopOurPlayback();
            if (_flow != null) ChainAuditionFolder.Clear(_folder);
        }
    }
}
