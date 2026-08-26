using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Radios;

namespace Radios.Tests
{
    /// <summary>
    /// The floor under every test in this assembly: a throwaway settings root,
    /// bound before the first test runs.
    ///
    /// <para><b>Why this exists (task #232).</b> The settings statics
    /// (<see cref="RadioConfig.BaseDirectory"/>,
    /// <see cref="KnownRadioRoster.CacheDirectory"/>,
    /// <see cref="Lexicon.OverlayDirectoryOverride"/>) all SELF-HEAL when they
    /// are empty — they derive the operator's real
    /// <c>%AppData%\JJFlexRadio</c> and carry on. That is right for the app and
    /// a landmine in a test process: a test that never sets them does not fail,
    /// it quietly reads (and could write) the machine's live configuration. Its
    /// result then depends on what is sitting in that folder today, which is
    /// the opposite of a test, and the dependency is invisible because nothing
    /// anywhere says which directory was used.</para>
    ///
    /// <para>So the assembly binds all three to an empty per-process temporary
    /// tree at module load, before xUnit has constructed anything. A class that
    /// isolates itself properly (see <see cref="RadioConfigStaticsScope"/>)
    /// overrides this for its own lifetime and restores it afterwards; a class
    /// that forgets gets an empty throwaway tree instead of Noel's real one.
    /// The floor is not a substitute for per-class isolation — it is what stops
    /// the absence of isolation from reaching outside the test process.</para>
    ///
    /// <para>A module initializer rather than a fixture on purpose: xUnit 2.x
    /// has no assembly-level fixture, and several types under test hold their
    /// folder in a <c>static readonly</c> field evaluated at type load, which
    /// can happen before any fixture would have run.</para>
    /// </summary>
    internal static class TestSettingsRoot
    {
        /// <summary>The throwaway tree bound for the life of the test process.</summary>
        internal static string Directory { get; private set; } = "";

        [ModuleInitializer]
        internal static void Bind()
        {
            try
            {
                Directory = Path.Combine(
                    Path.GetTempPath(),
                    "jjflex-testsuite-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(Directory);

                RadioConfig.BaseDirectory = Directory;
                KnownRadioRoster.CacheDirectory = Directory;
                Lexicon.OverlayDirectoryOverride = Directory;

                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    try { System.IO.Directory.Delete(Directory, recursive: true); }
                    catch (IOException) { /* a temp dir; the OS sweeps it */ }
                    catch (UnauthorizedAccessException) { }
                };
            }
            catch (Exception ex)
            {
                // Never take the whole assembly down over this. A failure here
                // means the tests fall back to the old behaviour, which is what
                // they had before — but say so, loudly, because it means the
                // live settings folder is back in scope.
                Console.Error.WriteLine(
                    "TestSettingsRoot: could not bind a throwaway settings root (" +
                    ex.Message + "). Tests may read the machine's live " +
                    "%AppData%\\JJFlexRadio.");
            }
        }
    }

    /// <summary>
    /// One test class's private copy of the process-wide settings statics, taken
    /// and given back as a unit — and loud when somebody else takes them at the
    /// same time.
    ///
    /// <para><b>Task #232.</b>
    /// <c>ConnectPathLearningConfigTests.TurnedOffMeansNoTrendIsEverReturned&#8203;HoweverLoudTheHistory</c>
    /// failed once in a full-suite run and passed five times either side of it,
    /// same commit, same machine. A test that fails one run in five teaches
    /// everyone to re-run rather than investigate, and the next real regression
    /// in that area gets re-run away and dismissed. The answer is isolation,
    /// never a retry: a retry hides the coupling and keeps the lie.</para>
    ///
    /// <para><b>What each class was doing before, and why it was not enough.</b>
    /// Six classes each hand-rolled the same save-set-restore dance in their
    /// constructor and Dispose. Every copy was correct in isolation and they
    /// differed in what they covered — some took two statics, some one, and
    /// only one of them invalidated
    /// <see cref="ConnectPathLearningConfig"/>'s cache, which is derived from
    /// the directory and therefore part of the same state. Six copies of a rule
    /// is six chances to omit a line, and the omission is invisible: nothing
    /// fails at the point of the mistake.</para>
    ///
    /// <para><b>The guard is the part that earns its keep.</b> These statics are
    /// serialised only by every toucher carrying
    /// <c>[Collection(RadioConfigStaticsCollection.Name)]</c> — a convention
    /// nothing enforced. This type makes a violation say so at the moment it
    /// happens, naming both scopes, instead of surfacing later as an unrelated
    /// assertion in whichever class lost the race.
    /// <c>RadioConfigStaticsIsolationTests</c> is the other half: it refuses to
    /// let a class touch this state without the attribute.</para>
    /// </summary>
    public sealed class RadioConfigStaticsScope : IDisposable
    {
        private static int _live;
        private static string _liveOwner = "";

