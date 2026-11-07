#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A stop request keeps the screen reader's answer instead of dropping it,
    /// and nothing reads that answer as speech having stopped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ruled by Noel 2026-10-02: "I'd accept this and also keep the prism
    /// error." <c>PrismScreenReader.Silence()</c> dropped Prism's error from
    /// the stop call inside a void method; <see cref="IScreenReader.Silence"/>
    /// now returns a <see cref="SpeechDelivery"/> the way
    /// <see cref="IScreenReader.Braille"/> does, and the three places
    /// <see cref="ScreenReaderOutput"/> asks for a stop keep it.
    /// </para>
    /// <para>
    /// <b>An accepted answer is a request taken, never speech stopped.</b> No
    /// supported backend can say when speaking has ended, so the last test
    /// here refuses any product code that reads the kept answer at all.
    /// </para>
    /// <para>
    /// In the speech statics collection because it swaps
    /// <see cref="ScreenReaderOutput"/>'s backend for a fake and puts it back.
    /// No sound: the fake speaks nowhere.
    /// </para>
    /// </remarks>
    [Collection(SpeechOutputStaticsCollection.Name)]
    public class ScreenReaderStopAnswerTests
    {
        private const BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic;

        [Fact]
        public void A_stop_the_reader_refuses_is_kept_with_its_reason()
        {
            var refusing = new FakeReader(SpeechDelivery.Failed("prism_backend_stop to NVDA returned InternalBackendError"));
            WithBackend(refusing, () =>
            {
                ScreenReaderOutput.Silence();

                Assert.Equal(1, refusing.Stops);
                var (answer, caller) = ScreenReaderOutput.LastSilenceAnswer;
                Assert.True(answer.Refused);
                Assert.Contains("prism_backend_stop", answer.Failure);
                Assert.Equal("a silence request", caller);
            });
        }

        [Fact]
        public void A_stop_the_reader_takes_is_kept_as_accepted()
        {
            var taking = new FakeReader(SpeechDelivery.Accepted);
            WithBackend(taking, () =>
            {
                ScreenReaderOutput.Silence();

                var (answer, _) = ScreenReaderOutput.LastSilenceAnswer;
                Assert.True(answer.Delivered);
                Assert.False(answer.Refused);
            });
        }

        [Fact]
        public void The_no_backend_and_diverted_readers_answer_as_their_speech_does()
        {
            Assert.Equal(SpeechDelivery.NotAttempted, new NullScreenReader().Silence());
            Assert.True(new DivertedScreenReader().Silence().Delivered);
            Assert.Equal(SpeechDelivery.Accepted, PrismScreenReader.Classify(PrismError.Ok, "stop", "NVDA"));
            SpeechDelivery refused = PrismScreenReader.Classify(PrismError.SpeakFailure, "stop", "NVDA");
            Assert.True(refused.Refused);
            Assert.Contains("prism_backend_stop to NVDA", refused.Failure);
        }

        [Fact]
        public void The_public_silence_keeps_its_void_signature()
        {
            MethodInfo silence = typeof(ScreenReaderOutput).GetMethod(nameof(ScreenReaderOutput.Silence),
                BindingFlags.Static | BindingFlags.Public)!;
            Assert.Equal(typeof(void), silence.ReturnType);
            Assert.Equal(typeof(SpeechDelivery), typeof(IScreenReader).GetMethod(nameof(IScreenReader.Silence))!.ReturnType);
        }

        [Fact]
        public void No_product_code_reads_the_kept_answer()
        {
            // The answer is a request taken, not speech stopped. A caller that
            // released a turn or started the next sentence on it would bring
            // back the defect the backends' missing completion signal is about.
            // So the only product file that may name it is the one that keeps it.
            string root = RepoRoot();
            var readers = new[] { "Radios", "JJFlexWpf", "JJTrace" }
                .Select(d => Path.Combine(root, d))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.EnumerateFiles(root, "*.vb", SearchOption.TopDirectoryOnly))
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                         && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Where(f => File.ReadAllText(f).Contains("LastSilenceAnswer", StringComparison.Ordinal))
                .Select(f => Path.GetRelativePath(root, f))
                .ToArray();

            Assert.Equal(new[] { Path.Combine("Radios", "ScreenReaderOutput.cs") }, readers);

            // Positive control: the search does see the file that keeps it.
            string keeper = File.ReadAllText(Path.Combine(root, "Radios", "ScreenReaderOutput.cs"));
            Assert.Contains("LastSilenceAnswer = (answer, caller);", keeper);
        }

        private static void WithBackend(IScreenReader fake, Action body)
        {
            FieldInfo backend = typeof(ScreenReaderOutput).GetField("_backend", Statics)!;
            FieldInfo available = typeof(ScreenReaderOutput).GetField("_available", Statics)!;
            object? savedBackend = backend.GetValue(null);
            object? savedAvailable = available.GetValue(null);
            backend.SetValue(null, fake);
            available.SetValue(null, true);
            try { body(); }
            finally
            {
                backend.SetValue(null, savedBackend);
                available.SetValue(null, savedAvailable);
            }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }

        private sealed class FakeReader : IScreenReader
        {
            private readonly SpeechDelivery _stopAnswer;
            public FakeReader(SpeechDelivery stopAnswer) { _stopAnswer = stopAnswer; }
            public int Stops { get; private set; }
            public string BackendName => "fake";
            public string? DetectedReader => null;
            public bool HasSpeech => true;
            public bool HasBraille => false;
            public bool Initialize() => true;
            public SpeechDelivery Speak(string message, bool interrupt) => SpeechDelivery.NotAttempted;
            public SpeechDelivery Output(string message, bool interrupt) => SpeechDelivery.NotAttempted;
            public SpeechDelivery Braille(string message) => SpeechDelivery.NotAttempted;
            public SpeechDelivery Silence() { Stops++; return _stopAnswer; }
            public void Dispose() { }
        }
    }
}
