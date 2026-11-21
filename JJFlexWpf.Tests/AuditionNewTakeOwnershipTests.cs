using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// A new take admitted while the previous take is still being processed (#262,
/// Sprint 49 Track Q phase B repairs 2 and 3; Codex's re-checks of 0194e190
/// and 807be937): the previous take's analysis stops owning what it publishes
/// AT the admission, before the countdown, though the take's own generation
/// changes only when the microphone opens. Its outcome finishing during the
/// countdown is neither shown nor queued. A start called off during its
/// countdown gives the previous take back its WHOLE terminal outcome (a verdict,
/// an unreadable take, a failed analysis), silently and exactly once, in either
/// order of the take becoming ready and its completion callback arriving; an
/// analysis still running when the start is called off publishes as usual.
/// </summary>
/// <remarks>
/// Windowless and silent, as Codex ran it: the REAL <c>StartAuditionAsync</c>,
/// start gate and countdown (lead-in off; the countdown's words go to the
/// diverted speech sink), the real speech arbiter read by reflection, an
/// uninitialized dialog with unhosted controls and a private STA dispatcher; the
/// take's recorder is a fake and every countdown is called off before it ends,
/// so no microphone opens. Every "nothing queued" check snapshots the arbiter
/// BEFORE the boundary it covers (Codex's re-check 2). The assembly runs its
/// tests one at a time, so the countdown settings set here cannot collide.
/// </remarks>
public sealed class AuditionNewTakeOwnershipTests
{
    private const BindingFlags P = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Any = P | BindingFlags.Public;
    private static readonly Type Fixture = typeof(AuditionAnalysisWindowTests);

