using System;
using System.IO;

namespace Radios
{
    /// <summary>
    /// The machine-local counterpart of <see cref="RadioConfig.AppDataRoot"/>:
    /// <c>%LocalAppData%\JJFlexRadio</c> normally, and a folder inside the
    /// throwaway settings tree when <c>JJFLEX_CONFIG_DIR</c> has moved it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For caches, not settings.</b> The About dialog, the HTML document
    /// viewer and the Fixer keep their shared WebView2 browser data here. That
    /// data is a regenerable browser cache and belongs to this machine, so it
    /// stays out of the roaming settings root, which roams with a Windows
    /// profile and is what the AppData backup walks. It always lived in
    /// <c>%LocalAppData%\JJFlexRadio\WebView2</c>, so on an ordinary run nothing
    /// moves.
    /// </para>
    /// <para>
    /// <b>Why this exists (#909).</b> Those three dialogs built the path from
    /// <c>SpecialFolder.LocalApplicationData</c> themselves, so a
    /// <c>JJFLEX_CONFIG_DIR</c> run that reported itself isolated still wrote the
    /// operator's real browser cache: the failure CLAUDE.md's rule about
    /// <see cref="RadioConfig.AppDataRoot"/> describes, one folder over. This
    /// root follows the settings root wherever it goes.
    /// </para>
    /// <para>
    /// <b>Never the sign-in folder.</b> SmartLink sign-in keeps its own WebView2
    /// data under <c>AppDataRoot\WebView2</c>. Sharing it would share SmartLink
    /// cookies with document views and contend for the folder's lock, which the
    /// sign-in path already retries around.
    /// </para>
    /// </remarks>
    public static class LocalDataRoot
    {
        /// <summary>The settings folder's standard name, the same name
        /// RadioConfig gives it: the ordinary root is this folder directly
        /// inside roaming app data, and it is also the name used when the
        /// settings root cannot be resolved at all.</summary>
        private const string FallbackFolderName = "JJFlexRadio";

        /// <summary>The folder inside a relocated settings tree that stands in
        /// for local app data.</summary>
        public const string RelocatedFolderName = "LocalData";

        /// <summary>This run's machine-local root.</summary>
        public static string Folder => Resolve(
            RadioConfig.AppDataRoot,
            SafeFolder(Environment.SpecialFolder.ApplicationData),
            SafeFolder(Environment.SpecialFolder.LocalApplicationData));

        /// <summary>
        /// The WebView2 user-data folder the document views share (About, the
        /// HTML document viewer, the Fixer): one folder, so one browser process
        /// for all of them.
        /// </summary>
        public static string DocumentViewsWebView2 => Path.Combine(Folder, "WebView2");

        /// <summary>
        /// The rule, without the environment. When the settings root is the
        /// ordinary one (<c>%AppData%\JJFlexRadio</c> itself), the local root is
        /// the folder of the same name inside local app data. When it has been
        /// moved, the local root moves with it, inside it.
        /// </summary>
        /// <remarks>
        /// Ordinary means the standard folder exactly, not any folder whose
        /// parent is roaming app data (#937): <c>JJFLEX_CONFIG_DIR</c> set to
        /// <c>%AppData%\Something</c> is a throwaway tree, and the old parent
        /// test sent its local data to <c>%LocalAppData%\Something</c>,
        /// outside it. RadioConfig refuses an override equal to the standard
        /// folder, so "is the standard folder" is "was not relocated".
        /// </remarks>
        public static string Resolve(string? appDataRoot, string? roamingAppData, string? localAppData)
        {
            if (string.IsNullOrWhiteSpace(appDataRoot))
                return Path.Combine(localAppData ?? "", FallbackFolderName);

            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDataRoot));
            bool ordinary = !IsRelocated(appDataRoot, roamingAppData);

            if (ordinary && !string.IsNullOrWhiteSpace(localAppData))
                return Path.Combine(localAppData!, Path.GetFileName(root));

