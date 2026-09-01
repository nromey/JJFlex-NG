using System;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Task #316: a network adapter change under a live session crashes the
    /// app. Bringing a VPN up while connected is the reported case; docking,
    /// joining wifi and tethering are the same event, and remote operators are
    /// exactly the people whose network changes under them.
    ///
    /// <para><b>Nothing here fixes the crash, and that is deliberate.</b> It
    /// cannot be reproduced from this seat — it needs a live radio and a VPN —
    /// and a guess committed on this path would be worth less than an open
    /// finding with a working instrument pointed at it. What these pin is the
    /// instrument: the watch starts, it never throws, and it says what it saw.
    /// </para>
    /// </summary>
    public sealed class NetworkChangeWatchTests
    {
        /// <summary>
        /// Idempotent and quiet. It is called from startup, and startup paths
        /// get called twice sooner or later.
        /// </summary>
        [Fact]
        public void StartingTwiceIsHarmless()
        {
            NetworkChangeWatch.Start();
            NetworkChangeWatch.Start();
        }

        /// <summary>
        /// The snapshot reads the machine and says something specific about it.
        ///
        /// <para>This test machine has adapters, so the reading must name at
        /// least one — an <b>absence is not evidence</b>, and a snapshot that
        /// silently returned an empty string would make every future trace line
        /// say nothing while looking like it said something.</para>
        /// </summary>
        [Fact]
        public void TheSnapshotNamesWhatItFound()
        {
            string snapshot = NetworkChangeWatch.Snapshot();

            Assert.StartsWith("Adapters up:", snapshot, StringComparison.Ordinal);

            // Either it found addresses, or it says outright that it found none
            // or could not read them. What it must never do is trail off.
            Assert.True(snapshot.Length > "Adapters up: ".Length,
                "The adapter snapshot said nothing at all. A trace line that looks like a "
                + "reading but carries none is worse than no line (#316).");
        }
    }
}
