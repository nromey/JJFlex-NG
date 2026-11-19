using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Radios;
using Radios.Speech;

namespace JJFlexWpf
{
    /// <summary>
    /// One run of the Audio Workshop's engine speed test as the operator hears
    /// and reads it: "started", one line per engine and pairing as each is
    /// ready, the notes, then "finished" (Sprint 49 Track L, #866; repaired by
    /// Track L2 after the 2026-11-19 integration-day bench). Windowless: the
    /// dialog hands it the result box's setter and its dispatcher.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every spoken line is heard (bench finding 1).</b> The lines share
    /// <see cref="SpeechSubject.EngineSpeedTest"/>, and a queued utterance that
    /// restates its subject retires every unheard one before it, so on the
    /// bench each line took the one before it out of the delivery queue and
    /// NVDA said 3 of 19, "finished" not among them. Each line ADDS to the
    /// test's subject instead (<c>additive</c>, the delivery the check's
    /// countdown uses for the same reason: no piece retires another), so every
    /// line is said, queued, in turn. Only "started", the answer to a fresh
    /// press, restates the subject; "busy" adds, so pressing Start again never
    /// takes back a line not yet said.
    /// </para>
    /// <para>
    /// <b>One road, in order (bench finding 2).</b> The helper lines and the
    /// helper pairings came AFTER "finished". The engine lines were posted to
    /// the dispatcher at normal priority, while the tail was written in the
    /// click handler's await continuation, which WPF resumes at the priority the
    /// click ran at, ahead of everything still queued at normal. So every line,
    /// the tail and the end of the run included, now leaves the test's own
    /// thread through one normal-priority post, in the order it was made.
    /// </para>
    /// <para>
    /// <b>The run's lines are one <see cref="SpokenSeries"/> (repair 1, Codex's
    /// check of 979bfa1f).</b> The series owns every spoken line after
    /// "started" until the reader has said it or it is taken back, which is
    /// longer than the test runs: the test ends in seconds and its lines take
    /// much longer to say. So closing the window takes back what is unsaid
    /// whether the test is still running or not (B1); a list longer than the
    /// arbiter's 16-entry ledger loses nothing to that cap, because the series
    /// hands over a few lines at a time (B2); and after any interrupt, "busy"
    /// or anyone else's, nothing new is handed over until the lines the
    /// interrupt rescued are back in the reader's queue, so "finished" follows
    /// every line that survived (B3). "started" and "busy" are not part of the
    /// series: each is an interrupt, said at once.
    /// </para>
    /// <para>
    /// Closing the window stops the test between packets and takes back
    /// whatever is still waiting to be said: the trace says "stopped, the
    /// window closed", and nothing is said after the close. A fresh run takes
    /// back what the run before it had left to say.
    /// </para>
    /// </remarks>
    internal sealed class EngineSpeedSession
    {
        /// <summary>How a line reaches the reader. The app's is <see cref="Say"/>; tests give an arbiter of their own.</summary>
        internal delegate void Voice(string text, SpeechIntent intent, bool additive);

        /// <summary>The test itself, run on its own thread: calls back with each line as it is ready.</summary>
        internal delegate IReadOnlyList<EngineSpeedResult> SpeedTest(Action<EngineSpeedResult> onResult, CancellationToken cancel);

        /// <summary>The app's voice: Terse, keyed with <see cref="SpeechSubject.EngineSpeedTest"/>.</summary>
        internal static void Say(string text, SpeechIntent intent, bool additive) =>
            ScreenReaderOutput.Speak(text, intent, VerbosityLevel.Terse, subject: SpeechSubject.EngineSpeedTest, additive: additive);

        /// <summary>The app's series for one run's lines, keyed with <see cref="SpeechSubject.EngineSpeedTest"/>.</summary>
        internal static SpokenSeries NewSeries() => ScreenReaderOutput.NewSpokenSeries(SpeechSubject.EngineSpeedTest);

        private readonly Dispatcher _dispatcher;
        private readonly Action<string> _show;
        private readonly Func<bool> _competing;
        private readonly SpeedTest _test;
        private readonly Func<EngineSpeedResult, string> _words;
        private readonly Voice _voice;
        private readonly Func<SpokenSeries> _newSeries;

        // The dispatcher's thread only.
        private CancellationTokenSource? _cancel;
        private bool _running;
        private SpokenSeries? _series;

