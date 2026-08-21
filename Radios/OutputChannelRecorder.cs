using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using JJTrace;

namespace Radios
{
    // ────────────────────────────────────────────────────────────────
    //  OutputChannelRecorder — the silent verification channel (#171).
    //
    //  Two INDEPENDENT switches, deliberately not a mode enum:
    //    RenderEnabled  — whether output actually sounds (speech via Prism,
    //                     earcons/CW via the NAudio mixers). Default true.
    //    RecordEnabled  — whether every output event is appended to a
    //                     JSON Lines transcript. Default false.
    //
    //  Why both can be on at once, and why that matters: the operator's
    //  most common and most expensive report is "that sounded wrong" or
    //  "it cut off", and reconstructing what happened used to mean memory
    //  plus code reading. With render AND record on, the exact event
    //  stream for the moment he is describing exists on disk. It also
    //  keeps silent mode honest — the recorded stream is produced by the
    //  same code path that renders, so a passing silent test says
    //  something real about what the operator will hear.
    //
    //  Why this exists at all: on 2026-08-21 three automated key-sweep
    //  runs produced zero valid data, every failure in the harness. The
    //  only outward sign of a Home field key was an utterance, and the
    //  only way to observe utterances was a Verbose trace capture grepped
    //  out of the meter firehose (~700 KB/min). This file is the escape:
    //  a separate, quiet, machine-readable stream. Output events must
    //  NEVER be routed into the main trace log — that recreates exactly
    //  the problem this escapes.
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records speech, CW, and earcon output events to a JSON Lines
    /// transcript, and owns the global render switch that lets the app run
    /// with all audio output diverted instead of sounded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Transcript contract (schema 1).</b> One JSON object per line.
    /// Every event carries <c>event</c> (type), <c>monotonicMs</c>
    /// (milliseconds since the recorder opened, from one shared Stopwatch —
    /// order and adjacency are assertable), and <c>utc</c> (ISO-8601 wall
    /// clock). Event types: <c>session-start</c>, <c>session-end</c>,
    /// <c>speech</c>, <c>output</c> (speech+braille via the Prism output path),
    /// <c>silence</c>, <c>cw</c>, <c>cw-cancel</c>, <c>earcon</c>,
    /// <c>earcon-start</c>, <c>earcon-stop</c>.
    /// </para>
    /// <para>
    /// <b>The session-start marker is load-bearing.</b> It is written the
    /// instant the transcript opens, carrying the app version and both
    /// switch states. A reader MUST treat a transcript with no marker as
    /// BROKEN INSTRUMENT, never as "no output" — a recorder that silently
    /// records nothing looks exactly like an app that correctly said
    /// nothing, and that shape (dead strings binary, empty capture window)
    /// is precisely what burned the 2026-08-21 sweep attempts.
    /// </para>
    /// <para>
    /// Default location: <c>%AppData%\JJFlexRadio\output-transcripts\
    /// transcript-yyyyMMdd-HHmmss-p&lt;pid&gt;.jsonl</c>. Per-session files
    /// (not one fixed name) so a later launch cannot destroy the evidence
    /// from the session the operator is describing. Old transcripts are
    /// pruned to the newest 30.
    /// </para>
    /// </remarks>
    public static class OutputChannelRecorder
    {
        /// <summary>Transcript schema version. Bump when the event shape changes.</summary>
        public const int SchemaVersion = 1;

        private const int MaxTranscriptsKept = 30;

        private static readonly object _sync = new();

        // One clock for every event — monotonic, so order is assertable even
        // across the wall-clock discontinuities (NTP, DST) that DateTime has.
        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        private static StreamWriter _writer;

        /// <summary>
        /// Whether output actually sounds. False = no audio device is opened
        /// anywhere (Prism is never loaded, the NAudio mixers are never
        /// created, Console.Beep fallbacks are skipped) — the app is runnable
        /// headless, on a machine with no sound card, while the operator is
        /// using his computer. Default true: production behaviour unchanged.
        /// </summary>
        public static bool RenderEnabled { get; private set; } = true;

