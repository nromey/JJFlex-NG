using System;
using System.IO;
using System.Reflection;
using JJPortaudio;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1145, the hazard fixed first: a stream closed by a failed start used to
    /// post a NULL stream block to the AudioServer on the next start, stop or
    /// close, and the server dereferenced it on a thread with no handler, which
    /// ends the process. Reachable today through a microphone start that fails
    /// twice. <see cref="Audio"/>'s start, stop and close now refuse a closed
    /// stream before posting anything.
    /// <para><b>Why these tests are honest.</b> No test in this process ever
    /// initialises the AudioServer, so its work queue is null and any post
    /// would throw. Each test asserts that first. No device is opened, no
    /// PortAudio call is made, nothing is heard.</para>
    /// </summary>
    public sealed class AudioClosedStreamTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>An engine stream in the stream table, as Audio's constructor leaves it. Nothing opened.</summary>
        internal static Audio NewAudioInTable(out int key)
        {
            Assert.Null(AudioAnchor.work); // a post would throw, so a pass means nothing was posted
            var audio = (Audio)Activator.CreateInstance(typeof(Audio), nonPublic: true)!;
            key = (int)typeof(Audio).GetField("qKey", Private)!.GetValue(audio)!;
            Assert.NotNull(Audio.queues.getQ(key));
            return audio;
        }

        /// <summary>An engine stream already closed: what a failed start leaves.</summary>
        internal static Audio ClosedAudio()
        {
            var audio = NewAudioInTable(out int key);
            Audio.queues.Remove(key); // what the server's close does
            Assert.Null(Audio.queues.getQ(key));
            return audio;
        }

        [Fact]
        public void Start_on_a_closed_stream_refuses_without_posting()
        {
            var audio = ClosedAudio();
            Assert.False(audio.Start());
            Assert.False(audio.Start()); // and again, as the gate retries
            Assert.False(audio.IsOpen);
        }

        [Fact]
        public void Stop_on_a_closed_stream_does_nothing()
        {
            var audio = ClosedAudio();
            audio.Stop();
            Assert.Null(audio.TheQ);
        }

        [Fact]
        public void Finished_on_a_closed_stream_does_nothing_and_does_not_wait()
        {
            var audio = ClosedAudio();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            audio.Finished();
            Assert.True(sw.ElapsedMilliseconds < 1000, "Finished waited " + sw.ElapsedMilliseconds + " ms for a close it never posted");
        }

        /// <summary>
        /// The JJAudioStream layer the remote-audio loop uses: a refused start,
        /// a stop and a close on a closed stream all survive, and the stream
        /// says it is not open rather than letting the caller find out by
        /// exception.
        /// </summary>
        [Fact]
        public void A_closed_JJAudioStream_starts_stops_and_closes_safely()
        {
            var stream = new JJAudioStream();
            Assert.False(stream.IsOpen); // never opened
            typeof(JJAudioStream).GetField("aud", Private)!.SetValue(stream, ClosedAudio());
            Assert.False(stream.IsOpen);
            Assert.False(stream.StartAudio());
            Assert.True(stream.StopAudio());
            stream.Close();
        }

        /// <summary>The server backstop, read from source: the server thread cannot be run without PortAudio.</summary>
        [Fact]
        public void The_audio_server_skips_a_work_item_with_no_stream()
        {
            string audio = File.ReadAllText(Path.Combine(RepoRoot(), "JJPortaudio", "JJPortaudio", "Audio.cs"));
            int take = audio.IndexOf("item = work.Take();", StringComparison.Ordinal);
            int sw = audio.IndexOf("switch (item.Type)", take, StringComparison.Ordinal);
            Assert.True(take > 0 && sw > take, "serverProc's take and switch were not found");
            string between = audio.Substring(take, sw - take);
            Assert.Contains("item.StreamBlock == null && item.Type != workItems.terminate", between);
            Assert.Contains("continue;", between);
        }

        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("the repository root was not found");
        }
    }
}
