using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The slot a running copy holds for its life, which keys the GUI client
    /// id file (#782, repair round 1, finding 1). Each test uses its own mutex
    /// name prefix so it never touches a live copy's slots or another test's.
    /// </summary>
    public sealed class GuiClientSlotTests
    {
        private static string Prefix() => @"Local\JJFlexRadio.Tests.GuiClientSlot." + Guid.NewGuid().ToString("N") + ".";

        [Fact]
        public void TheLowestFreeSlotIsTaken_AndChurnNeverHandsOutALiveCopysNumber()
        {
            string p = Prefix();
            using var a = GuiClientSlot.Acquire(p);
            using var b = GuiClientSlot.Acquire(p);
            Assert.Equal(1, a.Number);
            Assert.Equal(2, b.Number);
            Assert.True(a.Held && b.Held);

            // The process-count failure: close A, keep B, start C. C must NOT be 2.
            a.Dispose();
            using var c = GuiClientSlot.Acquire(p);
            Assert.Equal(1, c.Number);
            Assert.NotEqual(b.Number, c.Number);

            // And the gap left by B is the next one reused.
            b.Dispose();
            using var d = GuiClientSlot.Acquire(p);
            Assert.Equal(2, d.Number);
        }

        [Fact]
        public void SimultaneousLaunchesGetDistinctSlots()
        {
            string p = Prefix();
            var gate = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() => { gate.Wait(); return GuiClientSlot.Acquire(p); })).ToArray();
            gate.Set();
            var slots = Task.WhenAll(tasks).GetAwaiter().GetResult();
            try
            {
                var numbers = slots.Select(s => s.Number).OrderBy(n => n).ToArray();
                Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, numbers);
            }
            finally
            {
                foreach (var s in slots) s.Dispose();
            }
        }

        [Fact]
        public void ASlotWhoseOwnerDiedWithoutReleasing_IsTakenOver_AsAbandoned()
        {
            // Windows abandons a mutex when its owning thread ends; a process
            // ending is the same mechanism (its handles close). The next
            // WaitOne acquires it and reports WAIT_ABANDONED, which .NET raises
            // as AbandonedMutexException AFTER granting ownership.
            string p = Prefix();
            var owner = new Thread(() =>
            {
                var m = new Mutex(true, p + "1");   // owned, never released
                Assert.NotNull(m);
                // The thread ends holding it; the Mutex object is left for the
                // finalizer so the handle stays open until then — the OS marks
                // it abandoned the moment the owning thread exits.
                GC.KeepAlive(m);
            });
            owner.Start();
            owner.Join();

            using var taken = GuiClientSlot.Acquire(p);
            Assert.Equal(1, taken.Number);
            Assert.True(taken.TookOverAnAbandonedSlot, "the abandoned slot was not taken over: " + taken.Reason);
            Assert.Contains("previous owner ended", taken.Reason);
        }

        [Fact]
        public void PositiveControl_ASlotHeldByALivingOwnerIsNotTaken()
        {
            string p = Prefix();
            var holdUntil = new ManualResetEventSlim(false);
            var ready = new ManualResetEventSlim(false);
            var owner = new Thread(() =>
            {
                using var m = new Mutex(true, p + "1");
                ready.Set();
                holdUntil.Wait();
                m.ReleaseMutex();
            }) { IsBackground = true };
            owner.Start();
            ready.Wait();
            try
            {
                using var next = GuiClientSlot.Acquire(p);
                Assert.Equal(2, next.Number);
                Assert.False(next.TookOverAnAbandonedSlot);
                Assert.Contains("slot 1 held by another copy", next.Reason);
                Assert.Contains("gui-client-id-2.json", next.Reason);
            }
            finally
            {
                holdUntil.Set();
                owner.Join();
            }
        }

        [Fact]
        public void WithEverySlotHeld_ThereIsNoSlot_AndTheIdentityIsSessionOnly()
        {
            string p = Prefix();
            using var only = GuiClientSlot.Acquire(p, maxSlots: 1);
            Assert.Equal(1, only.Number);
            using var none = GuiClientSlot.Acquire(p, maxSlots: 1);
            Assert.Equal(0, none.Number);
            Assert.False(none.Held);
            Assert.Contains("no slot", none.Reason);

            var identity = GuiClientIdentity.ForConnect("0638-0000-8600-0001", none.Number);
            Assert.False(identity.Persistent);
            Assert.Null(identity.PresentedId);
            Assert.Null(identity.FilePath);
            Assert.Contains("holds no client-id slot", identity.DescribePresentation());
        }

        [Fact]
        public void TheProcessSlot_IsHeldOnceAndNamesItsFile()
        {
            var current = GuiClientSlot.Current;
            Assert.Same(current, GuiClientSlot.Current);
            Assert.True(current.Held, "the test process could not take a slot: " + current.Reason);
            Assert.Contains(current.Number == 1 ? "gui-client-id.json" : "gui-client-id-" + current.Number + ".json", current.Reason);
        }
    }
}