        /// <summary>Whether events are being appended to a transcript.</summary>
        public static bool RecordEnabled => _writer != null;

        /// <summary>Full path of the open transcript, or null when not recording.</summary>
        public static string TranscriptPath { get; private set; }

        /// <summary>
        /// If opening the transcript failed, the reason. The reader-side
        /// contract (missing session-start marker = broken instrument) is the
        /// real protection; this is for humans debugging the harness.
        /// </summary>
        public static string LastError { get; private set; }

        /// <summary>
        /// The two switches plus transcript path as parsed from a launch
        /// environment. See <see cref="ParseStartupSwitches"/>.
        /// </summary>
        public sealed class StartupSwitches
        {
            /// <summary>Output sounds. Default true (production).</summary>
            public bool Render = true;
            /// <summary>Events go to the transcript. Default false.</summary>
            public bool Record;
            /// <summary>Explicit transcript path, or null for the default.</summary>
            public string RecordPath;
        }

        /// <summary>
        /// Parse the #171 switches from command-line arguments and environment
        /// variables. THE single source of truth for the switch syntax — used
        /// by app startup (to Configure the recorder) and by the MyApplication
        /// constructor (to exempt render-off harness instances from
        /// single-instance forwarding; see Application.Designer.vb).
        /// Syntax: <c>--no-render</c> / <c>--silent</c> turn render off;
        /// <c>--record</c> or <c>--record=&lt;path&gt;</c> turn recording on.
        /// Env vars for harnesses that launch the exe without control of its
        /// arguments: <c>JJFLEX_RENDER=0</c>, <c>JJFLEX_RECORD=1</c>,
        /// <c>JJFLEX_RECORD_PATH=&lt;path&gt;</c> (implies recording).
        /// Command-line flags win over env vars where both appear.
        /// </summary>
        public static StartupSwitches ParseStartupSwitches(IEnumerable<string> args)
        {
            var s = new StartupSwitches();
            try
            {
                s.Render = Environment.GetEnvironmentVariable("JJFLEX_RENDER") != "0";
                s.Record = Environment.GetEnvironmentVariable("JJFLEX_RECORD") == "1";
                s.RecordPath = Environment.GetEnvironmentVariable("JJFLEX_RECORD_PATH");
                if (!string.IsNullOrWhiteSpace(s.RecordPath)) s.Record = true;

                if (args != null)
                {
                    foreach (var a in args)
                    {
                        if (a == null) continue;
                        if (string.Equals(a, "--no-render", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase))
                        {
                            s.Render = false;
                        }
                        else if (string.Equals(a, "--record", StringComparison.OrdinalIgnoreCase))
                        {
                            s.Record = true;
                        }
                        else if (a.StartsWith("--record=", StringComparison.OrdinalIgnoreCase))
                        {
                            s.Record = true;
                            s.RecordPath = a.Substring("--record=".Length);
                        }
                    }
                }
            }
            catch
            {
                // A parse failure must never take the app down; production
                // defaults (render on, record off) are always safe.
            }
            return s;
        }

