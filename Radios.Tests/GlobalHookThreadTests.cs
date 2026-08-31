using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #402. Pins the invariant nobody had: a global keyboard hook must not
    /// live on a thread that can block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What happened.</b> On 2026-08-29 a connect blocked the UI thread
    /// for ~45 seconds, three times. Both of this app's WH_KEYBOARD_LL hooks
    /// (CwCtrlInterrupt's Ctrl-silences-CW, HelpLauncher's Escape-closes-CHM)
    /// were installed from that thread, and Windows delivers a low-level
    /// hook's callbacks via the installing thread's pump. So for the whole of
    /// each stall, EVERY keystroke on the machine waited out
    /// LowLevelHooksTimeout before the system passed it through: the operator
    /// could not type into other applications. For a blind operator whose
    /// screen reader is keyboard-driven, that removes every route out.
    /// </para>
    /// <para>
    /// <b>Nothing could fail.</b> The hooks worked; the app worked; only the
    /// coincidence of "hook's thread" and "thread that blocks" was the
    /// defect, and no compile, review, or existing test looks at which thread
    /// a hook lives on. These scans make that an assertable fact: every
    /// global hook installs from KeyboardHookThread's dedicated pump (which
    /// does nothing but pump, so nothing can block it), and nothing on the
    /// callback path may block or marshal synchronously to a thread that can.
    /// </para>
    /// <para>
    /// The hook itself cannot be exercised in a unit test — installing a real
    /// WH_KEYBOARD_LL hook in a test process would put a hook into the
    /// operator's live input chain, which is its own category of wrong. So
    /// this file asserts the wiring in source, with positive controls proving
    /// the scans would see what they claim to forbid.
    /// </para>
    /// </remarks>
    public sealed class GlobalHookThreadTests
    {
        private const string Host = "JJFlexWpf/KeyboardHookThread.cs";

        /// <summary>
        /// The two hooks that were on the UI thread the night this broke.
        /// A third consumer (#307 wants one) simply joins this list.
        /// </summary>
        private static readonly string[] Consumers =
        {
            "JJFlexWpf/CwCtrlInterrupt.cs",
            "JJFlexWpf/HelpLauncher.cs",
        };

        /// <summary>
        /// THE ONE THAT WOULD HAVE CAUGHT IT. Any file in the tree that
        /// installs a global low-level hook must route the install through
        /// the dedicated hook thread — installing from wherever the caller
        /// happens to run is exactly how both hooks landed on the UI thread.
        /// </summary>
        [Fact]
        public void EveryGlobalHookInstallRoutesThroughTheDedicatedHookThread()
        {
            var installers = new List<string>();
            foreach (var file in SourceFiles())
            {
                string text = File.ReadAllText(file);
                if (!text.Contains("WH_KEYBOARD_LL", StringComparison.Ordinal)
                    && !text.Contains("WH_MOUSE_LL", StringComparison.Ordinal))
                    continue;
                if (!text.Contains("SetWindowsHookEx", StringComparison.Ordinal))
                    continue;

                installers.Add(file);

                if (Path.GetFileName(file) == "KeyboardHookThread.cs")
                    continue; // the host itself is allowed to know about hooks

                Assert.True(
                    text.Contains("KeyboardHookThread.InstallHook(", StringComparison.Ordinal),
                    Rel(file) + " installs a global low-level hook without routing it through "
                    + "KeyboardHookThread.InstallHook. Windows delivers the callback via the pump "
                    + "of whatever thread ran SetWindowsHookEx; from any thread that can block, "
                    + "that stalls every keystroke on the machine whenever it does — which is "
                    + "exactly what a stuck connect did on 2026-08-29 (#402).");
            }

            // Positive control: the scan must SEE the two known consumers, or
            // a green run proves only that the walk missed them.
            foreach (var consumer in Consumers)
            {
                Assert.Contains(installers,
                    f => Rel(f).Equals(consumer, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// The pump lives in exactly one place. A consumer that grows its own
        /// thread or its own pump is a second copy of the machinery this
        /// exists to centralize — and the next candidate to end up somewhere
        /// blockable.
        /// </summary>
        [Fact]
        public void ThePumpAndTheThreadLiveOnlyInTheHost()
        {
            string host = Read(Host);
            Assert.Contains("new Thread(", host, StringComparison.Ordinal);
            Assert.Contains("IsBackground = true", host, StringComparison.Ordinal);
            Assert.Contains("Dispatcher.Run()", host, StringComparison.Ordinal);

            foreach (var consumer in Consumers)
            {
                string text = Read(consumer);
                Assert.False(text.Contains("new Thread(", StringComparison.Ordinal),
                    consumer + " creates its own thread. The hook thread is the host's alone; a "
                    + "consumer that spawns one is rebuilding the machinery beside it.");
                Assert.False(text.Contains("Dispatcher.Run", StringComparison.Ordinal),
                    consumer + " runs its own message pump. Only KeyboardHookThread pumps.");
            }
        }

        /// <summary>
        /// Nothing on the callback path may block or marshal synchronously.
        /// A synchronous hop to the UI thread would simply move the coupling:
        /// the hook thread would then wait on the blocked thread, and every
        /// keystroke on the machine would wait on the hook thread.
        /// </summary>
        [Fact]
        public void TheCallbackPathContainsNoBlockingMarshal()
        {
            foreach (var file in new[] { Host }.Concat(Consumers))
            {
                string text = Read(file);
                var hits = FindBlockingPatterns(text);
                Assert.True(hits.Count == 0,
                    file + " contains " + string.Join(", ", hits) + ". The hook thread must never "
                    + "wait on application work: hand slow work off with Task.Run or a BeginInvoke "
                    + "post and return; read snapshots or volatile flags instead of asking another "
                    + "thread. (Thread.Join in the host's Shutdown is the one legitimate bounded "
                    + "wait — it runs on the dying process's exit path, never on the hook thread, "
                    + "and is deliberately not in this list.)");
            }
        }

        /// <summary>
        /// The hooks come OUT again, on the thread that owns them. Before
        /// #402, HelpLauncher declared UnhookWindowsHookEx and never called
        /// it — a P/Invoke import with one occurrence in the file is exactly
        /// that regression, which is why this counts rather than merely
        /// checking presence.
        /// </summary>
        [Fact]
        public void EveryHookIsTornDownAndTheHostDrivesTheTeardown()
        {
            foreach (var consumer in Consumers)
            {
                string text = Read(consumer);
                int occurrences = CountOf(text, "UnhookWindowsHookEx");
                Assert.True(occurrences >= 2,
                    consumer + " mentions UnhookWindowsHookEx " + occurrences + " time(s). One is "
                    + "just the extern declaration; the teardown call is missing, so the hook "
                    + "would outlive its pump.");
            }

            string host = Read(Host);
            Assert.Contains("ProcessExit", host, StringComparison.Ordinal);
            Assert.Contains("ShutdownStarted", host, StringComparison.Ordinal);
        }

        /// <summary>
        /// The positive control for the blocking-pattern detector: prove it
        /// flags what it claims to forbid and passes what it claims to allow,
        /// so a green run over the real files means the files are clean — not
        /// that the detector is blind.
        /// </summary>
        [Fact]
        public void TheBlockingPatternDetectorFlagsBadAndPassesGood()
        {
            string knownBad =
                "void Callback() { Application.Current.Dispatcher.Invoke(() => Cancel()); "
                + "Thread.Sleep(50); var x = task.Result; op.Wait(); "
                + "d.Invoke(DispatcherPriority.Send, work); "
                + "SendMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }";
            var flagged = FindBlockingPatterns(knownBad);
            Assert.Contains(flagged, h => h.Contains("Dispatcher.Invoke"));
            Assert.Contains(flagged, h => h.Contains("Thread.Sleep"));
            Assert.Contains(flagged, h => h.Contains(".Result"));
            Assert.Contains(flagged, h => h.Contains(".Wait("));
            Assert.Contains(flagged, h => h.Contains("Invoke(DispatcherPriority"));
            Assert.Contains(flagged, h => h.Contains("SendMessage("));

            string knownGood =
                "void Callback() { var c = _cancel; if (c != null) _ = Task.Run(c); "
                + "bool busy = _cwActive?.Invoke() ?? false; "
                + "dispatcher.BeginInvoke(wrapped); "
                + "dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); "
                + "PostMessage(fg, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }";
            Assert.Empty(FindBlockingPatterns(knownGood));
        }

        /// <summary>
        /// The positive control for the file reader itself, in the style of
        /// CountdownKeyUpRuleTests: a scan that reads the wrong file passes
        /// for the wrong reason.
        /// </summary>
        [Fact]
        public void TheSourceReaderFindsWhatIsThereAndNotWhatIsNot()
        {
            Assert.Contains("Dispatcher.Run()", Read(Host), StringComparison.Ordinal);
            Assert.Contains("WH_KEYBOARD_LL", Read(Consumers[0]), StringComparison.Ordinal);
            Assert.Contains("WH_KEYBOARD_LL", Read(Consumers[1]), StringComparison.Ordinal);
            Assert.DoesNotContain("NoSuchHookSymbol", Read(Host), StringComparison.Ordinal);
        }

        // ── machinery ───────────────────────────────────────────────────

        private static readonly (Regex Pattern, string Name)[] BlockingPatterns =
        {
            (new Regex(@"[Dd]ispatcher\s*\.\s*Invoke\s*\("), "Dispatcher.Invoke( — a synchronous marshal"),
            (new Regex(@"(?<!Begin)Invoke\s*\(\s*DispatcherPriority"), "Invoke(DispatcherPriority — the priority overload of the same synchronous marshal"),
            (new Regex(@"Thread\s*\.\s*Sleep"), "Thread.Sleep"),
            (new Regex(@"\.Wait\s*\("), ".Wait( — a blocking wait"),
            (new Regex(@"\.Result\b"), ".Result — sync-over-async"),
            (new Regex(@"GetResult\s*\(\s*\)"), "GetResult() — sync-over-async"),
            (new Regex(@"(?<![A-Za-z])SendMessage\s*\("), "SendMessage( — the synchronous window message; post it instead"),
        };

        private static List<string> FindBlockingPatterns(string text)
        {
            var hits = new List<string>();
            foreach (var (pattern, name) in BlockingPatterns)
            {
                if (pattern.IsMatch(text))
                    hits.Add(name);
            }
            return hits;
        }

        private static int CountOf(string text, string needle)
        {
            int count = 0, at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }

        /// <summary>
        /// Every non-test C#/VB source in the tree, vendor included on
        /// purpose: a vendored FlexLib that grew a global hook would be the
        /// same machine-wide hazard, and it should fail loudly here so a
        /// person looks, rather than ship quietly. Test projects are excluded
        /// because this file itself names the forbidden symbols in strings.
        /// </summary>
        private static IEnumerable<string> SourceFiles()
        {
            string root = RepoRoot();
            var skip = new[] { "\\bin\\", "\\obj\\", "\\.git\\", "\\.vs\\", "\\packages\\" };
            foreach (var pattern in new[] { "*.cs", "*.vb" })
            {
                foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                {
                    string tail = file.Substring(root.Length);
                    if (skip.Any(s => tail.Contains(s, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (tail.Contains(".Tests", StringComparison.OrdinalIgnoreCase))
                        continue;
                    yield return file;
                }
            }
        }

        private static string Rel(string absolute)
        {
            string root = RepoRoot();
            return absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? absolute.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/')
                : absolute;
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that cannot find "
                + "its subject proves nothing about it.");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
