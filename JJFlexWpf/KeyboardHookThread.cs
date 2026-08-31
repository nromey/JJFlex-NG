#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace JJFlexWpf
{
    /// <summary>
    /// The one thread in this process allowed to own a global (WH_*_LL)
    /// hook (#402). It pumps messages and does nothing else, so no
    /// application work can ever block it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this thread exists:</b> a WH_KEYBOARD_LL callback is delivered
    /// by the message pump of the thread that installed it. Until #402 both
    /// of this app's global hooks — <see cref="HelpLauncher"/>'s
    /// Escape-closes-CHM and <see cref="CwCtrlInterrupt"/>'s
    /// Ctrl-silences-CW — installed from the UI thread. So any bug that
    /// blocked the UI thread made EVERY keystroke on the machine wait out
    /// LowLevelHooksTimeout before Windows passed the key through, and after
    /// repeated timeouts Windows silently removes the hook, killing the
    /// feature for the rest of the session. On 2026-08-29 a blocked connect
    /// did exactly that, three times, about 45 seconds each: the operator
    /// could not type into OTHER applications while this one was stuck — and
    /// for a blind operator whose screen reader is keyboard-driven, that
    /// removes every route out.
    /// </para>
    /// <para>
    /// <b>The invariant:</b> a global hook and a thread that can ever block
    /// must not be the same thread. This thread's loop is
    /// <see cref="Dispatcher.Run"/> and nothing more; the only work ever
    /// posted here is installing a hook, removing one, and the hook
    /// callbacks themselves, which by contract return in microseconds.
    /// </para>
    /// <para>
    /// <b>Rules for anything that runs here</b> — pinned by
    /// <c>Radios.Tests/GlobalHookThreadTests.cs</c>:
    /// a callback never blocks and never marshals synchronously to another
    /// thread. Slow work is handed off (<c>Task.Run</c>, or a
    /// <c>BeginInvoke</c> post) and NEVER awaited; decisions that need
    /// application state read a snapshot or a volatile flag, never
    /// <c>Dispatcher.Invoke</c>; window messages go by <c>PostMessage</c>,
    /// never its synchronous sibling, which would couple this thread to the
    /// target window's possibly-blocked pump — precisely the disease this
    /// thread exists to cure.
    /// </para>
    /// </remarks>
    internal static class KeyboardHookThread
    {
        private static readonly object _gate = new object();
        private static Thread? _thread;
        private static Dispatcher? _dispatcher;

        /// <summary>
        /// Installs requested before the thread finished starting; the pump
        /// drains it exactly once. Null once the dispatcher is live — from
        /// then on installs post directly.
        /// </summary>
        private static List<Action>? _pendingInstalls = new List<Action>();

        private static readonly List<(string Name, Action Unhook)> _teardowns
            = new List<(string, Action)>();

        private static bool _processExitWired;

        /// <summary>
        /// The hook thread's managed id once it is running, for traces and
        /// diagnostics. Null before the first install starts it.
        /// </summary>
        internal static int? ManagedThreadId
        {
            get { lock (_gate) { return _thread?.ManagedThreadId; } }
        }

        /// <summary>
        /// Run <paramref name="installOnHookThread"/> on the dedicated pumped
        /// hook thread — the SetWindowsHookEx call must execute THERE, because
        /// Windows delivers a low-level hook's callbacks via the installing
        /// thread's pump. Fire-and-forget by design: the caller is typically
        /// the UI thread and must never wait on this one; install failures are
        /// traced by the installer itself, exactly as they were when the
        /// installs were inline.
        /// </summary>
        /// <param name="name">For traces only.</param>
        /// <param name="installOnHookThread">
        /// Installs the hook and records its handle. Runs on the hook thread.
        /// </param>
        /// <param name="unhookOnHookThread">
        /// Removes the hook if installed. Runs on the hook thread during
        /// shutdown, before the pump stops.
        /// </param>
        internal static void InstallHook(
            string name, Action installOnHookThread, Action unhookOnHookThread)
        {
            if (installOnHookThread == null) throw new ArgumentNullException(nameof(installOnHookThread));
            if (unhookOnHookThread == null) throw new ArgumentNullException(nameof(unhookOnHookThread));

            Action wrapped = () =>
            {
                try
                {
                    installOnHookThread();
                    Trace.WriteLine(
                        $"KeyboardHookThread: {name} installed from the dedicated hook thread " +
                        $"(managed id {Environment.CurrentManagedThreadId}).");
                }
                catch (Exception ex)
                {
                    // A hook is a convenience; failing to install one must
                    // never take the thread (or the app) with it.
                    Trace.WriteLine($"KeyboardHookThread: installing {name} failed: {ex.Message}");
                }
            };

            Dispatcher? live = null;
            lock (_gate)
            {
                _teardowns.Add((name, unhookOnHookThread));
                if (_dispatcher == null)
                {
                    // Thread not up yet (or still starting): queue the install
                    // for the pump to drain, rather than waiting here for the
                    // dispatcher to exist. Nothing in this class ever makes a
                    // caller wait on the hook thread.
                    _pendingInstalls!.Add(wrapped);
                    EnsureThreadStartedLocked();
                }
                else
                {
                    live = _dispatcher;
                }
            }
            live?.BeginInvoke(wrapped);
        }

        private static void EnsureThreadStartedLocked()
        {
            if (_thread != null)
                return;

            if (!_processExitWired)
            {
                // Belt and braces: Windows frees a process's hooks at process
                // death anyway, but an explicit unhook-and-join leaves nothing
                // to the cleanup of last resort. ProcessExit handlers get a
                // couple of seconds; the bounded Join below fits.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
                _processExitWired = true;
            }

            _thread = new Thread(Pump)
            {
                Name = "JJFlex global keyboard hooks",
                // Background, so this thread can never hold the process open:
                // even if Shutdown is somehow never called, exit proceeds and
                // Windows reclaims the hooks.
                IsBackground = true,
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        /// <summary>
        /// The whole life of the hook thread: create a dispatcher, drain the
        /// installs that were requested before it existed, pump until told to
        /// shut down. The unhooks run from ShutdownStarted, still on this
        /// thread, before the pump stops.
        /// </summary>
        private static void Pump()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;

            // Same soft-recover philosophy as the app's UI dispatcher
            // (ApplicationEvents wires CrashReporter there): a fault in a
            // hook callback is traced, not fatal. Callbacks are written not
            // to throw; this is the net under that contract, because an
            // unhandled exception here would take the whole process.
            dispatcher.UnhandledException += (_, e) =>
            {
                Trace.WriteLine($"KeyboardHookThread: unhandled exception in a hook callback: {e.Exception}");
                e.Handled = true;
            };

            dispatcher.ShutdownStarted += (_, _) => UnhookAll();

            List<Action> pending;
            lock (_gate)
            {
                _dispatcher = dispatcher;
                pending = _pendingInstalls!;
                _pendingInstalls = null;
            }
            foreach (var install in pending)
                dispatcher.BeginInvoke(install);

            // Pumps messages — which is how Windows delivers the hook
            // callbacks — until BeginInvokeShutdown. Nothing else ever
            // runs here, so nothing can block it.
            Dispatcher.Run();
        }

        private static void UnhookAll()
        {
            (string Name, Action Unhook)[] teardowns;
            lock (_gate)
            {
                teardowns = _teardowns.ToArray();
                _teardowns.Clear();
            }
            foreach (var (name, unhook) in teardowns)
            {
                try
                {
                    unhook();
                    Trace.WriteLine($"KeyboardHookThread: {name} unhooked.");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"KeyboardHookThread: unhooking {name} failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Unhook everything and end the thread. Wired to ProcessExit; safe
        /// to call more than once, from any thread. The Join is bounded and
        /// runs on the CALLER (a dying process's exit path), never on the
        /// hook thread — the one legitimate wait in this file.
        /// </summary>
        internal static void Shutdown()
        {
            Dispatcher? dispatcher;
            Thread? thread;
            lock (_gate)
            {
                dispatcher = _dispatcher;
                thread = _thread;
            }
            if (dispatcher == null || thread == null)
                return;

            try
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                if (!thread.Join(1500))
                {
                    Trace.WriteLine(
                        "KeyboardHookThread: hook thread did not end within 1500 ms of shutdown; " +
                        "proceeding — it is a background thread and Windows frees the hooks at process death.");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"KeyboardHookThread.Shutdown: {ex.Message}");
            }
        }
    }
}
