using System;
using System.Threading;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// A numbered slot one running copy of the program holds for its whole
    /// life, so two copies on one machine never present the same GUI client
    /// id to a radio (#782). The slot number, not the process count, keys
    /// the per-radio id file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not the process count.</b> <c>ProgramInstance</c> in
    /// <c>globals.vb</c> is the number of running copies at startup, and it
    /// names the trace file; it is left exactly as it was. As an identity key
    /// it fails on ordinary churn: start A (1) and B (2), connect B so its id
    /// is stored under 2, close A, start C — there are two processes again,
    /// so C is also 2 and presents B's LIVE id. Codex Astra found this on
    /// 2026-10-19 reviewing the first round of this track.
    /// </para>
    /// <para>
    /// <b>The mechanism.</b> One named mutex per slot,
    /// <c>Local\JJFlexRadio.GuiClientSlot.N</c>, taken with a zero wait from
    /// N = 1 upward; the first one acquired is ours and is HELD until this
    /// object is disposed or the process ends. Windows gives three things at
    /// once: the acquisition is atomic across simultaneous launches (two
    /// copies cannot both own one mutex), the slot comes back when its owner
    /// dies (a mutex whose owning thread or process ends becomes abandoned,
    /// and the next <c>WaitOne</c> acquires it and reports
    /// <c>WAIT_ABANDONED</c>, which .NET raises as
    /// <see cref="AbandonedMutexException"/> AFTER granting ownership), and
    /// the lowest free number is reused, so the file a slot names stays the
    /// same copy's file as long as that copy runs.
    /// </para>
    /// <para>
    /// <b>Held on its own thread, on purpose.</b> A mutex belongs to the
    /// THREAD that acquired it; if that thread ended while the process lived,
    /// the slot would read as abandoned and another copy could take it from
    /// under a running one. Each slot is therefore acquired and held by a
    /// dedicated background thread that lives until <see cref="Dispose"/>.
    /// </para>
    /// <para>
    /// <b>No slot is a valid outcome.</b> If every slot is held,
    /// <see cref="Number"/> is 0 and the identity becomes session-only
    /// (nothing presented, nothing stored). <see cref="Reason"/> says what
    /// happened, for the trace.
    /// </para>
    /// </remarks>
    public sealed class GuiClientSlot : IDisposable
    {
        /// <summary>The production mutex name prefix; the slot number follows it.</summary>
        public const string DefaultPrefix = @"Local\JJFlexRadio.GuiClientSlot.";

        /// <summary>How many copies of the program one machine may run with a
        /// stored identity each. A sixteenth copy is session-only.</summary>
        public const int DefaultMaxSlots = 16;

        private readonly Thread _holder;
        private int _disposed;

        private GuiClientSlot(int number, string reason, Thread holder, bool abandoned)
        {
            Number = number;
            Reason = reason;
            _holder = holder;
            TookOverAnAbandonedSlot = abandoned;
        }

        /// <summary>The slot held, 1-based; 0 when none could be taken.</summary>
        public int Number { get; }

        /// <summary>True when a slot is held.</summary>
        public bool Held => Number > 0;

        /// <summary>The slot's previous owner ended without releasing it (a crash,
        /// or a kill); Windows handed it over as abandoned and we hold it now.</summary>
        public bool TookOverAnAbandonedSlot { get; }

        /// <summary>One sentence for the trace.</summary>
        public string Reason { get; }

        private static readonly Lazy<GuiClientSlot> _current = new Lazy<GuiClientSlot>(() =>
        {
            var slot = Acquire();
            Tracing.TraceLine("GuiClientSlot: " + slot.Reason, slot.Held ? System.Diagnostics.TraceLevel.Info : System.Diagnostics.TraceLevel.Warning);
            return slot;
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>This process's slot, taken on first use and held until the
        /// process ends. Every rig in the process shares it.</summary>
        public static GuiClientSlot Current => _current.Value;

        /// <summary>
        /// Take the lowest free slot under <paramref name="prefix"/>. Tests pass
        /// their own prefix so they never touch a live copy's slots.
        /// </summary>
        public static GuiClientSlot Acquire(string prefix = DefaultPrefix, int maxSlots = DefaultMaxSlots)
        {
            if (string.IsNullOrWhiteSpace(prefix)) prefix = DefaultPrefix;
            if (maxSlots < 1) maxSlots = 1;
            var busy = new System.Collections.Generic.List<int>();
            for (int n = 1; n <= maxSlots; n++)
            {
                var result = TryHold(prefix + n, out Thread holder, out ManualResetEventSlim release);
                if (result == HoldResult.Held || result == HoldResult.HeldAbandoned)
                {
                    bool abandoned = result == HoldResult.HeldAbandoned;
                    string reason = "holding slot " + n
                        + (abandoned ? " (its previous owner ended without releasing it; taken over)" : "")
                        + (busy.Count > 0 ? " (slot" + (busy.Count == 1 ? " " : "s ") + string.Join(", ", busy) + " held by another copy)" : "")
                        + "; the per-radio client id file for this copy is " + (n == 1 ? "gui-client-id.json" : "gui-client-id-" + n + ".json");
                    var slot = new GuiClientSlot(n, reason, holder, abandoned);
                    slot.AdoptRelease(release);
                    return slot;
                }
                if (result == HoldResult.Busy) busy.Add(n);
                // Error: the name could not be opened at all; try the next.
            }
            return new GuiClientSlot(0, "no slot: all " + maxSlots + " slots are held by other copies (or could not be opened); this copy presents no client id and stores none", null, false);
        }

        private ManualResetEventSlim _adoptedRelease;
        private void AdoptRelease(ManualResetEventSlim release) => _adoptedRelease = release;

        private enum HoldResult { Held, HeldAbandoned, Busy, Error }

        /// <summary>
        /// Try to own the mutex <paramref name="name"/> on a fresh background
        /// thread that keeps owning it until <paramref name="release"/> is set.
        /// Returns once the thread knows whether it got it.
        /// </summary>
        private static HoldResult TryHold(string name, out Thread holder, out ManualResetEventSlim release)
        {
            var outcome = new ManualResetEventSlim(false);
            var rel = new ManualResetEventSlim(false);
            HoldResult result = HoldResult.Error;
            var thread = new Thread(() =>
            {
                Mutex mutex = null;
                bool owned = false;
                try
                {
                    mutex = new Mutex(false, name);
                    try
                    {
                        owned = mutex.WaitOne(0);
                        result = owned ? HoldResult.Held : HoldResult.Busy;
                    }
                    catch (AbandonedMutexException)
                    {
                        // WAIT_ABANDONED: ownership IS granted; the previous
                        // owner simply never released. Treat as acquired.
                        owned = true;
                        result = HoldResult.HeldAbandoned;
                    }
                }
                catch (Exception)
                {
                    result = HoldResult.Error;
                }
                finally
                {
                    outcome.Set();
                }
                if (owned)
                {
                    rel.Wait();
                    try { mutex?.ReleaseMutex(); } catch { }
                }
                mutex?.Dispose();
            })
            {
                IsBackground = true,
                Name = "GuiClientSlot holder " + name,
            };
            thread.Start();
            outcome.Wait();
            holder = thread;
            release = rel;
            return result;
        }

        /// <summary>Release the slot. The process ending releases it too.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _adoptedRelease?.Set();
            if (_holder != null && _holder.IsAlive) _holder.Join(2000);
        }

        public override string ToString() => Held ? "slot " + Number : "no slot";
    }
}
