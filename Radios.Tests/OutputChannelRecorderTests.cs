using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Radios;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  Tests for the silent verification channel (#171).
    //
    //  Every test runs with render OFF — that is the whole point of the
    //  channel (no Tolk load, no audio device, runnable on a headless CI
    //  box), and it keeps the test host from ever touching Tolk.dll.
    //
    //  The recorder is process-global static state, so all tests live in
    //  this one class (xUnit runs tests within a class sequentially) and
    //  each test Configures its own transcript file.
    //
    //  Sprint 35 Track M: no longer one class — SpeechCoalescerTimingTests
    //  also Configures the recorder, so the one-class rule became the
    //  collection below (same evolution RadioConfigStaticsCollection went
    //  through, and this time before the predicted parallel-trample failure
    //  rather than after it).
    // ────────────────────────────────────────────────────────────────
    [Collection(SpeechOutputStaticsCollection.Name)]
    public class OutputChannelRecorderTests : IDisposable
    {
        private readonly string _path;

        public OutputChannelRecorderTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "jjflex-recorder-tests");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jsonl");
        }

        public void Dispose()
        {
            // Leave the process in the no-record state and clean the file.
            OutputChannelRecorder.Configure(render: false, record: false);
            ScreenReaderOutput.SuppressSpeech = false;
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
            try { File.Delete(_path); } catch { }
        }

        private JsonDocument[] ReadEvents()
        {
            // FileShare.ReadWrite: the recorder holds the file open for append.
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => JsonDocument.Parse(l))
                .ToArray();
        }

        private static string Ev(JsonDocument d) => d.RootElement.GetProperty("event").GetString();

        [Fact]
        public void SessionStartMarker_IsFirstLine_AndCarriesSwitches()
        {
            // The marker is the broken-instrument tripwire: a reader that finds
            // no marker must conclude the recorder never opened, not that the
            // app said nothing. It must exist the instant Configure returns.
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);

            var events = ReadEvents();
            Assert.NotEmpty(events);
            var marker = events[0].RootElement;
            Assert.Equal("session-start", marker.GetProperty("event").GetString());
            Assert.Equal(OutputChannelRecorder.SchemaVersion, marker.GetProperty("schema").GetInt32());
            Assert.False(marker.GetProperty("render").GetBoolean());
            Assert.True(marker.GetProperty("record").GetBoolean());
            Assert.Equal(Environment.ProcessId, marker.GetProperty("pid").GetInt32());
            Assert.False(string.IsNullOrEmpty(marker.GetProperty("appVersion").GetString()));
            Assert.True(marker.TryGetProperty("monotonicMs", out _));
            Assert.True(marker.TryGetProperty("utc", out _));
        }

        [Fact]
        public void Speak_WithRenderOff_RecordsFinalTextLevelAndInterrupt()
        {
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;

            ScreenReaderOutput.Speak("Connected to Bench 6600", VerbosityLevel.Critical, true);

            var speech = ReadEvents().Single(d => Ev(d) == "speech").RootElement;
            Assert.Equal("Connected to Bench 6600", speech.GetProperty("text").GetString());
            Assert.Equal("Critical", speech.GetProperty("level").GetString());
            Assert.True(speech.GetProperty("interrupt").GetBoolean());
            Assert.False(speech.GetProperty("gated").GetBoolean());
            Assert.False(speech.GetProperty("suppressed").GetBoolean());
            // Render is off, so it must not claim to have sounded.
            Assert.False(speech.GetProperty("rendered").GetBoolean());
            // Origin comes from caller-info attributes — this file, this test.
            Assert.Contains("OutputChannelRecorderTests.cs", speech.GetProperty("origin").GetString());
        }

        [Fact]
        public void Speak_GatedByVerbosity_IsRecordedWithGatedFlag()
        {
            // The distinction this buys: "fired but the verbosity filter
            // dropped it" (event present, gated true) versus "never fired"
            // (event absent). Both sound like nothing.
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical; // speech "off"

            ScreenReaderOutput.Speak("a chatty hint", VerbosityLevel.Chatty);

            var speech = ReadEvents().Single(d => Ev(d) == "speech").RootElement;
            Assert.True(speech.GetProperty("gated").GetBoolean());
            Assert.False(speech.GetProperty("rendered").GetBoolean());
        }

        [Fact]
        public void Speak_Suppressed_IsDivertedNotDropped()
        {
            // SuppressSpeech used to mean DROPPED. It now means DIVERTED: not
            // sounded, but still visible to the transcript with its flag.
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
            ScreenReaderOutput.SuppressSpeech = true;
            try
            {
                ScreenReaderOutput.Speak("menu transition noise");
            }
            finally
            {
                ScreenReaderOutput.SuppressSpeech = false;
            }

            var speech = ReadEvents().Single(d => Ev(d) == "speech").RootElement;
            Assert.True(speech.GetProperty("suppressed").GetBoolean());
            Assert.False(speech.GetProperty("rendered").GetBoolean());
        }

        [Fact]
        public void Speak_QueueIntent_RecordsIntentAndQueuedForm()
        {
            // The intent engine is the production path new code uses; the
            // transcript must show the intent AND the final interrupt-vs-queue
            // form, because interrupt-vs-queue ordering is where the real bugs
            // live.
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;

            ScreenReaderOutput.Speak("startup series item", Speech.SpeechIntent.Queue,
                VerbosityLevel.Critical);

            var speech = ReadEvents().Single(d => Ev(d) == "speech").RootElement;
            Assert.Equal("Queue", speech.GetProperty("intent").GetString());
            Assert.False(speech.GetProperty("interrupt").GetBoolean());
            Assert.False(speech.GetProperty("gated").GetBoolean());
        }

        [Fact]
        public void MonotonicTimestamps_NeverDecrease()
        {
            // Order is where the real bugs live (focus speech cutting off group
            // announcements was purely an ordering defect) — so the transcript
            // must be assertable on order.
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;

            ScreenReaderOutput.Speak("first");
            ScreenReaderOutput.Speak("second");
            ScreenReaderOutput.Speak("third");

            var times = ReadEvents()
                .Select(d => d.RootElement.GetProperty("monotonicMs").GetDouble())
                .ToArray();
            Assert.True(times.Length >= 4); // marker + 3 speech
            for (int i = 1; i < times.Length; i++)
                Assert.True(times[i] >= times[i - 1],
                    $"monotonicMs went backwards at line {i}: {times[i - 1]} -> {times[i]}");
        }

        [Fact]
        public void Silence_IsRecorded()
        {
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);

            ScreenReaderOutput.Silence();

            // LINQ Single throws if the event is absent or duplicated.
            var silence = ReadEvents().Single(d => Ev(d) == "silence");
            Assert.NotNull(silence);
        }

        [Fact]
        public void CwEvent_CarriesTextAndOperatorParameters()
        {
            // The CW assertion is the TEXT that would be keyed, not a waveform —
            // plus WPM, pitch, and tone shape, because those are operator-
            // selectable and a wrong-shape regression is otherwise inaudible.
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);

            OutputChannelRecorder.RecordCw("SL 4/4", wpm: 25, pitchHz: 700,
                toneShape: "sine", riseFallMs: 5, volume: 0.25, rendered: false);

            var cw = ReadEvents().Single(d => Ev(d) == "cw").RootElement;
            Assert.Equal("SL 4/4", cw.GetProperty("text").GetString());
            Assert.Equal(25, cw.GetProperty("wpm").GetInt32());
            Assert.Equal(700, cw.GetProperty("pitchHz").GetInt32());
            Assert.Equal("sine", cw.GetProperty("toneShape").GetString());
            Assert.Equal("raised-cosine", cw.GetProperty("envelope").GetString());
            Assert.Equal(5, cw.GetProperty("riseFallMs").GetInt32());
            Assert.False(cw.GetProperty("rendered").GetBoolean());
        }

        [Fact]
        public void EarconEvent_DistinguishesGatedFromRendered()
        {
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);

            // gate=false, rendered=false: fired but the category gate ate it.
            OutputChannelRecorder.RecordEarcon("ConfirmTone", "alert",
                gate: false, rendered: false, freqHz: 800, durationMs: 135);

            var earcon = ReadEvents().Single(d => Ev(d) == "earcon").RootElement;
            Assert.Equal("ConfirmTone", earcon.GetProperty("name").GetString());
            Assert.Equal("alert", earcon.GetProperty("category").GetString());
            Assert.False(earcon.GetProperty("gate").GetBoolean());
            Assert.False(earcon.GetProperty("rendered").GetBoolean());
            Assert.Equal(800, earcon.GetProperty("freqHz").GetInt32());
        }

        [Fact]
        public void ContinuousEarcon_StartAndStop_AreSeparateEvents()
        {
            // A tone that outlives its stop is a real bug this project has had.
            // A transcript with an unmatched earcon-start is how a test sees it,
            // so start and stop must be separate lines.
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);

            OutputChannelRecorder.RecordEarconStart("ATUProgress", "alert",
                gate: true, rendered: false, freqHz: 450);
            OutputChannelRecorder.RecordEarconStop("ATUProgress", "alert");

            var events = ReadEvents();
            var start = events.Single(d => Ev(d) == "earcon-start").RootElement;
            var stop = events.Single(d => Ev(d) == "earcon-stop").RootElement;
            Assert.Equal("ATUProgress", start.GetProperty("name").GetString());
            Assert.Equal("ATUProgress", stop.GetProperty("name").GetString());
            Assert.True(start.GetProperty("monotonicMs").GetDouble()
                <= stop.GetProperty("monotonicMs").GetDouble());
        }

        [Fact]
        public void Close_WritesSessionEndMarker()
        {
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);
            ScreenReaderOutput.Speak("last words");

            OutputChannelRecorder.Close();

            var events = ReadEvents();
            Assert.Equal("session-end", Ev(events.Last()));
            Assert.False(OutputChannelRecorder.RecordEnabled);
            Assert.Null(OutputChannelRecorder.TranscriptPath);
        }

        [Fact]
        public void ParseStartupSwitches_DefaultsAreProduction()
        {
            var s = OutputChannelRecorder.ParseStartupSwitches(new[] { "jjflexible.exe" });
            Assert.True(s.Render);
            Assert.False(s.Record);
            Assert.Null(s.RecordPath);
        }

        [Fact]
        public void ParseStartupSwitches_ReadsFlags()
        {
            var s = OutputChannelRecorder.ParseStartupSwitches(
                new[] { "jjflexible.exe", "--no-render", @"--record=C:\temp\t.jsonl" });
            Assert.False(s.Render);
            Assert.True(s.Record);
            Assert.Equal(@"C:\temp\t.jsonl", s.RecordPath);
        }

        [Fact]
        public void ParseStartupSwitches_RecordPathEnvVarImpliesRecording()
        {
            try
            {
                Environment.SetEnvironmentVariable("JJFLEX_RECORD_PATH", @"C:\temp\env.jsonl");
                var s = OutputChannelRecorder.ParseStartupSwitches(Array.Empty<string>());
                Assert.True(s.Record);
                Assert.Equal(@"C:\temp\env.jsonl", s.RecordPath);
            }
            finally
            {
                Environment.SetEnvironmentVariable("JJFLEX_RECORD_PATH", null);
            }
        }

        [Fact]
        public void NotRecording_ProducesNoFileAndNoErrors()
        {
            OutputChannelRecorder.Configure(render: false, record: false);

            // Full speech path with neither render nor record: everything runs,
            // nothing sounds, nothing is written, nothing throws.
            ScreenReaderOutput.Speak("into the void", VerbosityLevel.Critical, true);

            Assert.False(OutputChannelRecorder.RecordEnabled);
            Assert.Null(OutputChannelRecorder.TranscriptPath);
            Assert.False(File.Exists(_path));
        }
    }
}
