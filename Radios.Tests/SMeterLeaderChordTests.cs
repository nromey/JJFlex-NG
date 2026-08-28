using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Ctrl+J, Ctrl+S — the dBm reading (#306) — and the S-family adjacency it
    /// lives inside.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generic leader checks already prove the chord is advertised, is
    /// handled, and has a row on both help pages. What they do not say is
    /// anything about its NEIGHBOURS, and the neighbours were the recorded
    /// concern: plain S toggles the radio's own spectral noise reduction and
    /// Shift+S toggles the PC one, so this chord sits one modifier from two
    /// unrelated DSP switches.
    /// </para>
    /// <para>
    /// A slip is recoverable — both neighbours are toggles that announce
    /// themselves by name — but only while all three tiers stay bound. If one
    /// were ever unbound, a slip onto it would fall through to the unknown
    /// arm, and the near-miss would have to answer instead. This pins the
    /// arrangement down so the reasoning stays true.
    /// </para>
    /// </remarks>
    public sealed class SMeterLeaderChordTests
    {
        private static HashSet<Keys> Advertised()
            => LeaderSourceScan.RealAdvertised(out _);

        [Fact]
        public void TheSweepSeesChordsItIsMeantToSee()
        {
            // Positive control: a set that came back empty, or that silently
            // failed to parse the Ctrl-modified form, would make every
            // assertion below vacuously true.
            HashSet<Keys> chords = Advertised();
            Assert.NotEmpty(chords);
            Assert.Contains(Keys.S, chords);                   // On-Radio Spectral NR
            Assert.Contains(Keys.Q | Keys.Control, chords);    // the QSO analyzer, a Ctrl chord
        }

        [Fact]
        public void TheDbmChordIsAdvertisedAndHandled()
        {
            Assert.Contains(Keys.S | Keys.Control, Advertised());
            Assert.Contains(Keys.S | Keys.Control, LeaderSourceScan.RealHandled());
        }

        [Fact]
        public void EveryTierOfTheSFamilyIsBoundSoASlipAlwaysLandsOnSomethingThatSpeaks()
        {
            HashSet<Keys> chords = Advertised();
            Assert.Contains(Keys.S, chords);                 // radio's spectral NR
            Assert.Contains(Keys.S | Keys.Shift, chords);    // PC spectral NR
            Assert.Contains(Keys.S | Keys.Control, chords);  // the dBm reading
        }

        [Fact]
        public void AnUnboundSTierChordHasABoundNeighbourToNameInTheNearMiss()
        {
            // The #206 near-miss turns "Unknown command" into a recovery by
            // naming one bound neighbour, bare form first. Alt+S is the one
            // S-tier chord nothing binds, so it is the case that exercises the
            // path — and it must find a neighbour rather than dead-end.
            HashSet<Keys> chords = Advertised();
            Assert.DoesNotContain(Keys.S | Keys.Alt, chords);

            IReadOnlyList<Keys> candidates =
                LeaderChordParser.NearMissCandidates(Keys.S | Keys.Alt);
            Assert.Contains(candidates, c => chords.Contains(c));

            // Bare first, which is the documented most-likely intent.
            Assert.Equal(Keys.S, candidates.First(c => chords.Contains(c)));
        }

        [Fact]
        public void TheDbmChordItselfNeverFallsThroughToTheNearMiss()
        {
            // Bound chords are not the near-miss's business. This is the
            // direction that changed: before #306, pressing Ctrl+J, Ctrl+S got
            // a near-miss pointing at the noise-reduction toggle. Now it takes
            // a reading.
            Assert.Contains(Keys.S | Keys.Control, Advertised());
        }
    }
}
