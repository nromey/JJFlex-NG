using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The reverse gear on "Don't show this again" (task #267).
    /// </summary>
    /// <remarks>
    /// <para><b>What was wrong.</b> The store offered <c>IsSuppressed</c> and
    /// <c>Suppress</c> and nothing else. No unsuppress, no clear, no
    /// enumeration — so a message an operator silenced was gone for the life of
    /// the install, and Settings could not even list what had been lost because
    /// the API had no method to ask. It was already live on three surfaces, one
    /// of them a confirmation dialog.</para>
    ///
    /// <para><b>No collection attribute, on purpose.</b> Every test here builds
    /// its own store over its own temp file, so nothing touches
    /// <c>RadioConfig.BaseDirectory</c> or any other process-wide static and
    /// nothing has to be given back. That is a property of the design under
    /// test rather than of the test: the store takes a path, and
    /// <c>AdvisorySuppression</c> is a thin facade over one instance of it.</para>
    /// </remarks>
    public sealed class AdvisorySuppressionTests : IDisposable
    {
        private readonly string _dir;

        public AdvisorySuppressionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "jjflex-advisory-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* a temp dir; the OS sweeps it */ }
            catch (UnauthorizedAccessException) { }
        }

        private string FreshFile() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");

        /// <summary>
        /// A store whose labels are predictable, so an assertion about the list
        /// is an assertion about the store rather than about today's wording.
        /// </summary>
        private AdvisorySuppressionStore NewStore(string? path = null) =>
            new AdvisorySuppressionStore(path ?? FreshFile(), key => "The message called " + key);

        private static AdvisoryKey Key(string value) => new AdvisoryKey(value, "The message called " + value);

        // ---------------------------------------------------------------
        // The four the brief names.
        // ---------------------------------------------------------------

        [Fact]
        public void SuppressThenUnsuppressBringsTheMessageBack()
        {
            AdvisorySuppressionStore store = NewStore();
            AdvisoryKey key = Key("smartlink-setup");

            Assert.False(store.IsSuppressed(key));

            store.Suppress(key);
            Assert.True(store.IsSuppressed(key));

            Assert.True(store.Unsuppress(key.Value));
            Assert.False(store.IsSuppressed(key));
        }

        [Fact]
        public void ClearBringsEveryMessageBackAndSaysHowMany()
        {
            AdvisorySuppressionStore store = NewStore();
            store.Suppress(Key("smartlink-setup"));
            store.Suppress(Key("register|1234-5678"));
            store.Suppress(Key("still-running-at-exit-v1"));

            Assert.Equal(3, store.Clear());

            Assert.Equal(0, store.Count);
            Assert.Empty(store.Snapshot());
            Assert.False(store.IsSuppressed("smartlink-setup"));
            Assert.False(store.IsSuppressed("register|1234-5678"));
            Assert.False(store.IsSuppressed("still-running-at-exit-v1"));
        }

        [Fact]
        public void TheListReportsExactlyWhatWasSilencedAndNothingElse()
        {
            AdvisorySuppressionStore store = NewStore();
            store.Suppress(Key("smartlink-setup"));
            store.Suppress(Key("register|1234-5678"));

            IReadOnlyList<SuppressedAdvisory> listed = store.Snapshot();

            Assert.Equal(2, listed.Count);
            Assert.Equal(
                new[] { "register|1234-5678", "smartlink-setup" },
                listed.Select(e => e.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.DoesNotContain(listed, e => e.Key == "firmware|1234-5678|4.2.20.41343");
        }

        [Fact]
        public void UnsuppressingSomethingUnknownIsHarmless()
        {
            // A stale list, a second window, a hand-edited file. The operator's
            // intent — show this again — is already satisfied, so this is not
            // an error condition and must not be reported as one.
            AdvisorySuppressionStore store = NewStore();
            store.Suppress(Key("smartlink-setup"));

            Assert.False(store.Unsuppress("a-key-nobody-ever-silenced"));

            Assert.Equal(1, store.Count);
            Assert.True(store.IsSuppressed("smartlink-setup"));
        }

        // ---------------------------------------------------------------
        // The invariants that keep the list readable.
        // ---------------------------------------------------------------

        [Fact]
        public void AKeyCannotExistWithoutTheWordsThatNameIt()
        {
            // This is what makes a named list possible at all. Before #267 the
            // keys were bare strings written inline at the call site, so the
            // only honest thing Settings could have read out was
            // "no-physical-access-cascade-v1".
            Assert.Throws<ArgumentException>(() => new AdvisoryKey("some-key", ""));
            Assert.Throws<ArgumentException>(() => new AdvisoryKey("some-key", "   "));
            Assert.Throws<ArgumentException>(() => new AdvisoryKey("", "A label"));
        }

        [Fact]
        public void LabelsComeFromTheCodeSoRewordingReachesEntriesAlreadySilenced()
        {
            // The file holds keys and dates, never prose. A label written into
            // it would be a copy of words that live in code, and a copy is what
            // drifts. Same store, same file, new wording — the list follows.
            string path = FreshFile();
            new AdvisorySuppressionStore(path, k => "Old words for " + k).Suppress(Key("smartlink-setup"));

            var reworded = new AdvisorySuppressionStore(path, k => "New words for " + k);

            Assert.Equal("New words for smartlink-setup", reworded.Snapshot().Single().Label);
        }

        [Fact]
        public void EveryDeclaredKeyDescribesItselfWithTheSameWordsItWasBuiltWith()
        {
            // AdvisoryKeys.Describe is the catalogue read backwards, and this
            // is the check that keeps it honest. Add a factory and forget to
            // teach Describe about it, and its key falls through to the
            // "a message this version does not recognise" fallback — a wall of
            // identifiers read aloud, which is the thing #267 exists to stop.
            //
            // Reflective rather than a hand-written list, so a factory added
            // next sprint is covered without anyone remembering to come here.
            List<(string Source, AdvisoryKey Key)> declared = DeclaredKeys();

            Assert.NotEmpty(declared);
            foreach ((string source, AdvisoryKey key) in declared)
            {
                Assert.Equal(key.Label, AdvisoryKeys.Describe(key.Value));
                Assert.False(string.IsNullOrWhiteSpace(key.Label),
                    source + " produced a key with no label.");
            }
        }

        [Fact]
        public void AKeyFromSomewhereElseIsNamedRatherThanDropped()
        {
            // A retired advisory, a newer build's key, a hand-edited file. The
            // operator cannot restore what the list refuses to show him.
            string described = AdvisoryKeys.Describe("something-this-build-never-heard-of");

            Assert.False(string.IsNullOrWhiteSpace(described));
            Assert.Contains("something-this-build-never-heard-of", described, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------
        // The file.
        // ---------------------------------------------------------------

        [Fact]
        public void ChoicesSurviveARestart()
        {
            string path = FreshFile();
            NewStore(path).Suppress(Key("smartlink-setup"));

            Assert.True(NewStore(path).IsSuppressed("smartlink-setup"));
        }

        [Fact]
        public void AnUnsuppressSurvivesARestartToo()
        {
            // The half that never existed before. A restore that is not written
            // down is a message that comes back for one session and vanishes
            // again on the next launch — worse than no reverse gear, because
            // the operator would believe he had fixed it.
            string path = FreshFile();
            AdvisorySuppressionStore first = NewStore(path);
            first.Suppress(Key("smartlink-setup"));
            first.Unsuppress("smartlink-setup");

            Assert.False(NewStore(path).IsSuppressed("smartlink-setup"));
        }

        [Fact]
        public void TheOldFileFormatIsReadRatherThanThrownAway()
        {
            // Format 1 shipped as a bare array of key strings with no dates.
            // Discarding it on upgrade would restore advisories the operator
            // silenced on purpose — the same disrespect a blanket reset button
            // commits, just done to him without asking.
            string path = FreshFile();
            File.WriteAllText(path, "[ \"smartlink-setup\", \"register|1234-5678\" ]");

            AdvisorySuppressionStore store = NewStore(path);

            Assert.True(store.IsSuppressed("smartlink-setup"));
            Assert.True(store.IsSuppressed("register|1234-5678"));
            // No date to report, so none is invented.
            Assert.All(store.Snapshot(), e => Assert.Null(e.Silenced));
        }

        [Fact]
        public void ADamagedFileBringsTheAdvisoriesBackRatherThanTakingTheAppDown()
        {
            string path = FreshFile();
            File.WriteAllText(path, "{ not json at all");

            AdvisorySuppressionStore store = NewStore(path);

            Assert.Equal(0, store.Count);
            Assert.False(store.IsSuppressed("smartlink-setup"));
        }

        [Fact]
        public void TheListIsNewestFirstBecauseThatIsWhereAnAccidentIs()
        {
            AdvisorySuppressionStore store = NewStore();
            store.Suppress(Key("first"));
            store.Suppress(Key("second"));
            store.Suppress(Key("third"));

            string[] order = store.Snapshot().Select(e => e.Key).ToArray();

            Assert.Equal("third", order[0]);
            Assert.Equal("first", order[2]);
        }

        [Fact]
        public void SilencingTheSameThingTwiceKeepsTheFirstDateAndOneEntry()
        {
            AdvisorySuppressionStore store = NewStore();
            store.Suppress(Key("smartlink-setup"));
            DateTimeOffset? first = store.Snapshot().Single().Silenced;

            store.Suppress(Key("smartlink-setup"));

            SuppressedAdvisory only = Assert.Single(store.Snapshot());
            Assert.Equal(first, only.Silenced);
        }

        [Fact]
        public void EachEntryRecordsWhenItWasSilenced()
        {
            // "Reset all warnings" tells an operator nothing about what he
            // silenced or when. The date is half of what makes the named list
            // worth having.
            DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);
            AdvisorySuppressionStore store = NewStore();
            store.Suppress(Key("smartlink-setup"));

            DateTimeOffset? when = store.Snapshot().Single().Silenced;

            Assert.NotNull(when);
            Assert.InRange(when!.Value, before, DateTimeOffset.UtcNow.AddSeconds(1));
        }

        /// <summary>
        /// Every <see cref="AdvisoryKey"/> the catalogue can produce, found by
        /// reflection: the static properties that return one, and the factory
        /// methods that build one from strings.
        /// </summary>
        private static List<(string Source, AdvisoryKey Key)> DeclaredKeys()
        {
            var found = new List<(string, AdvisoryKey)>();
            Type t = typeof(AdvisoryKeys);

            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                if (p.PropertyType != typeof(AdvisoryKey)) continue;
                if (p.GetValue(null) is AdvisoryKey key) found.Add((p.Name, key));
            }

            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.IsSpecialName) continue;
                if (m.ReturnType != typeof(AdvisoryKey)) continue;

                ParameterInfo[] ps = m.GetParameters();
                if (!ps.All(p => p.ParameterType == typeof(string))) continue;

                // Sample values with no vertical bar in them: the bar is the
                // catalogue's own field separator, and a sample carrying one
                // would be testing a malformed key rather than a real one.
                object[] args = ps.Select(p => (object)("sample-" + p.Name)).ToArray();
                if (m.Invoke(null, args) is AdvisoryKey key) found.Add((m.Name, key));
            }

            return found;
        }
    }
}
