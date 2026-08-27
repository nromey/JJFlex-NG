using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Radios
{
    /// <summary>
    /// One reportable fact about the running installation: a section it belongs
    /// under, a label, and a value. The About page renders these; the crash
    /// reporter and the debug bundle embed them. Nothing else should assemble
    /// its own version strings — two assemblers is how the report and the page
    /// end up disagreeing about what is running.
    /// </summary>
    public sealed class DiagnosticItem
    {
        public DiagnosticItem(string section, string label, string value)
        {
            Section = section;
            Label = label;
            Value = value;
        }

        public string Section { get; }
        public string Label { get; }
        public string Value { get; }
    }

    /// <summary>
    /// A point-in-time picture of what is actually running: the app's own build
    /// identity, every native and managed component's self-reported version,
    /// the environment, and the support facts people ask for first (trace file
    /// location, executable path, self-contained or not).
    ///
    /// EVERY value is queried at runtime from the thing itself — the loaded
    /// DLL, the running process, the live tracing state. Nothing is hardcoded.
    /// A baked-in version is worse than none, because it lies with confidence:
    /// this project's own docs claimed Opus 1.5.2 while 1.6.1 was shipping.
    ///
    /// PortAudio gets special handling. Its version text reads
    /// "PortAudio V19.7.0-devel" whether the DLL was built in 2021 or last
    /// week — upstream never bumps the number — so only the stamped revision
    /// suffix identifies a build. The display value leads with the revision
    /// and never presents the 19.7.0 text as if it meant something.
    ///
    /// Capture() never throws. Each probe is individually guarded and a failed
    /// probe reports itself honestly ("not available ...") instead of guessing.
    /// </summary>
    public sealed class DiagnosticSnapshot
    {
        // Section names, public so renderers group by identity rather than by
        // re-typed strings.
        public const string SectionApplication = "Application";
        public const string SectionComponents = "Components";
        public const string SectionEnvironment = "Environment";
        public const string SectionSupport = "Support";

        /// <summary>Local time the snapshot was taken.</summary>
        public DateTime CapturedAt { get; private set; }

        // --- Application identity ---

        /// <summary>Entry assembly simple name, e.g. "JJFlexRadio".</summary>
        public string AppName { get; private set; }

        /// <summary>Entry assembly version (System.Version string).</summary>
        public string AppAssemblyVersion { get; private set; }

        /// <summary>
        /// AssemblyInformationalVersion, e.g. "4.1.16+&lt;git sha&gt;". On a plain
        /// dotnet build the SHA is the only precise build identifier.
        /// </summary>
        public string AppInformationalVersion { get; private set; }

        /// <summary>
        /// FileVersion of the running executable. Carries the real 4-part build
        /// number (e.g. 4.1.16.697) on builds made through the build scripts —
        /// the key the NAS historical tree and tester zips are named by.
        /// </summary>
        public string AppFileVersion { get; private set; }

        /// <summary>
        /// The best single version string to show a user: the 4-part
        /// FileVersion when present, otherwise the assembly version.
        /// </summary>
        public string AppDisplayVersion
        {
            get
            {
                if (!string.IsNullOrEmpty(AppFileVersion)) return AppFileVersion;
                if (!string.IsNullOrEmpty(AppAssemblyVersion)) return AppAssemblyVersion;
                return "unknown";
            }
        }

        /// <summary>
        /// Local date the running build was produced, e.g. "2026-08-27", or
        /// null when it cannot be established. See <see cref="BuildStamp"/> for
        /// where it comes from and why the file's own timestamp is the last
        /// resort rather than the first.
        /// </summary>
        public string BuildDate { get; private set; }

        /// <summary>
        /// "Debug" or "Release" for the running build, or null when it cannot
        /// be established. A tester's report that names the version without the
        /// configuration is ambiguous — the nightly and the installer share a
        /// version number and behave differently.
        /// </summary>
        public string BuildConfiguration { get; private set; }

        /// <summary>
        /// The git commit the build was made from, short form, or null when the
        /// informational version carries no SHA (a build made outside a
        /// checkout).
        /// </summary>
        public string BuildCommit { get; private set; }

        /// <summary>Full path of the running executable.</summary>
        public string ExecutablePath { get; private set; }

        /// <summary>
        /// True when the .NET runtime is loaded from the install folder
        /// (self-contained deployment), false when a shared runtime is in use,
        /// null when it could not be determined.
        /// </summary>
        public bool? SelfContained { get; private set; }

        // --- Components ---

        /// <summary>RuntimeInformation.FrameworkDescription, e.g. ".NET 10.0.0".</summary>
        public string DotNetRuntime { get; private set; }

        /// <summary>FlexLib's file/product version, from the loaded assembly.</summary>
        public string FlexLibVersion { get; private set; }

        /// <summary>opus_get_version_string() from the loaded libopus.dll, e.g. "libopus 1.6.1".</summary>
        public string OpusVersion { get; private set; }

        /// <summary>
        /// prism_version_string() from the loaded prism.dll, e.g. "0.18.1".
        /// Null when prism.dll did not load, or when render is off — the #171
        /// silent channel promises Prism is never loaded then, and a version
        /// probe would load it.
        ///
        /// Same caveat as PortAudio: the string is stamped at CMake configure
        /// time from the project version, so a build made past the tag still
        /// reports the tag — the DLL we shipped until 2026-08-21 said "0.17.3"
        /// while being 46 commits newer. Honest only because the build policy
        /// is to build exactly at the pinned tag; the SHA pinned in CLAUDE.md
        /// is the real identifier.
        /// </summary>
        public string PrismVersion { get; private set; }

        /// <summary>Raw Pa_GetVersionText() from the loaded portaudio.dll.</summary>
        public string PortAudioVersionText { get; private set; }

        /// <summary>
        /// The revision stamped into portaudio.dll ("a880212"), "unknown" when
        /// the DLL was built without a stamp, or null when the DLL did not load.
        /// </summary>
        public string PortAudioRevision { get; private set; }

        /// <summary>WebView2 runtime browser version, or null when not installed.</summary>
        public string WebView2Runtime { get; private set; }

        // --- Environment ---

        /// <summary>Environment.OSVersion.VersionString.</summary>
        public string OsVersion { get; private set; }

        /// <summary>Process architecture, e.g. "X64".</summary>
        public string ProcessArchitecture { get; private set; }

        /// <summary>OS architecture, e.g. "X64".</summary>
        public string OsArchitecture { get; private set; }

        // --- Support facts ---

        /// <summary>True when tracing is currently writing to a file.</summary>
        public bool TracingActive { get; private set; }

        /// <summary>The live trace file path, or null when tracing is off.</summary>
        public string TraceFilePath { get; private set; }

        /// <summary>The folder trace files land in.</summary>
        public string TraceFolder { get; private set; }

        /// <summary>The folder archived trace sessions land in.</summary>
        public string TraceArchiveFolder { get; private set; }

        /// <summary>
        /// The speech library in use and what it is talking to, e.g.
        /// "Prism, using NVDA". Reads "none" when nothing came up, which is the
        /// one state a blind operator most needs stated rather than inferred.
        /// </summary>
        public string SpeechEngine { get; private set; }

        /// <summary>Detected screen reader description.</summary>
        public string ScreenReader { get; private set; }

        /// <summary>True when a braille display is available.</summary>
        public bool BrailleAvailable { get; private set; }

        /// <summary>
        /// The running executable's 4-part FileVersion, e.g. "4.1.16.1024", or
        /// null when it cannot be read.
        ///
        /// A deliberately cheap probe for callers that want the version alone
        /// and must not pay for a full Capture(). It lives HERE rather than at
        /// the call site because this class is the single assembler of version
        /// strings - two assemblers is how the About page and the spoken
        /// greeting end up disagreeing about what is running.
        /// </summary>
        public static string QuickFileVersion
        {
            get
            {
                try
                {
                    string path = System.Diagnostics.Process.GetCurrentProcess()
                        .MainModule?.FileName;
                    if (string.IsNullOrEmpty(path)) return null;
                    var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                    return string.IsNullOrEmpty(fvi.FileVersion) ? null : fvi.FileVersion;
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// The four facts that identify a build to a tester: the 4-part
        /// version, the date it was produced, Debug or Release, and the commit.
        /// Any field may be null when it could not be established honestly.
        /// </summary>
        public sealed class BuildIdentity
        {
            internal BuildIdentity(string version, DateTime? built, string configuration, string commit)
            {
                Version = version;
                Built = built;
                Configuration = configuration;
                Commit = commit;
            }

            /// <summary>4-part FileVersion, e.g. "4.1.16.1024".</summary>
            public string Version { get; }

            /// <summary>Local build time, or null when it could not be established.</summary>
            public DateTime? Built { get; }

            /// <summary>Local build date, "yyyy-MM-dd" — the written form.</summary>
            public string Date
            {
                get
                {
                    return Built.HasValue
                        ? Built.Value.ToString("yyyy-MM-dd",
                            System.Globalization.CultureInfo.InvariantCulture)
                        : null;
                }
            }

            /// <summary>
            /// The same date said aloud: "August 27, 2026". A hyphenated ISO
            /// date is for a page you read; a tester saying the date down a
            /// phone wants the month by name. Both come off one DateTime here,
            /// in the single assembler, so they cannot drift.
            /// </summary>
            public string DateSpoken
            {
                get
                {
                    return Built.HasValue
                        ? Built.Value.ToString("MMMM d, yyyy",
                            System.Globalization.CultureInfo.InvariantCulture)
                        : null;
                }
            }

            /// <summary>"Debug" or "Release".</summary>
            public string Configuration { get; }

            /// <summary>Short git SHA, or null outside a checkout.</summary>
            public string Commit { get; }
        }

        private static BuildIdentity _buildStamp;

        /// <summary>
        /// Cheap, cached build identity for callers that must not pay for a
        /// full <see cref="Capture"/> — the leader-key chord that speaks the
        /// build (#269) runs on a keypress and probes no native DLLs.
        ///
        /// It lives HERE, beside <see cref="QuickFileVersion"/>, because this
        /// class is the single assembler of version strings. #269's own note
        /// says it: read from the thing the About page already uses, do not
        /// build a second version-reporting path.
        ///
        /// THE DATE DOES NOT COME FROM THE FILE'S TIMESTAMP FIRST, and that is
        /// the entire point (#268). Dropbox rewrites timestamps on delivery, so
        /// the moment a tester receives a build, the file stops being able to
        /// say when it was made. install-manifest.json carries the build time as
        /// a VALUE the build itself wrote, which no delivery channel rewrites.
        /// The executable's own write time is the fallback for a tree that has
        /// no manifest, and it is honestly worse.
        /// </summary>
        public static BuildIdentity BuildStamp
        {
            get
            {
                var cached = _buildStamp;
                if (cached != null) return cached;
                cached = new BuildIdentity(
                    QuickFileVersion, ProbeBuildDate(), ProbeConfiguration(), ProbeCommit());
                _buildStamp = cached;
                return cached;
            }
        }

        private static DateTime? ProbeBuildDate()
        {
            try
            {
                string generated = ReadManifestGeneratedUtc();
                if (!string.IsNullOrEmpty(generated) &&
                    DateTime.TryParse(generated,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal |
                        System.Globalization.DateTimeStyles.AssumeUniversal,
                        out DateTime utc))
                {
                    return utc.ToLocalTime();
                }
            }
            catch { }

            try
            {
                string path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return File.GetLastWriteTime(path);
            }
            catch { }

            return null;
        }

        /// <summary>
        /// The "generated" value out of install-manifest.json, read from the
        /// head of the file only.
        ///
        /// The manifest carries a SHA-256 for every one of the ~364 shipped
        /// files, so parsing the whole document to reach one string near the
        /// top would be absurd on a keypress. generate-install-manifest.ps1
        /// writes schema, source, product, version, generated in that order —
        /// well inside the first block — so a partial reader that stops at the
        /// first match gets the answer without ever seeing the file list.
        /// </summary>
        private static string ReadManifestGeneratedUtc()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "install-manifest.json");
            if (!File.Exists(path)) return null;

            byte[] head = new byte[4096];
            int read;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                read = fs.Read(head, 0, head.Length);
            }
            if (read <= 0) return null;

            var reader = new System.Text.Json.Utf8JsonReader(
                head.AsSpan(0, read), isFinalBlock: false, state: default);
            try
            {
                while (reader.Read())
                {
                    if (reader.TokenType != System.Text.Json.JsonTokenType.PropertyName) continue;
                    if (!reader.ValueTextEquals("generated")) continue;
                    if (reader.Read() && reader.TokenType == System.Text.Json.JsonTokenType.String)
                        return reader.GetString();
                    return null;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Ran off the end of the head block before finding it, or the
                // file is not what we expect. Either way the caller falls back.
            }
            return null;
        }

        /// <summary>
        /// Debug or Release, from the entry assembly's own DebuggableAttribute
        /// rather than from install-manifest.json — the manifest writes
        /// "configuration" LAST, after the whole file list, so reaching it would
        /// mean parsing the entire document. The attribute is intrinsic to the
        /// binary and cannot disagree with the binary.
        /// </summary>
        private static string ProbeConfiguration()
        {
            try
            {
                Assembly asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var d = (System.Diagnostics.DebuggableAttribute)Attribute.GetCustomAttribute(
                    asm, typeof(System.Diagnostics.DebuggableAttribute));
                if (d == null) return "Release";
                return d.DebuggingFlags.HasFlag(
                    System.Diagnostics.DebuggableAttribute.DebuggingModes.DisableOptimizations)
                    ? "Debug" : "Release";
            }
            catch { return null; }
        }

        /// <summary>
        /// The short SHA out of AssemblyInformationalVersion ("4.1.16+abc1234").
        /// </summary>
        private static string ProbeCommit()
        {
            try
            {
                Assembly asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    asm, typeof(AssemblyInformationalVersionAttribute));
                string v = info?.InformationalVersion;
                if (string.IsNullOrEmpty(v)) return null;
                int plus = v.IndexOf('+');
                if (plus < 0 || plus == v.Length - 1) return null;
                string sha = v.Substring(plus + 1);
                return sha.Length > 7 ? sha.Substring(0, 7) : sha;
            }
            catch { return null; }
        }

        /// <summary>
        /// The app's own support assemblies and the versions they report, in a
        /// fixed order. Lives HERE because this class is the single assembler of
        /// version strings, and the About page had grown a second one — twice
        /// over, in fact: the HTML renderer and the plain-text renderer each
        /// carried their own independent copy, both re-deriving FlexLib's
        /// version behind this class's back. That is the exact failure the type
        /// comment above forbids, in the component built to fight it.
        ///
        /// FlexLib is served from <see cref="FlexLibVersion"/> rather than
        /// probed again, so the Components section and the library list cannot
        /// disagree — including about the unstamped-0.0.0 warning, which a
        /// second probe would silently render as a plausible-looking version.
        /// </summary>
        public IReadOnlyList<(string Name, string Version)> ComponentAssemblies
        {
            get { return _assemblies; }
        }

        private static readonly string[] ComponentAssemblyNames =
        {
            "FlexLib", "JJLogLib", "Radios", "RadioBoxes", "JJFlexWpf", "JJTrace"
        };

        private readonly List<(string Name, string Version)> _assemblies =
            new List<(string, string)>();

        private void CaptureComponentAssemblies()
        {
            foreach (string name in ComponentAssemblyNames)
            {
                if (string.Equals(name, "FlexLib", StringComparison.OrdinalIgnoreCase))
                {
                    // One probe, one answer. CaptureFlexLib already ran.
                    _assemblies.Add((name, FlexLibVersion ?? "not available"));
                    continue;
                }

                string version = "not available";
                try
                {
                    Assembly asm = Assembly.Load(name);
                    if (!string.IsNullOrEmpty(asm.Location) && File.Exists(asm.Location))
                    {
                        var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location);
                        version = fvi.ProductVersion ?? fvi.FileVersion ?? version;
                    }
                    if (version == "not available")
                    {
                        Version v = asm.GetName().Version;
                        if (v != null) version = v.ToString();
                    }
                }
                catch { /* an assembly that is not loaded reports itself honestly */ }

                _assemblies.Add((name, version));
            }
        }

        private readonly List<DiagnosticItem> _items = new List<DiagnosticItem>();

        /// <summary>
        /// The facts as an ordered, sectioned list. This is the single source
        /// every renderer draws from — HTML, plain text, crash report — so the
        /// surfaces cannot disagree.
        /// </summary>
        public IReadOnlyList<DiagnosticItem> Items
        {
            get { return _items; }
        }

        private DiagnosticSnapshot() { }

        /// <summary>
        /// Query everything and build the item list. Never throws; every probe
        /// is individually guarded and failures report themselves honestly.
        /// </summary>
        public static DiagnosticSnapshot Capture()
        {
            var s = new DiagnosticSnapshot();
            s.CapturedAt = DateTime.Now;

            s.CaptureAppIdentity();
            s.CaptureSelfContained();
            s.CaptureDotNet();
            s.CaptureFlexLib();
            s.CaptureOpus();
            s.CapturePortAudio();
            s.CapturePrism();
            s.CaptureWebView2();
            s.CaptureEnvironment();
            s.CaptureTracing();
            s.CaptureAccessibility();
            // After CaptureFlexLib, deliberately: the assembly list serves
            // FlexLib's version from the probe that already ran.
            s.CaptureComponentAssemblies();

            s.BuildItems();
            return s;
        }

        // ------------------------------------------------------------------
        // Probes. Each one guarded; a failure leaves the property null and the
        // item builder renders an honest "not available".
        // ------------------------------------------------------------------

        private void CaptureAppIdentity()
        {
            try
            {
                // The ENTRY assembly, deliberately. Reading GetType(...).Assembly
                // from a library gave crash reports that said
                // "App: System.Windows.Forms 10.0.0.0" for months (caught live
                // 2026-08-08) — the report carried no JJFlex version at all.
                Assembly asm = Assembly.GetEntryAssembly();
                if (asm == null) asm = Assembly.GetExecutingAssembly();
                AssemblyName name = asm.GetName();
                AppName = name.Name;
                AppAssemblyVersion = name.Version != null ? name.Version.ToString() : null;

                var info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    asm, typeof(AssemblyInformationalVersionAttribute));
                if (info != null && !string.IsNullOrEmpty(info.InformationalVersion))
                    AppInformationalVersion = info.InformationalVersion;
            }
            catch { /* identity stays null; rendered as unavailable */ }

            try
            {
                ExecutablePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(ExecutablePath))
                {
                    var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(ExecutablePath);
                    if (!string.IsNullOrEmpty(fvi.FileVersion))
                        AppFileVersion = fvi.FileVersion;
                }
            }
            catch { }

            // Same assembler, one probe: the About page and the spoken build
            // chord (#269) cannot disagree about when this was built, because
            // there is only one thing that works it out.
            try
            {
                var stamp = BuildStamp;
                BuildDate = stamp.Date;
                BuildConfiguration = stamp.Configuration;
                BuildCommit = stamp.Commit;
            }
            catch { }
        }

        private void CaptureSelfContained()
        {
            try
            {
                // A runtime fact, not a build flag: where did the runtime we are
                // executing on actually load from? In a self-contained deploy,
                // System.Private.CoreLib sits in the install folder.
                string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
                if (!string.IsNullOrEmpty(runtimeDir))
                {
                    SelfContained = string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeDir)),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
                        StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    // Single-file publish reports no location; fall back to the
                    // presence of coreclr.dll beside the executable.
                    SelfContained = File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"));
                }
            }
            catch { /* stays null → "could not determine" */ }
        }

        private void CaptureDotNet()
        {
            try { DotNetRuntime = RuntimeInformation.FrameworkDescription; }
            catch { }
        }

        private void CaptureFlexLib()
        {
            try
            {
                Assembly flexAsm = Assembly.Load("FlexLib");
                string path = flexAsm.Location;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                    FlexLibVersion = fvi.ProductVersion ?? fvi.FileVersion;
                }
                if (string.IsNullOrEmpty(FlexLibVersion) && flexAsm.GetName().Version != null)
                    FlexLibVersion = flexAsm.GetName().Version.ToString();

                // FlexLib's version comes from the <Version> stamp in
                // FlexLib.csproj, which a FlexLib upgrade must bump
                // (MIGRATION.md reapply item). If the stamp is ever forgotten
                // the DLL reverts to claiming 0.0.0.0 — report that loudly
                // instead of presenting it as if it were a version.
                if (FlexLibVersion != null && FlexLibVersion.StartsWith("0.0.0", StringComparison.Ordinal))
                    FlexLibVersion = "unstamped (" + FlexLibVersion +
                        ") — FlexLib.csproj lost its <Version> stamp; see MIGRATION.md";
            }
            catch { }
        }

        private void CaptureOpus()
        {
            try
            {
                // Asks the loaded libopus.dll itself. The DLL carries no Windows
                // version resource, so this call is the only honest answer.
                OpusVersion = POpusCodec.OpusInfo.VersionString();
                if (string.IsNullOrEmpty(OpusVersion)) OpusVersion = null;
            }
            catch { /* DllNotFound / BadImageFormat → rendered as unavailable */ }
        }

        private void CapturePortAudio()
        {
            try
            {
                // Version functions are safe without Pa_Initialize. The wrapper
                // lives in the PortAudioSharp assembly, which is the assembly
                // NativeLoader registered the runtimes\ resolver for — a
                // P/Invoke declared anywhere else would miss the resolver and
                // probe the wrong path.
                PortAudioVersionText = PortAudioSharp.PortAudio.Pa_GetVersionText();
                if (string.IsNullOrEmpty(PortAudioVersionText))
                {
                    PortAudioVersionText = null;
                    return;
                }

                // "PortAudio V19.7.0-devel, revision a880212" → "a880212".
                int idx = PortAudioVersionText.IndexOf("revision", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string rev = PortAudioVersionText.Substring(idx + "revision".Length)
                        .Trim().TrimEnd('.', ',');
                    if (rev.Length > 0) PortAudioRevision = rev;
                }
            }
            catch { }
        }

        private void CapturePrism()
        {
            try
            {
                // The #171 silent channel promises prism.dll is NEVER loaded
                // with render off; a version probe would load it. Honour the
                // promise and report nothing rather than break it for a
                // version string.
                if (!OutputChannelRecorder.RenderEnabled) return;

                // Asks the loaded prism.dll itself — no context needed, the
                // string is a baked-in constant. Like libopus, the DLL carries
                // no Windows version resource, so this call is the only
                // honest answer. #9: every component names its own version.
                PrismVersion = Speech.PrismNative.ReadUtf8(
                    Speech.PrismNative.prism_version_string());
                if (string.IsNullOrEmpty(PrismVersion)) PrismVersion = null;
            }
            catch { /* DllNotFound / BadImageFormat → rendered as unavailable */ }
        }

        private void CaptureWebView2()
        {
            try
            {
                WebView2Runtime =
                    Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
                if (string.IsNullOrEmpty(WebView2Runtime)) WebView2Runtime = null;
            }
            catch { /* runtime not installed */ }
        }

        private void CaptureEnvironment()
        {
            try { OsVersion = Environment.OSVersion.VersionString; } catch { }
            try
            {
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString();
                OsArchitecture = RuntimeInformation.OSArchitecture.ToString();
            }
            catch { }
        }

        private void CaptureTracing()
        {
            try
            {
                TracingActive = JJTrace.Tracing.On;
                TraceFilePath = JJTrace.Tracing.TraceFile;
                if (!string.IsNullOrEmpty(TraceFilePath))
                {
                    TraceFolder = Path.GetDirectoryName(TraceFilePath);
                }
                else
                {
                    // Tracing never started this session. Report where traces
                    // land by convention so "where are your logs" still gets an
                    // answer. Same folder every other AppData consumer uses.
                    TraceFolder = Path.Combine(
                        RadioConfig.AppDataRoot);
                }
                if (!string.IsNullOrEmpty(TraceFolder))
                    TraceArchiveFolder = Path.Combine(TraceFolder, "Traces");
            }
            catch { }
        }

        private void CaptureAccessibility()
        {
            try
            {
                string srName = ScreenReaderOutput.ScreenReaderName;
                if (!string.IsNullOrEmpty(srName))
                    ScreenReader = srName + " detected";
                else if (ScreenReaderOutput.IsAvailable)
                    ScreenReader = "SAPI (no screen reader detected)";
                else
                    ScreenReader = "None detected";
                BrailleAvailable = ScreenReaderOutput.HasBraille;

                // Which library is actually talking, and to what. Noel asked for
                // this on 2026-08-17, the same evening a completely
                // non-functional Prism integration went unnoticed because the
                // fallback caught it and nothing anywhere named the backend in
                // use. "It speaks" turned out not to be evidence of which thing
                // was speaking.
                string backend = ScreenReaderOutput.BackendName;

                // Pair the library's SELF-REPORTED version with its identity,
                // so the Speech line answers both "what library" and "what
                // engine" in one read: "Prism 0.18.1, using NVDA directly".
                // Until 2026-08-21 the DLL exported this string and nothing
                // ever asked — #9 wants every component version honest and
                // present, and this one was silently absent.
                if (string.Equals(backend, "Prism", StringComparison.OrdinalIgnoreCase)
                    && PrismVersion != null)
                {
                    backend = backend + " " + PrismVersion;
                }

                if (string.Equals(backend, "none", StringComparison.OrdinalIgnoreCase))
                {
                    SpeechEngine = "none — the application has no speech "
                                   + "(prism.dll missing or failed to load)";
                }
                else if (!ScreenReaderOutput.IsAvailable)
                {
                    SpeechEngine = backend + " loaded, but no speech is available";
                }
                else
                {
                    // Name the TIER, not just the backend. A raw synthesiser
                    // and a screen-reader integration both report "speech
                    // works", and the difference only becomes audible when two
                    // voices collide - which is exactly the situation the
                    // operator cannot diagnose without being told.
                    string reader = string.IsNullOrEmpty(srName) ? "unknown" : srName;
                    SpeechEngine = ScreenReaderOutput.Tier switch
                    {
                        Speech.SpeechTier.ScreenReader =>
                            backend + ", using " + reader
                            + " directly (its own voice, queue and braille)",
                        Speech.SpeechTier.UiaNotifications =>
                            backend + ", via UI Automation notifications — "
                            + "whichever screen reader is attached speaks our text itself",
                        Speech.SpeechTier.Synthesiser =>
                            backend + ", using a built-in synthesiser (" + reader
                            + "). No screen reader was reachable, so this is a "
                            + "separate voice — Ctrl+Shift+V turns it off",
                        _ => backend + ", using " + reader,
                    };
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Rendering-neutral item list. Labels "App", "Build", "FileVersion"
        // and "OS" are kept verbatim from the pre-snapshot crash report format
        // so existing triage reading stays untouched.
        // ------------------------------------------------------------------

        private void BuildItems()
        {
            // Application
            Add(SectionApplication, "App",
                AppName != null ? (AppName + " " + (AppAssemblyVersion ?? "?")) : "unknown");
            if (AppInformationalVersion != null)
                Add(SectionApplication, "Build", AppInformationalVersion);
            if (AppFileVersion != null)
                Add(SectionApplication, "FileVersion", AppFileVersion);
            if (BuildDate != null)
                Add(SectionApplication, "Built", BuildDate);
            if (BuildConfiguration != null)
                Add(SectionApplication, "Configuration", BuildConfiguration);
            Add(SectionApplication, "Executable",
                ExecutablePath ?? "not available");
            Add(SectionApplication, "Self-contained",
                SelfContained == true ? "yes — the .NET runtime ships inside the install folder"
                : SelfContained == false ? "no — a shared .NET runtime install is in use"
                : "could not determine");

            // Components
            Add(SectionComponents, ".NET runtime", DotNetRuntime ?? "not available");
            Add(SectionComponents, "FlexLib", FlexLibVersion ?? "not available");
            Add(SectionComponents, "Opus",
                OpusVersion ?? "not available (libopus.dll did not load or did not answer)");
            Add(SectionComponents, "PortAudio", PortAudioDisplay());
            Add(SectionComponents, "Speech", SpeechEngine ?? "not available");
            Add(SectionComponents, "WebView2 runtime",
                WebView2Runtime ?? "not installed");

            // Environment
            Add(SectionEnvironment, "OS", OsVersion ?? "unknown");
            if (ProcessArchitecture != null)
                Add(SectionEnvironment, "Process",
                    ProcessArchitecture + " process on " + (OsArchitecture ?? "?") + " Windows");

            // Support
            if (TracingActive && !string.IsNullOrEmpty(TraceFilePath))
                Add(SectionSupport, "Trace file", TraceFilePath);
            else
                Add(SectionSupport, "Trace file",
                    "tracing is off; trace files land in " + (TraceFolder ?? "the JJFlexRadio settings folder"));
            if (!string.IsNullOrEmpty(TraceArchiveFolder))
                Add(SectionSupport, "Trace archive", TraceArchiveFolder);
            Add(SectionSupport, "Screen reader", ScreenReader ?? "unknown");
            Add(SectionSupport, "Braille display", BrailleAvailable ? "available" : "not detected");
        }

        /// <summary>
        /// The canonical PortAudio display string. LEADS with the revision —
        /// the only part of PortAudio's version text that identifies a build —
        /// and never presents a bare 19.7.0, which reads the same on a
        /// five-year-old DLL and a current one.
        /// </summary>
        private string PortAudioDisplay()
        {
            if (PortAudioVersionText == null)
                return "not available (portaudio.dll did not load or did not answer)";

            if (PortAudioRevision != null &&
                !string.Equals(PortAudioRevision, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return "revision " + PortAudioRevision +
                    " (self-reports \"" + PortAudioVersionText +
                    "\"; the revision is the build identity — the version number never changes upstream)";
            }

            return "revision unknown — this build carries no revision stamp and cannot be told apart " +
                "from any other (self-reports \"" + PortAudioVersionText + "\")";
        }

        private void Add(string section, string label, string value)
        {
            _items.Add(new DiagnosticItem(section, label, value ?? ""));
        }

        /// <summary>
        /// The canonical plain-text rendering: flat "Label: Value" lines with a
        /// blank line between sections. The crash report embeds this verbatim;
        /// the About page's plain text and clipboard output are built from the
        /// same Items, so every surface agrees by construction.
        /// </summary>
        public string ToPlainText()
        {
            var sb = new StringBuilder();
            string section = null;
            foreach (DiagnosticItem item in _items)
            {
                if (section != null && item.Section != section)
                    sb.AppendLine();
                section = item.Section;
                sb.Append(item.Label).Append(": ").AppendLine(item.Value);
            }
            return sb.ToString().TrimEnd();
        }
    }
}
