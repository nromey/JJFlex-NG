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
        /// <summary>The folder name used when the settings root cannot be
        /// resolved at all; the same name RadioConfig gives the settings
        /// folder.</summary>
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
        /// ordinary one (directly inside roaming app data), the local root is
        /// the folder of the same name inside local app data. When it has been
        /// moved, the local root moves with it, inside it.
        /// </summary>
        public static string Resolve(string? appDataRoot, string? roamingAppData, string? localAppData)
        {
            if (string.IsNullOrWhiteSpace(appDataRoot))
                return Path.Combine(localAppData ?? "", FallbackFolderName);

            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDataRoot));
            string? parent = Path.GetDirectoryName(root);
            bool ordinary = !string.IsNullOrWhiteSpace(roamingAppData)
                && parent != null
                && string.Equals(parent,
                                 Path.TrimEndingDirectorySeparator(Path.GetFullPath(roamingAppData!)),
                                 StringComparison.OrdinalIgnoreCase);

            if (ordinary && !string.IsNullOrWhiteSpace(localAppData))
                return Path.Combine(localAppData!, Path.GetFileName(root));

            return Path.Combine(root, RelocatedFolderName);
        }

        private static string SafeFolder(Environment.SpecialFolder folder)
        {
            try { return Environment.GetFolderPath(folder); }
            catch (PlatformNotSupportedException) { return ""; }
        }
    }
}