        private readonly string _dir;
        private readonly string _owner;
        private readonly string? _savedBase;
        private readonly string? _savedCache;
        private readonly string? _savedOverlay;
        private bool _disposed;

        /// <summary>
        /// Take the statics. <paramref name="label"/> names the taker and shows
        /// up in the collision message, so pass something a person can act on —
        /// the test class name.
        /// </summary>
        public RadioConfigStaticsScope(string label)
        {
            _owner = string.IsNullOrWhiteSpace(label) ? "an unnamed scope" : label;

            string? previous = Interlocked.CompareExchange(ref _live, 1, 0) == 0
                ? null
                : _liveOwner;
            if (previous != null)
            {
                throw new InvalidOperationException(
                    "Two test classes hold the settings statics at once: '" + previous +
                    "' still has them and '" + _owner + "' is asking for them. " +
                    "Both must carry [Collection(RadioConfigStaticsCollection.Name)] — " +
                    "xUnit runs test classes in parallel, and without the collection " +
                    "they trample each other. See task #232.");
            }
            _liveOwner = _owner;

            _dir = Path.Combine(Path.GetTempPath(), "jjflex-" + label + "-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(_dir);

            _savedBase = RadioConfig.BaseDirectory;
            _savedCache = KnownRadioRoster.CacheDirectory;
            _savedOverlay = Lexicon.OverlayDirectoryOverride;

            RadioConfig.BaseDirectory = _dir;
            KnownRadioRoster.CacheDirectory = _dir;
            Lexicon.OverlayDirectoryOverride = _dir;

            ForgetEverythingDerivedFromTheDirectory();
        }

        /// <summary>This scope's private settings tree. Empty at construction.</summary>
        public string Directory => _dir;

        /// <summary>A path inside this scope's tree.</summary>
        public string PathTo(params string[] parts) =>
            Path.Combine(new[] { _dir }.Concat(parts).ToArray());

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // What the statics say NOW, before anything is restored. If this is
            // not what the scope set, somebody else wrote them while this class
            // was running and every assertion in it was reading a directory it
            // did not own.
            string? baseAtExit = RadioConfig.BaseDirectory;

            RadioConfig.BaseDirectory = _savedBase;
            KnownRadioRoster.CacheDirectory = _savedCache;
            Lexicon.OverlayDirectoryOverride = _savedOverlay;
            ForgetEverythingDerivedFromTheDirectory();

            _liveOwner = "";
            Interlocked.Exchange(ref _live, 0);

            try { System.IO.Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* a temp dir; the OS sweeps it */ }
            catch (UnauthorizedAccessException) { }

            if (!string.Equals(baseAtExit, _dir, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "'" + _owner + "' set RadioConfig.BaseDirectory to '" + _dir +
                    "' and found '" + (baseAtExit ?? "(null)") + "' at the end of the test. " +
                    "Something else wrote the settings statics mid-test, so this class's " +
                    "assertions were reading a store it did not own. See task #232.");
            }
        }

        /// <summary>
        /// Caches keyed on the settings directory. They are part of the state
        /// this scope owns: leaving one populated hands the next test a value
        /// loaded from a directory that no longer exists.
        /// </summary>
        private static void ForgetEverythingDerivedFromTheDirectory()
        {
            ConnectPathLearningConfig.Invalidate();
            Lexicon.Forget();
        }
    }
}
