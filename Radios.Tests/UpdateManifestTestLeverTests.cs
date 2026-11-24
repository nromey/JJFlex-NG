using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using JJFlexUpdater;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The update manifest URL was a constant, so no harness run could stage
    /// an in-app update. It is now a property whose default is production,
    /// moved only by a testing lever: <c>JJFLEX_TEST_APP_MANIFEST_URL</c>,
    /// read only on a run whose settings tree <c>JJFLEX_CONFIG_DIR</c> moved
    /// and that move was accepted, and taken only when it is plain http to
    /// localhost or 127.0.0.1 by parse, never by prefix. No network and no
    /// window.
    /// <para>
    /// <b>Process-wide state never leaks out of this class.</b> The URL and
    /// the cached settings root are statics. Every test that could move them,
    /// or must read the live gate under a state other than this process's own,
    /// does it in a fresh copy of Radios.dll or JJFlexUpdater.dll loaded into
    /// its own collectible <see cref="AssemblyLoadContext"/>: that copy has its
    /// own statics, so nothing it sets is seen by any other test, and the
    /// context is unloaded afterwards. Nothing here writes the environment.
    /// The only main-process static read is the URL's default, which no test
    /// in this class moves.
    /// </para>
    /// </summary>
    public sealed class UpdateManifestTestLeverTests
    {
        private const string Local = "http://localhost:8765/jjflex-app-manifest.json";

        [Fact]
        public void The_default_is_the_production_manifest()
        {
            Assert.Equal("https://data.jjflexible.radio/jjflex-app-manifest.json", UpdaterEndpoints.ProductionAppManifestUrl);
            // Nothing in this test assembly applies the lever, so the live
            // value is still the default.
            Assert.Equal(UpdaterEndpoints.ProductionAppManifestUrl, UpdaterEndpoints.AppManifestUrl);
        }

        [Fact]
        public void An_ordinary_run_ignores_the_variable_entirely()
        {
            Assert.Null(UpdaterEndpoints.DecideTestAppManifestUrl(Local, runRelocatedByConfigDir: false, out string line));
            Assert.Null(line);
            // And applying it there changes nothing and says nothing (in an
            // isolated copy, so a broken Apply cannot leak a moved URL).
            (string? applied, string url) = ApplyInIsolatedUpdater(Local, runRelocatedByConfigDir: false);
            Assert.Null(applied);
            Assert.Equal(UpdaterEndpoints.ProductionAppManifestUrl, url);
        }

        [Fact]
        public void An_isolated_run_with_nothing_offered_says_nothing()
        {
            Assert.Null(UpdaterEndpoints.DecideTestAppManifestUrl(null, runRelocatedByConfigDir: true, out string a));
            Assert.Null(a);
            Assert.Null(UpdaterEndpoints.DecideTestAppManifestUrl("  ", runRelocatedByConfigDir: true, out string b));
            Assert.Null(b);
        }

        [Theory]
        [InlineData("http://localhost:8765/jjflex-app-manifest.json")]
        [InlineData("http://LOCALHOST/m.json")]
        [InlineData("http://127.0.0.1:8080/m.json")]
        [InlineData("  http://127.0.0.1/m.json  ")]
        public void An_isolated_run_takes_local_http_and_traces_that_it_did(string offered)
        {
            string url = UpdaterEndpoints.DecideTestAppManifestUrl(offered, runRelocatedByConfigDir: true, out string line);
            Assert.Equal(offered.Trim(), url);
            Assert.NotNull(line);
            Assert.Contains("TESTING app manifest in use", line);
            Assert.Contains(offered.Trim(), line);
        }

        [Theory]
        [InlineData("http://localhost.example.com/m.json", "host")]
        [InlineData("http://127.0.0.1@elsewhere/m.json", "user information")]
        [InlineData("https://localhost/m.json", "scheme")]
        [InlineData("file:///C:/temp/m.json", "scheme")]
        [InlineData("http://data.jjflexible.radio/m.json", "host")]
        [InlineData("http://127.0.0.2/m.json", "host")]
        [InlineData("http://[::1]/m.json", "host")]
        [InlineData("localhost/m.json", "absolute")]
        public void An_isolated_run_refuses_anything_else_and_traces_the_refusal(string offered, string why)
        {
            Assert.Null(UpdaterEndpoints.DecideTestAppManifestUrl(offered, runRelocatedByConfigDir: true, out string line));
            Assert.NotNull(line);
            Assert.Contains("refused", line);
            Assert.Contains(why, line);
            Assert.Contains(UpdaterEndpoints.ProductionAppManifestUrl, line);
            Assert.False(UpdaterEndpoints.IsLocalHttpUrl(offered, out _));
        }

        [Fact]
        public void A_prefix_match_is_not_enough()
        {
            // Instrument control: both of these START with an accepted prefix.
            Assert.StartsWith("http://localhost", "http://localhost.example.com/");
            Assert.StartsWith("http://127.0.0.1", "http://127.0.0.1@elsewhere/");
            Assert.False(UpdaterEndpoints.IsLocalHttpUrl("http://localhost.example.com/", out _));
            Assert.False(UpdaterEndpoints.IsLocalHttpUrl("http://127.0.0.1@elsewhere/", out _));
            Assert.True(UpdaterEndpoints.IsLocalHttpUrl("http://localhost/", out _));
        }

        // ── The gate: relocated BECAUSE JJFLEX_CONFIG_DIR was set and accepted ──

        // Every case goes through the REAL resolver,
        // RadioConfig.ResolveStartupDirectory, with a synthetic standard folder,
        // so no test names the operator's live settings folder.

        private static string Scratch(string name) =>
            Path.Combine(Path.GetTempPath(), "jjflex-lever-" + Guid.NewGuid().ToString("N"), name);

        [Fact]
        public void Naming_the_normal_folder_is_refused_so_the_gate_stays_shut_and_production_stays()
        {
            string standard = Scratch("JJFlexRadio");
            // The resolver refuses it and falls back to that same folder, so the
            // run's root and the offered path are EQUAL. That equality is what
            // the first gate mistook for acceptance.
            string root = RadioConfig.ResolveStartupDirectory(standard, standard, out bool taken, out string? refusal);
            Assert.False(taken);
            Assert.NotNull(refusal);
            Assert.Equal(standard, root);

            bool gate = LocalDataRoot.RelocatedByConfigDir(standard, standard, root);
            Assert.False(gate);
            Assert.False(LocalDataRoot.RelocatedByConfigDir(standard.ToUpperInvariant() + "\\", standard, root));

            // So the production manifest is the one fetched, and nothing is said.
            (string? line, string url) = ApplyInIsolatedUpdater(Local, gate);
            Assert.Null(line);
            Assert.Equal(UpdaterEndpoints.ProductionAppManifestUrl, url);
        }

        [Fact]
        public void An_accepted_temporary_root_opens_the_gate_and_the_local_manifest_is_used()
        {
            // The positive control for the test above: the same resolver and
            // standard folder, offered an override it takes.
            string standard = Scratch("JJFlexRadio");
            string temp = Scratch("isolated");
            string root = RadioConfig.ResolveStartupDirectory(standard, temp, out bool taken, out _);
            Assert.True(taken);

            bool gate = LocalDataRoot.RelocatedByConfigDir(temp, standard, root);
            Assert.True(gate);
            Assert.True(LocalDataRoot.RelocatedByConfigDir("  " + temp + "\\  ", standard, root.ToUpperInvariant()));

            (string? line, string url) = ApplyInIsolatedUpdater(Local, gate);
            Assert.Equal(Local, url);
            Assert.NotNull(line);
            Assert.Contains("TESTING app manifest in use", line);
        }

        [Fact]
        public void A_relative_override_is_refused_even_when_it_would_land_on_the_root()
        {
            // A relative path depends on the working directory, so it is refused
            // wherever it lands. Here it lands EXACTLY on the root, so only the
            // resolver's rooted-path rule can shut the gate: a gate that roots
            // the path itself, or compares paths instead of asking, opens it.
            string standard = Scratch("JJFlexRadio");
            string relative = "jjflex-lever-relative-" + Guid.NewGuid().ToString("N");
            Assert.False(Path.IsPathRooted(relative));
            string landsOn = Path.GetFullPath(relative);

            Assert.False(LocalDataRoot.RelocatedByConfigDir(relative, standard, landsOn));
            // Positive control: the same place offered as a full path is taken.
            Assert.True(LocalDataRoot.RelocatedByConfigDir(landsOn, standard, landsOn));
        }

        [Fact]
        public void An_accepted_override_that_is_not_the_cached_root_keeps_the_gate_shut()
        {
            // The variable changed after the settings root was cached: the
            // resolver would take the new value, but this run's stores are on
            // another root, so the run is not isolated where the variable says.
            string standard = Scratch("JJFlexRadio");
            string offered = Scratch("offered");
            Assert.False(LocalDataRoot.RelocatedByConfigDir(offered, standard, Scratch("cached")));
            Assert.False(LocalDataRoot.RelocatedByConfigDir(offered, standard, standard));
            Assert.False(LocalDataRoot.RelocatedByConfigDir(offered, standard, ""));
            Assert.False(LocalDataRoot.RelocatedByConfigDir(offered, standard, null));
            // Positive control: the cached root IS the override.
            Assert.True(LocalDataRoot.RelocatedByConfigDir(offered, standard, offered));
        }

        [Fact]
        public void No_override_never_opens_the_gate_whatever_the_root()
        {
            string standard = Scratch("JJFlexRadio");
            string elsewhere = Scratch("elsewhere");
            Assert.False(LocalDataRoot.RelocatedByConfigDir(null, standard, elsewhere));
            Assert.False(LocalDataRoot.RelocatedByConfigDir("", standard, elsewhere));
            Assert.False(LocalDataRoot.RelocatedByConfigDir("   ", standard, elsewhere));
            // An unresolvable roaming folder reads as relocated to the broader
            // rule, and must not engage this one.
            Assert.True(LocalDataRoot.IsRelocated(elsewhere, null));
        }

        // ── The live gate property, read in an isolated copy of Radios.dll ──

        [Fact]
        public void The_live_gate_opens_on_this_process_s_accepted_override()
        {
            // This test process runs with JJFLEX_CONFIG_DIR naming its own
            // throwaway tree (TestSettingsRoot), which the resolver accepts.
            Assert.Null(TestSettingsRoot.Failure);
            Assert.False(string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(RadioConfig.ConfigDirOverrideVariable)));

            (bool gate, string? cached) = LiveGateInIsolatedRadios(preloadedRoot: null);
            Assert.True(gate);
            // Reading the gate filled the copy's cache, with the override.
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(TestSettingsRoot.Directory),
                Path.TrimEndingDirectorySeparator(cached ?? ""),
                ignoreCase: true);
        }

        [Fact]
        public void The_live_gate_stays_shut_when_the_cached_root_is_not_the_override()
        {
            // The same environment as the test above, but the copy's settings
            // root was cached somewhere else first. The live property must say
            // no: one that is always true, or that ignores the cached root, goes
            // red here.
            string cachedElsewhere = Scratch("cached-elsewhere");
            (bool gate, string? cached) = LiveGateInIsolatedRadios(preloadedRoot: cachedElsewhere);
            Assert.Equal(cachedElsewhere, cached);
            Assert.False(gate);
        }

        // ── Apply moves the URL, in an isolated copy of JJFlexUpdater.dll ──

        [Fact]
        public void Apply_on_an_isolated_run_moves_the_url_the_fetcher_reads()
        {
            (string? line, string url) = ApplyInIsolatedUpdater(Local, runRelocatedByConfigDir: true);
            Assert.Equal(Local, url);
            Assert.NotNull(line);
            // A refused URL leaves production in place and says why.
            (string? refusedLine, string refusedUrl) =
                ApplyInIsolatedUpdater("http://localhost.example.com/m.json", runRelocatedByConfigDir: true);
            Assert.Equal(UpdaterEndpoints.ProductionAppManifestUrl, refusedUrl);
            Assert.Contains("refused", refusedLine);
            // This process's own URL never moved.
            Assert.Equal(UpdaterEndpoints.ProductionAppManifestUrl, UpdaterEndpoints.AppManifestUrl);
        }

        // ── Isolation helpers ──

        /// <summary>
        /// Apply the lever in a fresh copy of JJFlexUpdater.dll and read back
        /// that copy's URL. The copy's statics are its own, so a moved URL never
        /// reaches this process's <see cref="UpdaterEndpoints"/>.
        /// </summary>
        private static (string? line, string url) ApplyInIsolatedUpdater(string? offered, bool runRelocatedByConfigDir)
        {
            (string? line, string url) result = (null, "");
            WeakReference unloaded = InIsolatedContext(typeof(UpdaterEndpoints).Assembly.Location, assembly =>
            {
                Type endpoints = assembly.GetType(typeof(UpdaterEndpoints).FullName!, throwOnError: true)!;
                Assert.NotSame(typeof(UpdaterEndpoints), endpoints);
                MethodInfo apply = endpoints.GetMethod(nameof(UpdaterEndpoints.ApplyTestAppManifestUrl))!;
                PropertyInfo url = endpoints.GetProperty(nameof(UpdaterEndpoints.AppManifestUrl))!;
                Assert.Equal(UpdaterEndpoints.ProductionAppManifestUrl, (string)url.GetValue(null)!);
                string? line = (string?)apply.Invoke(null, new object?[] { offered, runRelocatedByConfigDir });
                result = (line, (string)url.GetValue(null)!);
            });
            WaitForUnload(unloaded);
            return result;
        }

        /// <summary>
        /// Read <see cref="LocalDataRoot.RunRelocatedByConfigDir"/> in a fresh
        /// copy of Radios.dll, with that copy's cached settings root optionally
        /// set first. Returns the gate and the copy's cached root afterwards.
        /// This process's own cached root and environment are never touched.
        /// </summary>
        private static (bool gate, string? cached) LiveGateInIsolatedRadios(string? preloadedRoot)
        {
            (bool gate, string? cached) result = (false, null);
            WeakReference unloaded = InIsolatedContext(typeof(LocalDataRoot).Assembly.Location, assembly =>
            {
                Type config = assembly.GetType(typeof(RadioConfig).FullName!, throwOnError: true)!;
                Type local = assembly.GetType(typeof(LocalDataRoot).FullName!, throwOnError: true)!;
                Assert.NotSame(typeof(LocalDataRoot), local);
                FieldInfo? cache = config.GetField("_appDataRoot", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.True(cache != null, "the settings root's cache field moved; this test sets it in an isolated copy");
                Assert.Null(cache!.GetValue(null));
                if (preloadedRoot != null) cache.SetValue(null, preloadedRoot);
                bool gate = (bool)local.GetProperty(nameof(LocalDataRoot.RunRelocatedByConfigDir))!.GetValue(null)!;
                result = (gate, (string?)cache.GetValue(null));
            });
            WaitForUnload(unloaded);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference InIsolatedContext(string assemblyPath, Action<Assembly> body)
        {
            var context = new AssemblyLoadContext("manifest-lever-" + Guid.NewGuid().ToString("N"), isCollectible: true);
            try
            {
                body(context.LoadFromAssemblyPath(assemblyPath));
            }
            finally
            {
                context.Unload();
            }
            return new WeakReference(context);
        }

        private static void WaitForUnload(WeakReference unloaded)
        {
            for (int i = 0; unloaded.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        // ── The wiring ──

        [Fact]
        public void The_fetcher_reads_the_property()
        {
            string fetcher = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexUpdater", "Net", "ManifestFetcher.cs"));
            Assert.Contains("FetchAppManifestAsync(UpdaterEndpoints.AppManifestUrl, cancellationToken)", fetcher);
        }

        [Fact]
        public void Start_up_reads_the_variable_only_behind_the_accepted_override_gate()
        {
            string events = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "ApplicationEvents.vb"));
            int gate = events.IndexOf("If Radios.LocalDataRoot.RunRelocatedByConfigDir Then", StringComparison.Ordinal);
            Assert.True(gate > 0, "the start-up gate is gone");
            int read = events.IndexOf("UpdaterEndpoints.TestAppManifestUrlVariable", StringComparison.Ordinal);
            int apply = events.IndexOf("UpdaterEndpoints.ApplyTestAppManifestUrl(", StringComparison.Ordinal);
            int endIf = events.IndexOf("End If", gate, StringComparison.Ordinal);
            Assert.True(read > gate && read < endIf, "the variable is read outside the gate");
            Assert.True(apply > gate && apply < endIf, "the lever is applied outside the gate");
            // Exactly one place in the app reads the variable, and applies it.
            Assert.Single(Regex.Matches(events, Regex.Escape("TestAppManifestUrlVariable")));
            Assert.Single(Regex.Matches(events, Regex.Escape("ApplyTestAppManifestUrl(")));
            // The gate is the accepted-override one, never the broader relocation.
            Assert.DoesNotContain("RunIsRelocated Then", events.Substring(gate - 200, 400));
            // Its line is traced after the diagnostic log opens.
            int init = events.IndexOf("            InitializeApplication()", StringComparison.Ordinal);
            int traced = events.IndexOf("JJTrace.Tracing.TraceLine(_testManifestTraceLine", StringComparison.Ordinal);
            Assert.True(init > apply && traced > init, "the lever's line is not traced after the log opens");
        }
    }
}
