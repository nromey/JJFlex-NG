using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #402, second lockout (2026-08-29): the connect phase must never block
    /// the UI thread, and the wrapper that guarantees that must never be able
    /// to quietly disarm itself. This file pins both, by source scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What happened.</b> Sprint 42 Track A wrapped every blocking connect
    /// call (Start, TryAutoConnect, RetryConnect) in
    /// <c>RunConnectPhaseOffUiThread</c>, which spins a worker and pumps the
    /// UI thread. It shipped with no assertion that the wrapper was actually
    /// on the path — and the same night, the operator's startup connect ran
    /// the 45-second station-name wait INLINE on the UI thread, three times,
    /// 132 seconds with no speech, no keyboard and no cancel, ending with his
    /// own radio disposed. He killed the process. He is blind; a frozen
    /// application is indistinguishable from a crashed one.
    /// </para>
    /// <para>
    /// <b>Why nothing was unwrapped and it still happened.</b> Every call
    /// site went through the wrapper. The wrapper's first line was
    /// <c>If Not Application.MessageLoop Then Return work()</c> — meant as
    /// "already off the UI thread, just run it". But MessageLoop asks whether
    /// a WinForms message loop is running on this thread RIGHT NOW, and the
    /// startup connect runs inside MyApplication_Startup, BEFORE
    /// Application.Run — on the very thread that becomes the message-loop
    /// thread the moment startup returns. The guard read "no loop" as "not
    /// the UI thread" and ran everything inline. The trace signature: zero
    /// threads named ConnectPhase in the whole session, and FlexBase.Start's
    /// own lines tagged with the UI thread's id.
    /// </para>
    /// <para>
    /// <b>The rule, then, has two halves.</b> One: every call site of the
    /// blocking trio stays inside the wrapper (the scan Track A never had).
    /// Two: the wrapper decides "am I the UI thread" by THREAD IDENTITY,
    /// captured at startup, never by inferring it from runtime state that is
    /// legitimately absent during the one connect every session begins with.
    /// </para>
    /// <para>
    /// Same shape as <see cref="CountdownKeyUpRuleTests"/>: source scans with
    /// a positive control, because a scan that reads nothing — wrong path,
    /// renamed file, regex that matches nothing — passes for the wrong
    /// reason, and this file exists precisely because an absence looked like
    /// success once already.
    /// </para>
    /// </remarks>
    public sealed class ConnectPhaseUiThreadRuleTests
    {
        private const string Globals = "globals.vb";
        private const string AppEvents = "ApplicationEvents.vb";
        private const string Wrapper = "RunConnectPhaseOffUiThread";
        private const string TesterDialog = "JJFlexWpf/Dialogs/ConnectionTesterDialog.xaml.cs";

        /// <summary>
        /// Matches a rig-flavoured receiver calling Start(): the wrapped
        /// locals (walkRig, startingRig, retryingRig, autoRetryRig) and the
        /// module-level RigControl alike. Timers, threads and audio sessions
        /// have no "rig" in their names, so they stay out of scope.
        /// </summary>
        private static readonly Regex RigStart =
            new(@"\b\w*[Rr]ig\w*\.Start\(\)", RegexOptions.IgnoreCase);

        /// <summary>
        /// HALF ONE: no call site of the blocking trio sits outside the
        /// wrapper in globals.vb. A seventh unwrapped path is exactly what
        /// everyone went looking for on 2026-08-29 — this makes the looking
        /// mechanical, and makes adding an eighth impossible to do quietly.
        /// </summary>
        [Fact]
        public void EveryBlockingConnectCallSiteInGlobalsGoesThroughTheWrapper()
        {
            var code = CodeLines(Read(Globals)).ToList();

            AssertAllWrapped(code, l => l.Contains(".TryAutoConnect(", StringComparison.Ordinal),
                "TryAutoConnect", minimum: 3);
            AssertAllWrapped(code, l => l.Contains(".RetryConnect(", StringComparison.Ordinal),
                "RetryConnect", minimum: 1);
            AssertAllWrapped(code, l => RigStart.IsMatch(l),
                "a rig's Start()", minimum: 4);
        }

        private static void AssertAllWrapped(
            List<string> codeLines, Func<string, bool> isCallSite, string what, int minimum)
        {
            var sites = codeLines.Where(isCallSite).ToList();

            // The positive control half: a scan that found fewer call sites
            // than are known to exist is reading the wrong thing, and its
            // "nothing unwrapped" verdict is worthless.
            Assert.True(sites.Count >= minimum,
                $"Expected at least {minimum} call site(s) of {what} in {Globals} and found "
                + $"{sites.Count}. Either the connect flow moved out of {Globals} — in which "
                + "case this scan must follow it — or the scan is no longer reading what it "
                + "thinks it is. A scan that sees nothing proves nothing.");

            var unwrapped = sites.Where(l => !l.Contains(Wrapper, StringComparison.Ordinal)).ToList();
            Assert.True(unwrapped.Count == 0,
                $"{unwrapped.Count} call site(s) of {what} in {Globals} sit outside "
                + $"{Wrapper}:\n  " + string.Join("\n  ", unwrapped.Select(l => l.Trim()))
                + "\nEvery blocking connect call runs off the UI thread. The station-name wait "
                + "alone is a 45-second budget, retried — on the UI thread that is no speech, "
                + "no keys and no cancel for a blind operator (#402).");
        }

        /// <summary>
        /// HALF TWO — the one that would have caught the second lockout. The
        /// wrapper's "am I the UI thread" question is answered by thread
        /// identity captured at startup, never by Application.MessageLoop,
        /// which is False during the whole of MyApplication_Startup while the
        /// startup connect runs on the UI thread regardless.
        /// </summary>
        [Fact]
        public void TheWrapperDecidesByThreadIdentityNotByMessageLoop()
        {
            string body = WrapperBody();
            var code = string.Join("\n", CodeLines(body));

            Assert.DoesNotContain("MessageLoop", code, StringComparison.Ordinal);

            Assert.Contains("UiThreadId", code, StringComparison.Ordinal);
            Assert.Contains("Environment.CurrentManagedThreadId", code, StringComparison.Ordinal);

            // And the polarity: inline execution only when the UI thread is
            // KNOWN and this is provably not it. An unknown identity pumps,
            // because pumping needlessly costs a little overhead while
            // running inline wrongly is the 45-second lockout itself.
            Assert.Contains("UiThreadId >= 0", code, StringComparison.Ordinal);
        }

        /// <summary>
        /// The identity the wrapper keys off is captured in
        /// MyApplication_Startup BEFORE InitializeApplication runs — because
        /// InitializeApplication is what reaches openTheRadio(True), the
        /// startup connect, the exact leg that locked the operator out.
        /// </summary>
        [Fact]
        public void TheUiThreadIdentityIsCapturedBeforeTheStartupConnectCanRun()
        {
            var code = string.Join("\n", CodeLines(Read(AppEvents)));

            int captured = code.IndexOf("UiThreadId = Environment.CurrentManagedThreadId",
                StringComparison.Ordinal);
            int initialize = code.IndexOf("InitializeApplication()", StringComparison.Ordinal);

            Assert.True(captured >= 0,
                $"{AppEvents} no longer captures UiThreadId from "
                + "Environment.CurrentManagedThreadId. Without the capture the wrapper cannot "
                + "recognise the UI thread during startup, when no message loop exists to infer "
                + "it from — and the startup connect is the leg that froze on 2026-08-29.");
            Assert.True(initialize >= 0,
                $"InitializeApplication() is no longer called from {AppEvents}. If the startup "
                + "sequence moved, move this scan with it — the ordering it proves still has to "
                + "hold wherever the first connect now begins.");
            Assert.True(captured < initialize,
                "UiThreadId is captured AFTER InitializeApplication() in " + AppEvents + ". "
                + "InitializeApplication reaches openTheRadio(True) — the startup connect — so "
                + "a capture after it arms the wrapper one connect too late, and the one connect "
                + "it misses is the one that locked the operator out.");
        }

        /// <summary>
        /// THE ENUMERATION, not the inspection. Walk every non-vendor,
        /// non-test source in the repository: the only files calling
        /// TryAutoConnect or RetryConnect are globals.vb (wrapped, proven
        /// above) and ConnectionTester.cs (run on a dedicated worker thread,
        /// proven below). A new caller anywhere else fails here and has to
        /// prove its threading before it ships.
        /// </summary>
        [Fact]
        public void NoOtherSourceCallsTheBlockingConnectTrio()
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "globals.vb",
                Path.Combine("Radios", "ConnectionTester.cs"),
            };

            var offenders = new List<string>();
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in SourceFiles())
            {
                bool calls = CodeLines(File.ReadAllText(file)).Any(l =>
                    l.Contains(".TryAutoConnect(", StringComparison.Ordinal)
                    || l.Contains(".RetryConnect(", StringComparison.Ordinal));
                if (!calls) continue;

                string relative = Path.GetRelativePath(RepoRoot(), file);
                found.Add(relative);
                if (!allowed.Contains(relative)) offenders.Add(relative);
            }

            // Positive control: the walker must SEE the callers known to
            // exist, or its clean verdict is a walker that read nothing.
            foreach (string known in allowed)
            {
                Assert.True(found.Contains(known),
                    $"The repository walk did not find the known call sites in {known}. Either "
                    + "the connect calls moved (update the allowed set and prove the new home's "
                    + "threading) or the walk is broken — and a broken walk passing the "
                    + "no-other-callers assertion is exactly the false green this file exists "
                    + "to prevent.");
            }

            Assert.True(offenders.Count == 0,
                "New caller(s) of TryAutoConnect/RetryConnect outside the proven files:\n  "
                + string.Join("\n  ", offenders)
                + "\nEvery route to the blocking connect trio must run off the UI thread or be "
                + "unable to block. Wrap it in RunConnectPhaseOffUiThread, run it on a dedicated "
                + "worker like ConnectionTester, or document here why it cannot block — then add "
                + "it to the allowed set. The seventh path is always the one nobody enumerated.");
        }

        /// <summary>
        /// The one allowed caller outside globals.vb: the Connection Tester
        /// runs its whole pass — TryAutoConnect included — on a dedicated STA
        /// worker thread. Pin that, so the allowance above stays earned.
        /// </summary>
        [Fact]
        public void TheConnectionTesterRunsOnItsOwnThread()
        {
            var code = string.Join("\n", CodeLines(Read(TesterDialog)));

            Assert.Contains("new Thread(() => _tester.Run())", code, StringComparison.Ordinal);
            Assert.Contains("testThread.Start()", code, StringComparison.Ordinal);
        }

        /// <summary>
        /// The C# side holds no route of its own to a rig's Start(): the only
        /// rig handle it has is RigControl, and it never starts it. Connect
        /// flow stays in globals.vb, where the wrapper scan watches it.
        /// </summary>
        [Fact]
        public void TheWpfSideNeverStartsTheRigItself()
        {
            var offenders = new List<string>();
            foreach (string file in SourceFiles().Where(f =>
                Path.GetRelativePath(RepoRoot(), f).StartsWith("JJFlexWpf", StringComparison.OrdinalIgnoreCase)))
            {
                if (CodeLines(File.ReadAllText(file)).Any(l =>
                    l.Contains("RigControl.Start()", StringComparison.Ordinal)))
                {
                    offenders.Add(Path.GetRelativePath(RepoRoot(), file));
                }
            }

            Assert.True(offenders.Count == 0,
                "RigControl.Start() called from the WPF side:\n  "
                + string.Join("\n  ", offenders)
                + "\nStart() can sit in a 45-second station-name wait. If the WPF side needs a "
                + "connect, it raises the VB-side flow (SelectRadioCallback), whose call sites "
                + "are wrapped and scanned.");
        }

        /// <summary>
        /// The positive control for the readers themselves: they find what is
        /// known to be present and discriminate against what is known not to
        /// be. Every assertion above is a source scan, and a scan reading the
        /// wrong file passes for the wrong reason.
        /// </summary>
        [Fact]
        public void TheSourceReaderFindsWhatIsThereAndNotWhatIsNot()
        {
            string globals = Read(Globals);
            string appEvents = Read(AppEvents);

            Assert.Contains(Wrapper, globals, StringComparison.Ordinal);
            Assert.Contains("MyApplication_Startup", appEvents, StringComparison.Ordinal);

            Assert.DoesNotContain("NoSuchConnectPhaseSymbol", globals, StringComparison.Ordinal);
            Assert.DoesNotContain("NoSuchConnectPhaseSymbol", appEvents, StringComparison.Ordinal);

            // The comment-stripper earns its keep: prose may say MessageLoop
            // (the wrapper's own comment explains the trap by name), and the
            // stripper is what keeps that prose from being read as code.
            Assert.Contains("MessageLoop", WrapperBody(), StringComparison.Ordinal);
            Assert.DoesNotContain("MessageLoop",
                string.Join("\n", CodeLines(WrapperBody())), StringComparison.Ordinal);
        }

        // ── plumbing ────────────────────────────────────────────────────────

        /// <summary>
        /// The wrapper's own text, signature to End Function. Scoped so the
        /// MessageLoop prohibition judges the wrapper's code, not the rest of
        /// a ten-thousand-line module.
        /// </summary>
        private static string WrapperBody()
        {
            string source = Read(Globals);
            int start = source.IndexOf("Private Function " + Wrapper, StringComparison.Ordinal);
            Assert.True(start >= 0,
                Wrapper + " is gone from " + Globals + ". If the connect phase found a better "
                + "off-UI-thread mechanism, port these assertions to it — the rule outlives the "
                + "function.");
            int end = source.IndexOf("End Function", start, StringComparison.Ordinal);
            Assert.True(end > start, "Could not find the end of " + Wrapper + ".");
            return source.Substring(start, end - start);
        }

        /// <summary>
        /// Lines that are code: whole-line VB (') and C# (//) comments
        /// removed. Trailing comments survive, which is acceptable — every
        /// pattern this file scans for is call-shaped, and prose does not
        /// write call syntax by accident.
        /// </summary>
        private static IEnumerable<string> CodeLines(string source)
        {
            foreach (string raw in source.Split('\n'))
            {
                string t = raw.TrimStart();
                if (t.StartsWith("'", StringComparison.Ordinal)) continue;
                if (t.StartsWith("//", StringComparison.Ordinal)) continue;
                yield return raw;
            }
        }

        private static IEnumerable<string> SourceFiles()
        {
            string root = RepoRoot();
            var skip = new[] { "FlexLib_API", ".git", "bin", "obj", "packages" };

            IEnumerable<string> Walk(string dir)
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (skip.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (name.Contains("Tests", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (string f in Walk(sub)) yield return f;
                }
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    if (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return f;
                    }
                }
            }

            return Walk(root);
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that cannot "
                + "find its subject proves nothing about it.");
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
