using System;
using System.Collections.Generic;
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
/// Sprint 49 Track Q phase B repair 2, R2; Codex's re-check of 0194e190, whose
/// reproduction this adopts): the previous take's analysis stops owning what it
/// publishes AT the admission, before the countdown, though the take's own
/// generation changes only when the microphone opens. Its verdict finishing
/// during the countdown is neither shown nor queued, and so cannot acquire the
/// new take's speech lifetime. A start called off during its countdown gives
/// the previous take its result back, silently.
/// </summary>
/// <remarks>
/// Windowless and silent, as Codex ran it: the REAL <c>StartAuditionAsync</c>,
/// start gate and countdown (silent: a lead-in off, the countdown's words go to
/// the diverted speech sink), the real speech arbiter read by reflection, an
/// uninitialized dialog with unhosted controls and a private STA dispatcher; the
/// take's recorder is a fake and the countdown is always called off before it
/// ends, so no microphone opens. <see cref="CountdownSettings.Current"/> is set
/// for each test and put back.
/// </remarks>
[Collection("Countdown settings")]
public sealed class AuditionNewTakeOwnershipTests
{
    private const BindingFlags P = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type Fixture = typeof(AuditionAnalysisWindowTests);

    private static object? Helper(object fixture, string name, params object[] args) =>
        Fixture.GetMethod(name, BindingFlags.Static | P)!.Invoke(fixture, args);
    private static object? Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, P)!.Invoke(o, args);
    private static void Set(object o, string n, object? v) => o.GetType().GetField(n, P)!.SetValue(o, v);
    private static object? Get(object o, string n) => o.GetType().GetField(n, P)!.GetValue(o);

    /// <param name="startAdmitted">False is the positive control: a start the gate refuses changes nothing.</param>
    /// <param name="calledOff">After the old analysis finishes during the countdown, the start is called off.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void A_previous_takes_verdict_finishing_during_the_next_takes_countdown_is_neither_shown_nor_queued(
        bool startAdmitted, bool calledOff)
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
            Task? start = null;
            AudioCheckStartGate? gate = null;
            object? dialog = null;
            try
            {
                CountdownSettings.Current = new CountdownSettings { Enabled = true, Seconds = 10, LeadIn = false };
                dialog = Helper(fixture, "Dialog", AudioCheckListenMethods.Audition)!;
                var pair = Fixture.GetMethod("Take", P)!.Invoke(fixture, null)!;
                var take = (ChainAuditionTake)pair.GetType().GetField("Item1")!.GetValue(pair)!;
                object chain = pair.GetType().GetField("Item2")!.GetValue(pair)!;
                var release = (ManualResetEventSlim)chain.GetType().GetField("Release")!.GetValue(chain)!;
                var entered = (ManualResetEventSlim)chain.GetType().GetField("Entered")!.GetValue(chain)!;
                Set(dialog, "_audition", take);
                gate = new AudioCheckStartGate();
                Set(dialog, "_checkStarts", gate);
                Set(dialog, "_countdownSkipKeysHooked", true);

                // The previous take's analysis, started the window's own way, held running.
                var analysis = (Task<AuditionAnalysisOutcome>)Helper(fixture, "Begin", dialog, take,
                    take.AnalyseInBackground((string)Helper(fixture, "Recorded", take)!)!)!;
                Assert.True(entered.Wait(10000));

                using (var occupied = startAdmitted ? null : gate.TryBegin())
                {
                    start = (Task)Call(dialog, "StartAuditionAsync")!;
                    if (startAdmitted) { Assert.True(gate.IsBusy); Assert.False(start.IsCompleted); }
                    else Assert.True(start.IsCompleted);   // refused at the gate: nothing ended

                    var earlier = new HashSet<object>((IEnumerable<object>)Helper(fixture, "Entries")!, ReferenceEqualityComparer.Instance);
                    var box = (TextBox)Get(dialog, "_micReadingBox")!;
                    release.Set();
                    var outcome = analysis.Result;
                    Assert.True(take.IsCurrent(outcome));   // the take itself still calls it current: the window must not
                    Call(dialog, "OnAuditionAnalysed", outcome);
                    string verdict = ChainAudition.Verdict(outcome.Result!);
                    var queued = ((IEnumerable<object>)Helper(fixture, "Entries")!).Where(e => !earlier.Contains(e)
                        && (string?)e.GetType().GetField("Subject", P | BindingFlags.Public)!.GetValue(e) == Radios.Speech.SpeechSubject.AuditionVerdict)
                        .ToArray();
                    if (startAdmitted)
                    {
                        Assert.Empty(queued);
                        Assert.NotEqual(verdict, box.Text);
                    }
                    else
                    {
                        // The positive control: with no new take, the verdict is shown, queued and valid.
                        Assert.Equal(verdict, box.Text);
                        Assert.NotEmpty(queued);
                        Assert.All(queued, e => Assert.True(((Func<bool>)e.GetType().GetField("StillValid", P | BindingFlags.Public)!.GetValue(e)!)()));
                    }

                    if (startAdmitted)
                    {
                        // Call the start off during its countdown (as Escape does), and let it finish.
                        gate.Cancel();
                        ((CountdownRun?)Get(dialog, "_startCountdown"))?.Cancel();
                        var until = DateTime.UtcNow.AddSeconds(10);
                        while (!start.IsCompleted && DateTime.UtcNow < until)
                        {
                            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                            Thread.Sleep(10);
                        }
                        Assert.True(start.IsCompleted);
                        start.GetAwaiter().GetResult();
                        if (calledOff)
                        {
                            // The take is still there: its held result comes back to the reading box, silently.
                            var after = new HashSet<object>((IEnumerable<object>)Helper(fixture, "Entries")!, ReferenceEqualityComparer.Instance);
                            Assert.Equal(verdict, box.Text);
                            Assert.Equal(AuditionTakeState.Ready, take.State);
                            Assert.DoesNotContain(((IEnumerable<object>)Helper(fixture, "Entries")!), e => !after.Contains(e));
                        }
                    }
                }
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
        Assert.True(thread.Join(30000));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
