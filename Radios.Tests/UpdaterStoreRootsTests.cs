using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using JJFlexUpdater;
using JJFlexUpdater.Hashing;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The updater kept its preferences in <c>%APPDATA%\JJFlexRadio</c> and its
    /// install-hash cache in <c>%LOCALAPPDATA%\JJFlexRadio</c>, built from the
    /// system folders, so a run isolated by <c>JJFLEX_CONFIG_DIR</c> whose
    /// manifest fetch succeeded would write the operator's live settings
    /// folder. Both now follow the throwaway tree on a run whose override the
    /// settings resolver accepted, and stay exactly where they were otherwise.
    /// No network, no window, and nothing is written: every path here is
    /// computed, never saved to.
    /// <para>
    /// <b>Process-wide state never leaks out of this class.</b> The two folders
    /// are statics, so every test that applies them does it in a fresh copy of
    /// JJFlexUpdater.dll in its own collectible <see cref="AssemblyLoadContext"/>
    /// (the pattern <see cref="UpdateManifestTestLeverTests"/> uses), which is
    /// unloaded afterwards. This process's own updater folders are never moved,
    /// and the test says so.
    /// </para>
    /// </summary>
    public sealed class UpdaterStoreRootsTests
    {
        private static string Scratch(string name) =>
            Path.Combine(Path.GetTempPath(), "jjflex-stores-" + Guid.NewGuid().ToString("N"), name);

        /// <summary>The ordinary paths, built exactly as the updater always
        /// built them. Computed only; nothing is written there.</summary>
        private static string OrdinarySettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JJFlexRadio", UpdaterSettings.DefaultFileName);

        private static string OrdinaryCachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JJFlexRadio", HashInventoryCache.DefaultFileName);

        [Fact]
        public void An_accepted_relocated_run_keeps_update_settings_under_the_relocated_root()
        {
            // Through the real resolver, with a synthetic standard folder.
            string standard = Scratch("JJFlexRadio");
            string temp = Scratch("isolated");
            string root = RadioConfig.ResolveStartupDirectory(standard, temp, out bool taken, out _);
            Assert.True(taken);
            bool gate = LocalDataRoot.RelocatedByConfigDir(temp, standard, root);
            Assert.True(gate);
            string local = LocalDataRoot.Resolve(root, Path.GetDirectoryName(standard), Scratch("local"));
            Assert.Equal(Path.Combine(root, LocalDataRoot.RelocatedFolderName), local);

            Stores s = ApplyInIsolatedUpdater(root, local, gate);

            Assert.Equal(Path.Combine(root, UpdaterSettings.DefaultFileName), s.SettingsPath);
            Assert.Equal(Path.Combine(local, HashInventoryCache.DefaultFileName), s.CachePath);
            Assert.NotEqual(OrdinarySettingsPath, s.SettingsPath);
            Assert.NotEqual(OrdinaryCachePath, s.CachePath);
            Assert.NotNull(s.Line);
            Assert.Contains(s.SettingsPath, s.Line);
            Assert.Contains("isolated by JJFLEX_CONFIG_DIR", s.Line);
            AssertThisProcessNeverMoved();
        }

        [Fact]
        public void An_ordinary_run_keeps_the_roaming_path_and_says_nothing()
        {
            string root = Scratch("would-be-root");
            Stores s = ApplyInIsolatedUpdater(root, Path.Combine(root, "LocalData"), runRelocatedByConfigDir: false);

            Assert.Equal(OrdinarySettingsPath, s.SettingsPath);
            Assert.Equal(OrdinaryCachePath, s.CachePath);
            Assert.Null(s.Line);
            AssertThisProcessNeverMoved();
        }

        [Fact]
        public void A_refused_override_naming_the_normal_folder_keeps_the_roaming_path()
        {
            // The resolver refuses an override naming the ordinary folder and
            // falls back to that same folder, so the gate stays shut.
            string standard = Scratch("JJFlexRadio");
            string root = RadioConfig.ResolveStartupDirectory(standard, standard, out bool taken, out string? refusal);
            Assert.False(taken);
            Assert.NotNull(refusal);
            bool gate = LocalDataRoot.RelocatedByConfigDir(standard, standard, root);
            Assert.False(gate);

            Stores s = ApplyInIsolatedUpdater(root, LocalDataRoot.Resolve(root, Path.GetDirectoryName(standard), Scratch("local")), gate);

            Assert.Equal(OrdinarySettingsPath, s.SettingsPath);
            Assert.Equal(OrdinaryCachePath, s.CachePath);
            Assert.Null(s.Line);
            AssertThisProcessNeverMoved();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("relative\\tree")]
        public void An_isolated_run_without_a_full_root_moves_nothing_and_says_so(string? root)
        {
            Assert.False(UpdaterStoreRoots.DecideRelocatedRoots(root, Scratch("local"), true,
                out string? settings, out string? local, out string? line));
            Assert.Null(settings);
            Assert.Null(local);
            Assert.NotNull(line);
            Assert.Contains("not a full path", line);
            // Positive control: a full path is taken.
            Assert.True(UpdaterStoreRoots.DecideRelocatedRoots(Scratch("root"), Scratch("local"), true,
                out _, out _, out _));
        }

        private static void AssertThisProcessNeverMoved()
        {
            Assert.Null(UpdaterStoreRoots.SettingsFolder);
            Assert.Null(UpdaterStoreRoots.LocalFolder);
            Assert.Equal(OrdinarySettingsPath, UpdaterSettings.DefaultPath());
            Assert.Equal(OrdinaryCachePath, HashInventoryCache.DefaultCachePath());
        }

        // ── The wiring ──

        [Fact]
        public void Load_and_save_default_to_the_path_that_follows_the_tree()
        {
            string settings = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexUpdater", "UpdaterSettings.cs"));
            Assert.Equal(2, Regex.Matches(settings, Regex.Escape("path ??= DefaultPath();")).Count);
            string cache = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexUpdater", "Hashing", "HashInventoryCache.cs"));
            Assert.Contains("public HashInventoryCache() : this(DefaultCachePath()) { }", cache);
        }

        [Fact]
        public void Start_up_moves_the_stores_behind_the_accepted_override_gate_before_the_lever()
        {
            string events = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "ApplicationEvents.vb"));
            int gate = events.IndexOf("If Radios.LocalDataRoot.RunRelocatedByConfigDir Then", StringComparison.Ordinal);
            Assert.True(gate > 0, "the start-up gate is gone");
            int endIf = events.IndexOf("End If", gate, StringComparison.Ordinal);
            int stores = events.IndexOf("UpdaterStoreRoots.ApplyRelocatedRoots(", StringComparison.Ordinal);
            int lever = events.IndexOf("UpdaterEndpoints.ApplyTestAppManifestUrl(", StringComparison.Ordinal);
            Assert.True(stores > gate && stores < endIf, "the stores are moved outside the gate");
            Assert.True(stores < lever, "the manifest lever is applied before the stores have moved");
            Assert.Single(Regex.Matches(events, Regex.Escape("UpdaterStoreRoots.ApplyRelocatedRoots(")));
            // From the settings root and the local root every other store uses.
            string call = events.Substring(stores, Math.Min(200, events.Length - stores));
            Assert.Contains("Radios.RadioConfig.AppDataRoot", call);
            Assert.Contains("Radios.LocalDataRoot.Folder", call);
            // Its line is traced after the diagnostic log opens.
            int init = events.IndexOf("            InitializeApplication()", StringComparison.Ordinal);
            int traced = events.IndexOf("JJTrace.Tracing.TraceLine(_updaterStoresTraceLine", StringComparison.Ordinal);
            Assert.True(init > stores && traced > init, "the stores' line is not traced after the log opens");
        }

        // ── Isolation ──

        private readonly record struct Stores(string? Line, string SettingsPath, string CachePath);

        /// <summary>
        /// Apply the store roots in a fresh copy of JJFlexUpdater.dll and read
        /// back that copy's two default paths. The copy's statics are its own.
        /// </summary>
        private static Stores ApplyInIsolatedUpdater(string? settingsRoot, string? localRoot, bool runRelocatedByConfigDir)
        {
            Stores result = default;
            WeakReference unloaded = InIsolatedContext(typeof(UpdaterStoreRoots).Assembly.Location, assembly =>
            {
                Type roots = assembly.GetType(typeof(UpdaterStoreRoots).FullName!, throwOnError: true)!;
                Assert.NotSame(typeof(UpdaterStoreRoots), roots);
                Type settings = assembly.GetType(typeof(UpdaterSettings).FullName!, throwOnError: true)!;
                Type cache = assembly.GetType(typeof(HashInventoryCache).FullName!, throwOnError: true)!;
                MethodInfo settingsPath = settings.GetMethod(nameof(UpdaterSettings.DefaultPath))!;
                MethodInfo cachePath = cache.GetMethod(nameof(HashInventoryCache.DefaultCachePath))!;
                // The copy starts ordinary.
                Assert.Equal(OrdinarySettingsPath, (string)settingsPath.Invoke(null, null)!);
                string? line = (string?)roots.GetMethod(nameof(UpdaterStoreRoots.ApplyRelocatedRoots))!
                    .Invoke(null, new object?[] { settingsRoot, localRoot, runRelocatedByConfigDir });
                result = new Stores(line, (string)settingsPath.Invoke(null, null)!, (string)cachePath.Invoke(null, null)!);
            });
            for (int i = 0; unloaded.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference InIsolatedContext(string assemblyPath, Action<Assembly> body)
        {
            var context = new AssemblyLoadContext("updater-stores-" + Guid.NewGuid().ToString("N"), isCollectible: true);
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
    }
}
