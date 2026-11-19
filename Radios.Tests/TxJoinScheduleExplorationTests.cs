using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Flex.Smoothlake.FlexLib;
using JJPortaudio;
using Radios;
using Xunit;
using static Radios.Tests.TxJoinRepairThreeTests;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track H2, join repair 3: the schedule exploration of the
    /// microphone worker, as carried without the unkey hold (Noel's H1 = B,
    /// 2026-11-19; families 1 and 2, the hold's, stay on sprint49/h2-join).
    /// Instead of single outcomes it drives the REAL types through every
    /// placement of a disruptive event and asserts two invariants of the state
    /// model (written in the H2 report under "Join repair 3"):
    /// <list type="bullet">
    /// <item>(d) no worker operation outlives its run or its audio server;</item>
    /// <item>(e) completed work is published whatever producer the gate selects.</item>
    /// </list>
    /// Each schedule runs on a fresh rig (an unconnected FlexBase, fake
    /// streams). A failure names the schedule. Windowless; no device.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TxJoinScheduleExplorationTests
    {
        // ══ Family 3: the microphone worker against its run and its server: (d) and (e) ══

        private sealed class WorkerRig : IDisposable
        {
            public readonly Rig X = new();
            public readonly FlexBase.audioChannelData Channel;
            public readonly RemoteAudioWake OldWake = new(), NewWake = new();
            public readonly List<Action> Jobs = new();
            public readonly List<string> Log = new();
            public readonly List<Fake> Opened = new();
            public readonly List<string> Violations = new();
            public long Server;
            public Action? BetweenOps, DuringSecond;
            public int Opens;

            public WorkerRig()
            {
                Channel = RepairTwoChannel(X);
                Channel.PortAudioStream = null; Channel.Started = false; // tier 2: the dead stream is out
                Set(X.B, "_remoteAudioWake", OldWake);
                Set(X.B, "_micReopenPending", true);
                X.B.TxCaptureWorkerForTest = a => Jobs.Add(a);
                X.B.TxCaptureOpenForTest = rate =>
                {
                    Opens++;
                    // (d) every open begins only on its own live server, for a wanted job.
                    var job = Get(X.B, "_txOpenJob");
                    bool wanted = job != null && (int)job.GetType().GetField("State")!.GetValue(job)! == 0
                        && ReferenceEquals(job.GetType().GetField("Wake")!.GetValue(job), Get(X.B, "_remoteAudioWake"))
                        && !(bool)Get(X.B, "stopRemoteAudio")! && !(bool)Get(X.B, "Disconnecting")!;
                    if (Audio.ServerGeneration != Server) Violations.Add("an open began on a server that had ended");
                    if (!wanted) Violations.Add("an open began for a job no longer wanted");
                    Log.Add("open at " + rate);
                    if (Opens == 1) { BetweenOps?.Invoke(); return null!; }   // the first rate will not open; then the event, after its operation
                    DuringSecond?.Invoke();                                   // the event while the second opens
                    var f = new Fake(); Opened.Add(f); return f;
                };
            }

            public void Event(string e)
            {
                Log.Add(e);
                switch (e)
                {
                    case "abandon": Call(X.B, "AbandonTxOpenJob", "explore"); break;
                    case "server ends": Audio.MoveServerGenerationForTest(); break;
                    case "new run": Set(X.B, "_remoteAudioWake", NewWake); break;
                    case "stop": Set(X.B, "stopRemoteAudio", true); break;
                    case "disconnecting": Set(X.B, "Disconnecting", true); break;
                    default: throw new ArgumentException(e);
                }
            }

            public void Dispose()
            {
                Set(X.B, "stopRemoteAudio", false); Set(X.B, "Disconnecting", false);
                OldWake.Dispose(); NewWake.Dispose(); X.Dispose();
            }
        }

        [Fact]
        public void No_worker_operation_outlives_its_run_and_returned_work_is_always_published()
        {
            var events = new[] { "", "abandon", "server ends", "new run", "stop", "disconnecting" };
            var places = new[] { "before", "between", "during", "after" };
            var passes = new[] { "microphone", "self-clock", "loop" };
            int runs = 0, published = 0;
            foreach (var e in events)
                foreach (var place in e == "" ? new[] { "before" } : places)
                    foreach (var pass in passes)
                    {
                        // The server cannot end DURING an operation: the gate excludes it.
                        if (e == "server ends" && place == "during") continue;
                        using var w = new WorkerRig();
                        Call(w.X.B, "StartTxOpenJob", 50, Enum.Parse(typeof(FlexBase).GetNestedType("TxOpenKind", System.Reflection.BindingFlags.NonPublic)!, "Replace"), w.Channel, 10, "explore");
                        w.Server = (long)Get(Get(w.X.B, "_txOpenJob")!, "Server")!;
                        if (e != "" && place == "before") w.Event(e);
                        if (e != "" && place == "between") w.BetweenOps = () => w.Event(e);
                        if (e != "" && place == "during") w.DuringSecond = () => w.Event(e);
                        w.Jobs[0]();
                        if (e != "" && place == "after") w.Event(e);
                        w.Log.Add(pass + " pass");
                        switch (pass)
                        {
                            case "microphone": Call(w.X.B, "startOpusInputChannel"); break;
                            case "self-clock": Call(w.X.B, "startSelfClockedTxInput"); break;
                            default: Call(w.X.B, "TakeReturnedTxReplacement"); break;
                        }
                        foreach (var close in w.Jobs.Skip(1).ToList()) close();
                        runs++;
                        string at = " [" + e + " " + place + ", " + pass + "] " + string.Join("; ", w.Log);
                        Assert.True(w.Violations.Count == 0, "(d) " + string.Join(", ", w.Violations) + ":" + at);
                        // (d) every stream opened is published into its own run, or closed: never left, never leaked into another run.
                        foreach (var f in w.Opened)
                        {
                            bool inChannel = ReferenceEquals(w.Channel.PortAudioStream, f);
                            bool currentRun = ReferenceEquals(Get(w.X.B, "_remoteAudioWake"), w.OldWake);
                            Assert.True(inChannel ^ (f.Closes == 1), "(d) a stream opened was neither published nor closed exactly once:" + at);
                            if (inChannel) Assert.True(currentRun, "(d) a stream was published into a later run:" + at);
                        }
                        // (e) work that came back to a run still current, not abandoned and not closing, is published at the next pass, whatever producer.
                        bool stillWanted = e == "" || place == "after" && e != "abandon" && e != "new run" && e != "server ends";
                        if (stillWanted && w.Opened.Count == 1 && e != "stop" && e != "disconnecting")
                        {
                            Assert.Same(w.Opened[0], w.Channel.PortAudioStream);
                            published++;
                        }
                    }
            Assert.True(runs > 50, "the exploration ran " + runs + " schedules");
            Assert.True(published >= 3, "positive control: the ordinary return was published by every producer (" + published + ")");
        }
    }
}
