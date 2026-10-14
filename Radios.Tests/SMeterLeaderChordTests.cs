using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Ctrl+J, Ctrl+S — the S-meter unit toggle (#337) — and the S-family
    /// adjacency it lives inside.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generic leader checks already prove the chord is advertised, is
    /// handled, and has a row on both help pages. What they do not say is
    /// anything about its NEIGHBOURS, and the neighbours were the recorded
    /// concern: until Sprint 48 Track F plain S toggled the radio's own
    /// spectral noise reduction and Shift+S the PC one, so this chord sat one
    /// modifier from two unrelated DSP switches.
    /// </para>
    /// <para>
    /// Track F moved both into the noise layer (JJ key N, #518), so Ctrl+S is
    /// now the ONLY bound chord on the letter at the top level. The reasoning
    /// this file pinned — a slip always lands on something that speaks — has
    /// to stay true the other way round: a slip onto S, Shift+S or Alt+S now
    /// falls through to the unknown arm, and the near-miss (#206) must name
    /// Ctrl+S rather than dead-end. For an operator whose fingers learned the
    /// old map that answer is honest but unhelpful — they wanted the spectral
    /// reducer, and are told about the S-meter — which the Track F report
    /// names as the cost of the move. This file pins what IS, so the help and
    /// the near-miss cannot drift from it.
    /// </para>
    /// <para>
    /// The chord took a one-shot dBm READING for one day (#306, Sprint 37
    /// Track G) before #337 made it the unit toggle. Nothing here depended on
    /// which of the two it was — the chord is bound either way, and that is
    /// all these checks are about.
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
            Assert.Contains(Keys.N, chords);                   // the noise layer's door, a plain letter
            Assert.Contains(Keys.Q | Keys.Control, chords);    // the QSO analyzer, a Ctrl chord
        }

        [Fact]
        public void TheUnitToggleChordIsAdvertisedAndHandled()
        {
            Assert.Contains(Keys.S | Keys.Control, Advertised());
            Assert.Contains(Keys.S | Keys.Control, LeaderSourceScan.RealHandled());
        }

        [Fact]
        public void OnlyTheUnitToggleIsBoundOnTheLetterAtTheTopLevel()
        {
            // Sprint 48 Track F: the radio's spectral NR (plain S) and the PC
            // one (Shift+S) live in the noise layer now. Shift+S is a slice
            // jump's letter beyond H, so nothing binds it here either — which
            // is the Shift tier's rule holding at the top level (#515).
            HashSet<Keys> chords = Advertised();
            Assert.DoesNotContain(Keys.S, chords);
            Assert.DoesNotContain(Keys.S | Keys.Shift, chords);
            Assert.DoesNotContain(Keys.S | Keys.Alt, chords);
            Assert.Contains(Keys.S | Keys.Control, chords);  // the S-meter unit toggle
        }

        [Fact]
        public void EverySlipOntoTheLetterHasTheUnitToggleToNameInTheNearMiss()
        {
            // The #206 near-miss turns "Unknown command" into a recovery by
            // naming the first bound neighbour in LeaderChordParser's order
            // (bare, Ctrl, Alt, Shift). Three S chords are unbound now, and
            // for each the first bound neighbour must be Ctrl+S — a slip must
            // land on something that speaks, never dead-end. That it names
            // the S-meter to a hand reaching for spectral NR is the cost the
            // move was made with; the help pages say so.
            HashSet<Keys> chords = Advertised();
            foreach (Keys slip in new[] { Keys.S, Keys.S | Keys.Shift, Keys.S | Keys.Alt })
            {
                Assert.DoesNotContain(slip, chords);
                IReadOnlyList<Keys> candidates = LeaderChordParser.NearMissCandidates(slip);
                Assert.Contains(candidates, c => chords.Contains(c));
                Assert.Equal(Keys.S | Keys.Control, candidates.First(c => chords.Contains(c)));
            }
        }

        [Fact]
        public void TheUnitToggleChordItselfNeverFallsThroughToTheNearMiss()
        {
            // Bound chords are not the near-miss's business. This is the
            // direction that changed: before #306 bound it, pressing Ctrl+J,
            // Ctrl+S got a near-miss pointing at the noise-reduction toggle.
            // Now it switches the S-meter's unit.
            Assert.Contains(Keys.S | Keys.Control, Advertised());
        }
    }
}