        /// <param name="dispatcher">The result box's dispatcher: every line after "started" is posted to it.</param>
        /// <param name="show">Sets the result box's whole text.</param>
        /// <param name="competing">Whether the radio's receive audio is running, read when Start is pressed.</param>
        /// <param name="test">The test (the app's is <see cref="EngineSpeedTest.Run"/>).</param>
        /// <param name="words">A line's words.</param>
        /// <param name="voice">How "started" and "busy" are said; <see cref="Say"/> when null.</param>
        /// <param name="series">A new series for a run's lines; <see cref="NewSeries"/> when null.</param>
        internal EngineSpeedSession(Dispatcher dispatcher, Action<string> show, Func<bool> competing, SpeedTest test,
            Func<EngineSpeedResult, string> words, Voice? voice = null, Func<SpokenSeries>? series = null)
        {
            _dispatcher = dispatcher;
            _show = show;
            _competing = competing;
            _test = test;
            _words = words;
            _voice = voice ?? Say;
            _newSeries = series ?? NewSeries;
        }

        /// <summary>Whether a run is going (the dispatcher's thread).</summary>
        internal bool Running => _running;

        /// <summary>Whether any line of the latest run is still owed to the reader (the dispatcher's thread).</summary>
        internal bool Speaking => _series?.Owes == true;

        /// <summary>
        /// The Start press, on the dispatcher's thread. Says "busy" if a run is
        /// going; otherwise says "started" and runs the test on a thread of its
        /// own, never the UI thread and never the receive loop's. The task ends
        /// when the test's thread does; the lines it posted may still be on
        /// their way.
        /// </summary>
        internal Task Start()
        {
            if (_running)
            {
                _voice(Lexicon.Get("audio.diagnostics.speed.busy"), SpeechIntent.Interrupt, additive: true);
                return Task.CompletedTask;
            }
            _running = true;
            var cancel = new CancellationTokenSource();
            _cancel = cancel;
            // Whatever the last run still had to say is history now.
            _series?.Withdraw("a new speed test starting");
            var series = _newSeries();
            _series = series;
            var text = new StringBuilder();
            string started = Lexicon.Get("audio.diagnostics.speed.started");
            text.AppendLine(started);
            _show(text.ToString());
            _voice(started, SpeechIntent.Interrupt, additive: false);
            // Read on the dispatcher's thread, before the test starts: whether the
            // radio's receive audio shares the processor with the test.
            bool competing = _competing();

            void Post(string line, bool spoken) => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (cancel.IsCancellationRequested) return;
                text.AppendLine(line);
                _show(text.ToString());
                if (spoken) series.Add(line, VerbosityLevel.Terse);
            });

            return Task.Factory.StartNew(() =>
            {
                try
                {
                    _test(r =>
                    {
                        System.Diagnostics.Trace.WriteLine(TraceLine(r));
                        Post(_words(r), spoken: true);
                    }, cancel.Token);
                    cancel.Token.ThrowIfCancellationRequested();
                    Post(Lexicon.Get("audio.diagnostics.speed.defaults_note"), spoken: false);
                    Post(Lexicon.Get("audio.diagnostics.speed.helper_pairs_note"), spoken: false);
                    if (competing) Post(Lexicon.Get("audio.diagnostics.speed.competes"), spoken: false);
                    Post(Lexicon.Get("audio.diagnostics.speed.finished"), spoken: true);
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Trace.WriteLine("AudioWorkshop speed test: stopped, the window closed");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("AudioWorkshop speed test: failed: " + ex.Message);
                    Post(Lexicon.Get("audio.diagnostics.speed.test_failed"), spoken: true);
                }
                finally
                {
                    // Last on the same road, so a second Start is "busy" until
                    // every line of this run has been shown.
                    _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                    {
                        _running = false;
                        if (ReferenceEquals(_cancel, cancel)) _cancel = null;
                        cancel.Dispose();
                    });
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>
        /// The window closed (the dispatcher's thread): stop the test if it is
        /// running, and take back every line not yet said, whether it is or not
        /// (B1: the test ending is not its lines being said).
        /// </summary>
        internal void Stop()
        {
            _cancel?.Cancel();
            _series?.Withdraw("the Audio Workshop closing");
            _series = null;
        }

        /// <summary>The trace line for one result.</summary>
        internal static string TraceLine(EngineSpeedResult r) =>
            "AudioWorkshop speed test: " + string.Join(" then ", r.EngineIds)
            + (r.Model != null ? " model " + r.Model : "") + ": " + r.Verdict
            + (r.Figures is { } f ? FormattableString.Invariant($", mean {f.MeanMs:F3} ms, 99th percentile {f.P99Ms:F3} ms, worst {f.WorstMs:F3} ms over {f.Frames} packets") : "")
            + (r.Detail != null ? ", " + r.Detail : "");
    }
}
