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
        public static bool RunRelocatedByConfigDir => RelocatedByConfigDir(
            Environment.GetEnvironmentVariable(RadioConfig.ConfigDirOverrideVariable),
            RadioConfig.AppDataRoot);

        /// <summary>
        /// The rule behind <see cref="RunRelocatedByConfigDir"/>, without the
        /// environment: an override was offered, and the settings root this
        /// run resolved IS that override (RadioConfig resolves to the offered
        /// path only when it accepts it, and refuses one equal to the
        /// ordinary folder).
        /// </summary>
        public static bool RelocatedByConfigDir(string? offered, string? appDataRoot)
        {
            if (string.IsNullOrWhiteSpace(offered) || string.IsNullOrWhiteSpace(appDataRoot)) return false;
            string candidate = offered.Trim();
            if (!Path.IsPathRooted(candidate)) return false;
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
            return string.Equals(
                Path.TrimEndingDirectorySeparator(full),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDataRoot)),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeFolder(Environment.SpecialFolder folder)
        {
            try { return Environment.GetFolderPath(folder); }
            catch (PlatformNotSupportedException) { return ""; }
        }
    }
}
