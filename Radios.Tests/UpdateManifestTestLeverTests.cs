using System;
using System.IO;
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
    /// localhost or 127.0.0.1 by parse, never by prefix. Pure decisions plus
    /// the start-up call site; no network, no window, and nothing here moves
    /// the process-wide URL.
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
            // And applying it there changes nothing and says nothing.
            Assert.Null(UpdaterEndpoints.ApplyTestAppManifestUrl(Local, runRelocatedByConfigDir: false));
            Assert.Equal(UpdaterEndpoints.ProductionAppManifestUrl, UpdaterEndpoints.AppManifestUrl);
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

        [Fact]
        public void The_gate_is_an_accepted_override_not_any_relocation()
        {
            string temp = Path.Combine(Path.GetTempPath(), "jjflex-lever-" + Guid.NewGuid().ToString("N"));
            // Accepted: the run's root IS the override.
            Assert.True(LocalDataRoot.RelocatedByConfigDir(temp, temp));
            Assert.True(LocalDataRoot.RelocatedByConfigDir("  " + temp + "\\  ", temp.ToUpperInvariant()));
            // No override at all: never, even when the root is not the ordinary
            // one (an unresolvable roaming folder reads as relocated, and must
            // not engage this).
            Assert.False(LocalDataRoot.RelocatedByConfigDir(null, temp));
            Assert.False(LocalDataRoot.RelocatedByConfigDir("", temp));
            // Refused: the root fell back to something other than the override.
            string standard = Path.Combine(Path.GetTempPath(), "JJFlexRadio");
            Assert.False(LocalDataRoot.RelocatedByConfigDir("relative\\dir", standard));
            Assert.False(LocalDataRoot.RelocatedByConfigDir(temp, standard));
            Assert.False(LocalDataRoot.RelocatedByConfigDir(temp, ""));
            // Positive control for the "unresolvable roaming folder" case:
            // the broader rule calls that relocated.
            Assert.True(LocalDataRoot.IsRelocated(temp, null));
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
