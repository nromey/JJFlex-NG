using System;
using System.Collections.Generic;
using Radios.Fixer.Evidence;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The capture scope's three rules: announce what you start (#194), leave
    /// what you found (#173), and stop only what you own — through the
    /// existing plumbing, never a packager of its own.
    /// </summary>
    public class FixerCaptureScopeTests
    {
        /// <summary>A fake capture stack that behaves like the real bridge:
        /// Start flips the capturing state, Stop archives.</summary>
        private sealed class FakeCapture
        {
            public bool Available = true;
            public bool Capturing;
            public bool StartHonored = true;
            public readonly List<string> StartReasons = new();
            public int Stops;
            public readonly List<string> Announcements = new();
            public string ArchivePath = @"C:\traces\archived.zip";

            public FixerCaptureScope.Plumbing Plumbing() => new()
            {
                IsAvailable = () => Available,
                IsCapturing = () => Capturing,
                Start = reason =>
                {
                    StartReasons.Add(reason);
                    if (StartHonored) Capturing = true;
                },
                Stop = () => { Stops++; Capturing = false; },
                LastArchivePath = () => ArchivePath,
                Announce = Announcements.Add,
            };
        }

        [Fact]
        public void Off_at_begin_starts_the_capture_and_announces_it()
        {
            var fake = new FakeCapture();
            FixerCaptureScope scope = FixerCaptureScope.Begin("ATX-357", "Transmit",
                                                              fake.Plumbing());

            Assert.True(scope.WeStartedIt);
            Assert.Equal("Transmit tests run ATX-357", Assert.Single(fake.StartReasons));
            Assert.Single(fake.Announcements);   // the recording light, spoken
            Assert.Contains("recording", scope.Note);
        }

        [Fact]
        public void End_stops_what_it_started_archives_and_announces()
        {
            var fake = new FakeCapture();
            FixerCaptureScope scope = FixerCaptureScope.Begin("ATX-357", "Transmit",
                                                              fake.Plumbing());
            fake.Announcements.Clear();

            scope.End();

            Assert.Equal(1, fake.Stops);
            Assert.Equal(@"C:\traces\archived.zip", scope.ArchivePath);
            Assert.Single(fake.Announcements);

            scope.End();   // every close path may call it
            Assert.Equal(1, fake.Stops);
            Assert.Single(fake.Announcements);
        }

        [Fact]
        public void Already_running_is_left_exactly_as_found()
        {
            var fake = new FakeCapture { Capturing = true };
            FixerCaptureScope scope = FixerCaptureScope.Begin("ATX-357", "Transmit",
                                                              fake.Plumbing());

            Assert.False(scope.WeStartedIt);
            Assert.Empty(fake.StartReasons);      // we never touched it
            Assert.Empty(fake.Announcements);     // we changed nothing, we say nothing
            Assert.Contains("already running", scope.Note);
            Assert.Contains("left running", scope.Note);

            scope.End();
            Assert.Equal(0, fake.Stops);          // still theirs, still running
            Assert.True(fake.Capturing);
            Assert.Equal("", scope.ArchivePath);
        }

        [Fact]
        public void Unavailable_capture_is_an_honest_note_not_a_failure()
        {
            var fake = new FakeCapture { Available = false };
            FixerCaptureScope scope = FixerCaptureScope.Begin("ATX-357", "Transmit",
                                                              fake.Plumbing());

            Assert.False(scope.WeStartedIt);
            Assert.Empty(fake.StartReasons);
            Assert.Contains("not available", scope.Note);
            scope.End();   // harmless
            Assert.Equal(0, fake.Stops);
        }

        [Fact]
        public void No_plumbing_at_all_reads_as_unavailable()
        {
            FixerCaptureScope scope = FixerCaptureScope.Begin("ATX-357", "Transmit", null);
            Assert.False(scope.WeStartedIt);
            Assert.Contains("not available", scope.Note);
            scope.End();
        }

        [Fact]
        public void A_start_that_does_not_take_is_admitted_and_never_owned()
        {
            var fake = new FakeCapture { StartHonored = false };
            FixerCaptureScope scope = FixerCaptureScope.Begin("ATX-357", "Transmit",
                                                              fake.Plumbing());

            Assert.Single(fake.StartReasons);     // we tried
            Assert.False(scope.WeStartedIt);      // but the state says it never took
            Assert.Contains("could not be started", scope.Note);
            Assert.Empty(fake.Announcements);     // never announce a recording that is not running

            scope.End();
            Assert.Equal(0, fake.Stops);          // owning nothing, we stop nothing
        }

        [Fact]
        public void Throwing_plumbing_never_reaches_the_run()
        {
            var plumbing = new FixerCaptureScope.Plumbing
            {
                IsAvailable = () => true,
                IsCapturing = () => false,
                Start = _ => throw new InvalidOperationException("bang"),
                Stop = () => throw new InvalidOperationException("bang"),
                LastArchivePath = () => throw new InvalidOperationException("bang"),
                Announce = _ => throw new InvalidOperationException("bang"),
            };

            FixerCaptureScope scope = FixerCaptureScope.Begin("ATX-357", "Transmit", plumbing);
            scope.End();
            Assert.False(scope.WeStartedIt);
            Assert.NotEqual("", scope.Note);
        }
    }
}