        /// <summary>
        /// Set the switches and, when recording, open the transcript and write
        /// the session-start marker immediately. Call once at app startup,
        /// BEFORE ScreenReaderOutput.Initialize and EarconPlayer.Initialize —
        /// both consult RenderEnabled to decide whether to open audio devices.
        /// </summary>
        /// <param name="render">Output sounds (production behaviour).</param>
        /// <param name="record">Events are appended to the transcript.</param>
        /// <param name="explicitPath">Exact transcript path (harness use), or
        /// null for the default per-session file under AppData.</param>
        public static void Configure(bool render, bool record, string explicitPath = null)
        {
            lock (_sync)
            {
                RenderEnabled = render;
                if (!record)
                {
                    CloseLocked("reconfigured");
                    return;
                }
                if (_writer != null) CloseLocked("reconfigured");

                try
                {
                    string path = explicitPath;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        string dir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "JJFlexRadio", "output-transcripts");
                        Directory.CreateDirectory(dir);
                        PruneOldTranscripts(dir);
                        path = Path.Combine(dir,
                            $"transcript-{DateTime.Now:yyyyMMdd-HHmmss}-p{Environment.ProcessId}.jsonl");
                    }
                    else
                    {
                        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    }

                    // FileShare.Read so a harness can tail the transcript live
                    // while the app appends to it.
                    var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false));
                    TranscriptPath = path;
                    LastError = null;