            return Path.Combine(root, RelocatedFolderName);
        }

        /// <summary>
        /// Whether this run's settings root has been moved by
        /// <c>JJFLEX_CONFIG_DIR</c>: the one rule every store that must follow
        /// the throwaway tree asks (#937, #1066).
        /// </summary>
        public static bool RunIsRelocated => IsRelocated(
            RadioConfig.AppDataRoot,
            SafeFolder(Environment.SpecialFolder.ApplicationData));

        /// <summary>
        /// The rule behind <see cref="RunIsRelocated"/>, without the
        /// environment. Ordinary means the standard folder exactly
        /// (<c>%AppData%\JJFlexRadio</c>, in any letter case, with or without
        /// a trailing separator), not any folder whose parent is roaming app
        /// data (#937). A root that cannot be resolved at all is the ordinary
        /// one, as <see cref="Resolve"/> treats it; an unknown roaming folder
        /// means nothing can be shown to be ordinary, so it is relocated.
        /// </summary>
        public static bool IsRelocated(string? appDataRoot, string? roamingAppData)
        {
            if (string.IsNullOrWhiteSpace(appDataRoot)) return false;
            if (string.IsNullOrWhiteSpace(roamingAppData)) return true;

            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDataRoot));
            string standard = Path.Combine(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(roamingAppData)),
                FallbackFolderName);
            return !string.Equals(root, standard, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether <c>JJFLEX_CONFIG_DIR</c> was set AND accepted for this run:
        /// narrower than <see cref="RunIsRelocated"/>, which is also true when
        /// the roaming folder cannot be resolved at all. For anything that
        /// must engage only on a deliberately isolated test run.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It asks the resolver, never compares paths.</b> The first version
        /// said "accepted" when the offered path equalled the settings root.
        /// But RadioConfig REFUSES an override naming the ordinary settings
        /// folder and falls back to that same folder, so the two were equal on
        /// exactly the run that was not isolated: the gate opened on live
        /// settings and the trace said the run was isolated. Equal paths cannot
        /// prove acceptance; only the resolver's own verdict can.
        /// </para>
        /// <para>
        /// <b>Why it re-asks rather than recording.</b> The cleaner home is a
        /// flag RadioConfig records beside its cached root at the moment it
        /// decides. That file is being changed by Sprint 49, so this does not
        /// touch it: it runs the same resolver
        /// (<see cref="RadioConfig.ResolveStartupDirectory"/>) on the same
        /// standard folder and the same variable, and opens only when the
        /// resolver says the override was taken AND what it resolved to is the
        /// root this run actually cached. The equality half covers a variable
        /// changed after the cache filled. Once Sprint 49 has merged, recording
        /// the verdict where it is made is the better design.
        /// </para>
        /// </remarks>
        public static bool RunRelocatedByConfigDir
        {
            get
            {
                // The cached root first, so the cache is filled before the
                // comparison, as every store that follows the tree sees it.
                string appDataRoot = RadioConfig.AppDataRoot;
                string? standard = StandardSettingsFolder();
                if (standard == null) return false;
                return RelocatedByConfigDir(
                    Environment.GetEnvironmentVariable(RadioConfig.ConfigDirOverrideVariable),
                    standard,
                    appDataRoot);
            }
        }

        /// <summary>
        /// The rule behind <see cref="RunRelocatedByConfigDir"/>, without the
        /// environment: the real resolver, given <paramref name="standardFolder"/>
        /// and <paramref name="offered"/>, says it took the override, and the
        /// folder it took is <paramref name="appDataRoot"/>.
        /// </summary>
        public static bool RelocatedByConfigDir(string? offered, string standardFolder, string? appDataRoot)
        {
            string resolved = RadioConfig.ResolveStartupDirectory(
                standardFolder, offered, out bool isTemporary, out _);
            if (!isTemporary || string.IsNullOrWhiteSpace(appDataRoot)) return false;
            return string.Equals(
                Path.TrimEndingDirectorySeparator(resolved),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDataRoot)),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The ordinary settings folder, built exactly as RadioConfig builds
        /// the one it hands the resolver; null where RadioConfig would have
        /// failed to resolve a root at all.
        /// </summary>
        private static string? StandardSettingsFolder()
        {
            try
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    FallbackFolderName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string SafeFolder(Environment.SpecialFolder folder)
        {
            try { return Environment.GetFolderPath(folder); }
            catch (PlatformNotSupportedException) { return ""; }
        }
    }
}