    private static object? Helper(object fixture, string name, params object[] args) =>
        Fixture.GetMethod(name, BindingFlags.Static | P)!.Invoke(fixture, args);
    private static object? Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, P)!.Invoke(o, args);
    private static void Set(object o, string n, object? v) => o.GetType().GetField(n, P)!.SetValue(o, v);
    private static object? Get(object o, string n) => o.GetType().GetField(n, P)!.GetValue(o);

    /// <summary>What the analysis of the previous take does.</summary>
    public enum Kind { Verdict, Unreadable, Fails }

    private sealed class Held : IChainAuditionProcessor
    {
        public readonly ManualResetEventSlim Entered = new(false), Release = new(false);
        public bool Throw;
        public ChainProcessing Process(float[] raw, int rate, CancellationToken cancel, Action<double>? progress)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(20), cancel);
            if (Throw) throw new InvalidOperationException("the chain failed");
            return ChainProcessing.Rendered(raw.Select(s => s * 0.5f).ToArray(), 0, gateEnabled: false, thresholdDb: null);
        }
    }

    /// <summary>One scenario on a private STA dispatcher: the previous take's analysis started the window's way and held running.</summary>
    private sealed class Ctx
    {
        public required object Fixture;
        public required object Dialog;
        public required ChainAuditionTake Take;
        public required AudioCheckStartGate Gate;
        public required ManualResetEventSlim Entered, Release;
        public required Task<AuditionAnalysisOutcome> Analysis;
        public Task? Start;

        public TextBox Box => (TextBox)Get(Dialog, "_micReadingBox")!;
        public HashSet<object> Snapshot() => new((IEnumerable<object>)Helper(Fixture, "Entries")!, ReferenceEqualityComparer.Instance);
        public object[] AuditionEntriesSince(HashSet<object> before) => ((IEnumerable<object>)Helper(Fixture, "Entries")!)
            .Where(e => !before.Contains(e) && (string?)e.GetType().GetField("Subject", Any)!.GetValue(e) == Radios.Speech.SpeechSubject.AuditionVerdict)
            .ToArray();

        public void Admit()
        {
            Start = (Task)Call(Dialog, "StartAuditionAsync")!;
            Assert.True(Gate.IsBusy);
            Assert.False(Start.IsCompleted);
        }

        /// <summary>Call the admitted start off during its countdown, as Escape does, and let it finish on this dispatcher.</summary>
        public void CallOff()
        {
            Gate.Cancel();
            ((CountdownRun?)Get(Dialog, "_startCountdown"))?.Cancel();
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!Start!.IsCompleted && DateTime.UtcNow < until)
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Thread.Sleep(10);
            }
            Assert.True(Start.IsCompleted);
            Start.GetAwaiter().GetResult();
        }

        /// <summary>The completion callback, as BeginAuditionProcessing's continuation posts it.</summary>
        public void Deliver(AuditionAnalysisOutcome outcome) => Call(Dialog, "OnAuditionAnalysed", outcome);
    }

    private static void Scenario(Kind kind, Action<Ctx> body, Action<ChainAuditionTake>? before = null)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced);
        Assert.False(OutputChannelRecorder.RenderEnabled);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var saved = CountdownSettings.Current;
            using var fixture = new AuditionAnalysisWindowTests();
            object? dialog = null;
            AudioCheckStartGate? gate = null;
            try
            {
                CountdownSettings.Current = new CountdownSettings { Enabled = true, Seconds = 10, LeadIn = false };
                dialog = Helper(fixture, "Dialog", AudioCheckListenMethods.Audition)!;
                var chain = new Held { Throw = kind == Kind.Fails };
                using var loadEntered = new ManualResetEventSlim(false);
                var recorder = Activator.CreateInstance(Fixture.GetNestedType("FakeRecorder", BindingFlags.NonPublic)!, nonPublic: true)!;
                string folder = (string)Get(fixture, "_folder")!;
                Func<string, (float[] Mono, int Rate)?> readMono = p => ((float[], int)?)Fixture.GetMethod("ReadMono", BindingFlags.Static | P)!.Invoke(null, new object[] { p });
                var take = new ChainAuditionTake(folder, (IAuditionRecorder)recorder, chain, kind == Kind.Unreadable
                    ? p => { chain.Entered.Set(); chain.Release.Wait(TimeSpan.FromSeconds(20)); return null; }
                    : readMono);
                before?.Invoke(take);
                Set(dialog, "_audition", take);
                gate = new AudioCheckStartGate();
                Set(dialog, "_checkStarts", gate);
                Set(dialog, "_countdownSkipKeysHooked", true);
                var analysis = (Task<AuditionAnalysisOutcome>)Helper(fixture, "Begin", dialog, take,
                    take.AnalyseInBackground((string)Helper(fixture, "Recorded", take)!)!)!;
                Assert.True(chain.Entered.Wait(10000));
                body(new Ctx { Fixture = fixture, Dialog = dialog, Take = take, Gate = gate, Entered = chain.Entered, Release = chain.Release, Analysis = analysis });
            }
            catch (Exception e) { failure = e; }
            finally
            {
                gate?.Cancel();
                if (dialog != null) ((CountdownRun?)Get(dialog, "_startCountdown"))?.Cancel();
                CountdownSettings.Current = saved;
                if (dialog != null) Call(dialog, "EndAudition");
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(40000));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static string Words(Kind kind, AuditionAnalysisOutcome outcome) => kind switch
    {
        Kind.Verdict => ChainAudition.Verdict(outcome.Result!),
        Kind.Unreadable => Lexicon.Get("audio.audition.unreadable"),
        _ => Lexicon.Get("audio.audition.analysis_failed"),
    };

    /// <summary>
    /// Repair 2's case: the outcome arrives DURING the countdown and is held
    /// back. Repair 3: whatever it was (a verdict, an unreadable take, a failed
    /// analysis), calling the start off puts it in the reading box silently.
    /// </summary>
    [Theory]
    [InlineData(Kind.Verdict)]
    [InlineData(Kind.Unreadable)]
    [InlineData(Kind.Fails)]
    public void An_outcome_finishing_during_the_countdown_is_held_back_then_restored_whole_and_silently_when_the_start_is_called_off(Kind kind)
    {
        Scenario(kind, x =>
        {
            x.Admit();
            var beforeCompletion = x.Snapshot();
            x.Release.Set();
            var outcome = x.Analysis.Result;
            Assert.Equal(kind == Kind.Verdict, outcome.Result != null);
            Assert.Equal(kind == Kind.Verdict ? "" : kind == Kind.Unreadable ? ChainAuditionTake.TroubleUnreadable : ChainAuditionTake.TroubleFailed,
                outcome.Trouble);
            x.Deliver(outcome);
            Assert.NotEqual(Words(kind, outcome), x.Box.Text);       // held back during the countdown
            Assert.Empty(x.AuditionEntriesSince(beforeCompletion));
            x.CallOff();
            Assert.Equal(Words(kind, outcome), x.Box.Text);           // the whole outcome, restored
            Assert.Empty(x.AuditionEntriesSince(beforeCompletion));   // silently: snapshot taken before the boundary
            x.Deliver(outcome);                                       // a duplicate delivery says nothing either
            Assert.Empty(x.AuditionEntriesSince(beforeCompletion));
        });
    }

    /// <summary>The positive control: the gate refuses the start, so nothing ends, and each outcome is shown and queued valid.</summary>
    [Theory]
    [InlineData(Kind.Verdict)]
    [InlineData(Kind.Unreadable)]
    [InlineData(Kind.Fails)]
    public void With_the_start_refused_the_outcome_is_shown_and_queued_as_usual(Kind kind)
    {
        Scenario(kind, x =>
        {
            using (x.Gate.TryBegin())
            {
                var refused = (Task)Call(x.Dialog, "StartAuditionAsync")!;
                Assert.True(refused.IsCompleted);
            }
            var before = x.Snapshot();
            x.Release.Set();
            var outcome = x.Analysis.Result;
            x.Deliver(outcome);
            Assert.Equal(Words(kind, outcome), x.Box.Text);
            var queued = x.AuditionEntriesSince(before);
            Assert.NotEmpty(queued);
            Assert.All(queued, e => Assert.True(((Func<bool>)e.GetType().GetField("StillValid", Any)!.GetValue(e)!)()));
        });
    }

    /// <summary>
    /// Codex's other order: the take reaches its terminal state (ready, with its
    /// result exposed under the lock; or idle after an unreadable take or a
    /// failure) before its task completes, and the start is called off in that
    /// interval. A ready verdict is restored silently from the take; a failure is
    /// shown silently when its callback arrives; either way the late callback
    /// queues nothing, and is not shown twice.
    /// </summary>
    [Theory]
    [InlineData(Kind.Verdict)]
    [InlineData(Kind.Unreadable)]
    [InlineData(Kind.Fails)]
    public void An_outcome_restored_while_its_task_is_still_finishing_is_not_spoken_by_the_late_callback(Kind kind)
    {
        using var terminal = new ManualResetEventSlim(false);
        using var finish = new ManualResetEventSlim(false);
        Scenario(kind, x =>
        {
            x.Admit();
            var before = x.Snapshot();
            x.Release.Set();
            Assert.True(terminal.Wait(10000));                // terminal state set, the task not yet complete
            Assert.NotEqual(AuditionTakeState.Analysing, x.Take.State);
            Assert.False(x.Analysis.IsCompleted);
            x.CallOff();
            if (kind == Kind.Verdict)
                Assert.Equal(ChainAudition.Verdict(x.Take.Result!), x.Box.Text);   // restored silently from the take
            finish.Set();
            var outcome = x.Analysis.Result;
            x.Deliver(outcome);                               // the late callback
            Assert.Equal(Words(kind, outcome), x.Box.Text);
            Assert.Empty(x.AuditionEntriesSince(before));
            x.Deliver(outcome);                               // and never twice
            Assert.Empty(x.AuditionEntriesSince(before));
        }, take => typeof(ChainAuditionTake).GetProperty("BeforeOutcomeReturnsForTest", P)!.SetValue(take,
            new Action(() => { terminal.Set(); finish.Wait(TimeSpan.FromSeconds(20)); })));
    }

    /// <summary>An analysis still running when the start is called off is the take's again, and publishes as usual, spoken.</summary>
    [Fact]
    public void An_analysis_still_running_when_the_start_is_called_off_publishes_as_usual()
    {
        Scenario(Kind.Verdict, x =>
        {
            x.Admit();
            x.CallOff();
            var before = x.Snapshot();
            x.Release.Set();
            var outcome = x.Analysis.Result;
            x.Deliver(outcome);
            Assert.Equal(ChainAudition.Verdict(outcome.Result!), x.Box.Text);
            Assert.NotEmpty(x.AuditionEntriesSince(before));
        });
    }
}