                    // The marker goes out the instant the channel opens.
                    // Retrofitting this costs a day — we know, because the
                    // 2026-08-21 harness failures were all missing-marker-shaped.
                    WriteLocked("session-start", w =>
                    {
                        w.WriteNumber("schema", SchemaVersion);
                        w.WriteString("appVersion", GetAppVersion());
                        w.WriteBoolean("render", RenderEnabled);
                        w.WriteBoolean("record", true);
                        w.WriteNumber("pid", Environment.ProcessId);
                    });
                }
                catch (Exception ex)
                {
                    // Fail LOUDLY in the places a harness or developer looks,
                    // then run without recording. The missing marker tells any
                    // reader the instrument is broken.
                    _writer = null;
                    TranscriptPath = null;
                    LastError = ex.Message;
                    Tracing.TraceLine($"OutputChannelRecorder: FAILED to open transcript - {ex.Message}", TraceLevel.Error);
                    try { Console.Error.WriteLine($"OutputChannelRecorder: FAILED to open transcript - {ex.Message}"); }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Write the session-end marker and close the transcript. Call at app
        /// shutdown. A transcript without a session-end line means the app
        /// crashed or was killed — every event line before that is still valid
        /// (each line is flushed as it is written).
        /// </summary>
        public static void Close()
        {
            lock (_sync) { CloseLocked("shutdown"); }
        }

        // ── Channel: speech ──

        /// <summary>
        /// Record one speech event — the exact final text after all gating and
        /// formatting ran. <paramref name="gated"/> = the verbosity filter
        /// dropped it; <paramref name="suppressed"/> = SuppressSpeech was on;
        /// <paramref name="rendered"/> = it was actually handed to the speech backend.
        /// Interrupt-vs-queue is recorded because order is where the real bugs
        /// live — one shipped defect was controls speaking on focus and cutting
        /// off the group announcement, purely an ordering problem.
        /// </summary>
        public static void RecordSpeech(string text, string level, string intent, bool interrupt,
            bool gated, bool suppressed, bool rendered, string origin)
        {
            Write("speech", w =>
            {
                w.WriteString("text", text);
                // ALWAYS emit level/intent/origin, writing an explicit JSON
                // null when the call site passed nothing. Omitting a field
                // collapses two different facts into one appearance: "this
                // utterance carried no intent" and "this field was never
                // recorded" look identical to a reader, so an assertion that
                // keys on intent silently matches nothing instead of failing.
                //
                // Found 2026-08-21, and only because someone checked: intent
                // was present on 4 of 32 events in a live connect transcript,
                // while the I3 assertion (do dialog titles queue or cut off
                // speech) was already being planned around that field. Three
                // states must stay distinguishable — a string is a real value,
                // an explicit null means the call site supplied none, and an
                // ABSENT key means a transcript written before this change.
                if (level  != null) w.WriteString("level",  level);  else w.WriteNull("level");
                if (intent != null) w.WriteString("intent", intent); else w.WriteNull("intent");
                w.WriteBoolean("interrupt", interrupt);
                w.WriteBoolean("gated", gated);
                w.WriteBoolean("suppressed", suppressed);
                w.WriteBoolean("rendered", rendered);
                if (origin != null) w.WriteString("origin", origin); else w.WriteNull("origin");
            });
        }

        /// <summary>
        /// Record which speech backend and tier came up. Written from
        /// ScreenReaderOutput.Initialize, and again on every later channel
        /// change (#167) — the UIA upgrade after the window shows, or a screen
        /// reader arriving late and displacing the synthesiser. "Spoke via
        /// NVDA" and "spoke via the fallback synthesiser" are materially
        /// different outcomes for a test to assert on — a tier moving from
        /// Synthesiser to ScreenReader in the transcript IS the recovery
        /// assertion — and a backend of "none" with render on is the
        /// app-cannot-talk deployment failure, visible on the first line
        /// anyone reads.
        /// </summary>
        public static void RecordSpeechBackend(string backendName, string reader, string tier,
            bool hasSpeech, bool hasBraille)
        {
            Write("speech-backend", w =>
            {
                w.WriteString("backend", backendName);
                if (reader != null) w.WriteString("reader", reader);
                w.WriteString("tier", tier);
                w.WriteBoolean("speech", hasSpeech);
                w.WriteBoolean("braille", hasBraille);
            });
        }

        /// <summary>Record a speech-and-braille Output call.</summary>
        public static void RecordBrailleOutput(string text, bool interrupt, bool rendered, string origin)
        {
            Write("output", w =>
            {
                w.WriteString("text", text);
                w.WriteBoolean("interrupt", interrupt);
                w.WriteBoolean("rendered", rendered);
                if (origin != null) w.WriteString("origin", origin);
            });
        }

        /// <summary>
        /// Record a Silence() call. An explicit silence is an interrupt event —
        /// a transcript reader analysing a cutoff needs to see it.
        /// </summary>
        public static void RecordSilence(string origin)
        {
            Write("silence", w =>
            {
                if (origin != null) w.WriteString("origin", origin);
            });
        }

        // ── Channel: CW ──

        /// <summary>
        /// Record one CW notification — the TEXT that would be keyed (the
        /// assertion is "SL 4/4", not a waveform), plus the operator-selectable
        /// parameters in force: WPM, sidetone pitch, and tone shape. A
        /// wrong-shape regression is otherwise inaudible to a test.
        /// </summary>
        public static void RecordCw(string text, int wpm, int pitchHz, string toneShape,
            int riseFallMs, double volume, bool rendered)
        {
            Write("cw", w =>
            {
                w.WriteString("text", text);
                w.WriteNumber("wpm", wpm);
                w.WriteNumber("pitchHz", pitchHz);
                // toneShape is the mark voice in force ("sine" default, or a
                // named CW waveform from the #145 sound-character work);
                // the envelope is always raised-cosine with riseFallMs as its
                // operator-tunable attack/release.
                w.WriteString("toneShape", toneShape);
                w.WriteString("envelope", "raised-cosine");
                w.WriteNumber("riseFallMs", riseFallMs);
                w.WriteNumber("volume", Math.Round(volume, 3));
                w.WriteBoolean("rendered", rendered);
            });
        }

        /// <summary>Record a CW cancel — an in-flight sequence was interrupted.</summary>
        public static void RecordCwCancel(string reason = null)
        {
            Write("cw-cancel", w =>
            {
                if (reason != null) w.WriteString("reason", reason);
            });
        }

        // ── Channel: earcons and notification sounds ──

        /// <summary>
        /// Record one one-shot earcon. <paramref name="gate"/> is the state of
        /// the user's earcon gate at fire time and <paramref name="rendered"/>
        /// whether audio actually reached a live mixer — the pair is what lets
        /// a reader distinguish "fired but gated off" from "never fired"
        /// (event absent). Those need different fixes and sound identical.
        /// </summary>
        public static void RecordEarcon(string name, string category, bool gate, bool rendered,
            int? freqHz = null, int? durationMs = null, double? pan = null, string detail = null)
        {
            Write("earcon", w =>
            {
                w.WriteString("name", name);
                w.WriteString("category", category);
                w.WriteBoolean("gate", gate);
                w.WriteBoolean("rendered", rendered);
                if (freqHz.HasValue) w.WriteNumber("freqHz", freqHz.Value);
                if (durationMs.HasValue) w.WriteNumber("durationMs", durationMs.Value);
                if (pan.HasValue) w.WriteNumber("pan", Math.Round(pan.Value, 3));
                if (detail != null) w.WriteString("detail", detail);
            });
        }

        /// <summary>
        /// Record the START of a continuous earcon (ATU progress, TX tone
        /// monitor). Start and stop are separate events on purpose: a tone
        /// that outlives its stop is a real bug this project has had, and a
        /// stream with an unmatched start is exactly how a test sees it.
        /// </summary>
        public static void RecordEarconStart(string name, string category, bool gate, bool rendered,
            double? freqHz = null)
        {
            Write("earcon-start", w =>
            {
                w.WriteString("name", name);
                w.WriteString("category", category);
                w.WriteBoolean("gate", gate);
                w.WriteBoolean("rendered", rendered);
                if (freqHz.HasValue) w.WriteNumber("freqHz", Math.Round(freqHz.Value, 1));
            });
        }

        /// <summary>Record the STOP of a continuous earcon.</summary>
        public static void RecordEarconStop(string name, string category)
        {
            Write("earcon-stop", w =>
            {
                w.WriteString("name", name);
                w.WriteString("category", category);
            });
        }

        // ── Internals ──

        private static void Write(string eventType, Action<Utf8JsonWriter> body)
        {
            if (_writer == null) return; // fast path: not recording
            lock (_sync)
            {
                if (_writer == null) return;
                WriteLocked(eventType, body);
            }
        }

        // Timestamp is taken INSIDE the lock so transcript lines are ordered
        // by monotonicMs — order is the property tests assert on.
        private static void WriteLocked(string eventType, Action<Utf8JsonWriter> body)
        {
            try
            {
                using var ms = new MemoryStream(256);
                using (var jw = new Utf8JsonWriter(ms))
                {
                    jw.WriteStartObject();
                    jw.WriteString("event", eventType);
                    jw.WriteNumber("monotonicMs", Math.Round(_clock.Elapsed.TotalMilliseconds, 3));
                    jw.WriteString("utc", DateTime.UtcNow.ToString("O"));
                    body(jw);
                    jw.WriteEndObject();
                }
                _writer.WriteLine(Encoding.UTF8.GetString(ms.ToArray()));
                // Flush per event: the transcript must survive a crash — a
                // partial transcript with a marker is data; a buffered one
                // lost to a crash is the silent-nothing trap again.
                _writer.Flush();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Tracing.TraceLine($"OutputChannelRecorder: write failed - {ex.Message}", TraceLevel.Error);
            }
        }

        private static void CloseLocked(string reason)
        {
            if (_writer == null) return;
            WriteLocked("session-end", w => w.WriteString("reason", reason));
            try { _writer.Dispose(); } catch { }
            _writer = null;
            TranscriptPath = null;
        }

        private static string GetAppVersion()
        {
            try
            {
                var asm = Assembly.GetEntryAssembly();
                return asm?.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
                    ?? asm?.GetName().Version?.ToString()
                    ?? "unknown";
            }
            catch { return "unknown"; }
        }

        private static void PruneOldTranscripts(string dir)
        {
            try
            {
                var stale = new DirectoryInfo(dir)
                    .GetFiles("transcript-*.jsonl")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(MaxTranscriptsKept - 1); // -1: this session adds one
                foreach (var f in stale)
                {
                    try { f.Delete(); } catch { }
                }
            }
            catch { /* pruning is best-effort */ }
        }
    }
}
