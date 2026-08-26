using System.Windows.Forms;
using Xunit;

namespace JJFlexWpf.Tests
{
    /// <summary>
    /// The runtime half of #206: KeyInventory's near-miss lookup, against the
    /// REAL LeaderCommands table — the same table the Ctrl+J help, the Keys
    /// dialog and the Command Finder read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// LeaderLayerConsistencyTests (Radios.Tests) proves the advertised
    /// strings against the switch from SOURCE; this proves the built lookup
    /// answers the questions DoLeaderCommand's unknown-command arm actually
    /// asks. Pure static data — no window, no dispatcher, no desktop, so it
    /// runs green under DeskGuard on the interactive desktop.
    /// </para>
    /// <para>
    /// The Ctrl+G case is Noel's own press from 2026-08-23, transcript
    /// LeaderInvalidTone at 256371 ms: he meant Ctrl+J then G and got
    /// "Unknown command". The assertion that G's description comes back is
    /// the assertion that he now gets told what G does instead.
    /// </para>
    /// </remarks>
    public class LeaderNearMissTests
    {
        [Fact]
        public void Ctrl_G_names_bare_G_and_what_it_does()
        {
            bool found = KeyInventory.TryFindLeaderNearMiss(
                Keys.G | Keys.Control, out string key, out string what);

            Assert.True(found, "Ctrl+G is unbound and bare G arms the test tone — the near-miss must be found");
            Assert.Equal("G", key);
            Assert.Contains("test tone", what, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_bare_form_wins_even_when_shift_is_also_bound()
        {
            // Ctrl+T is unbound; both T (meter tones) and Shift+T (alert
            // sounds) are bound. The bare form is the most likely intent and
            // must be the one named.
            bool found = KeyInventory.TryFindLeaderNearMiss(
                Keys.T | Keys.Control, out string key, out string what);

            Assert.True(found);
            Assert.Equal("T", key);
            Assert.Contains("meter tones", what, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_chord_that_is_actually_bound_is_not_a_near_miss()
        {
            // G is bound (the test tone). The unknown-command arm never runs
            // for it — but if it were asked anyway, the answer must be no,
            // or a bug elsewhere would overwrite a real command's speech.
            Assert.False(KeyInventory.TryFindLeaderNearMiss(Keys.G, out _, out _));
            Assert.False(KeyInventory.TryFindLeaderNearMiss(Keys.A | Keys.Control, out _, out _));
        }

        [Fact]
        public void A_letter_bound_at_no_tier_stays_a_plain_unknown()
        {
            // X carries nothing at bare, Shift or Ctrl. The generic
            // "Unknown command" message is then correct, and inventing an
            // alternative would be worse than silence.
            Assert.False(KeyInventory.TryFindLeaderNearMiss(Keys.X | Keys.Control, out _, out _));
        }

        [Fact]
        public void A_shifted_press_can_recover_to_the_bare_form()
        {
            // Shift+Q is unbound (Q sits outside the slice-jump row); Q is
            // the noise-profile capture.
            bool found = KeyInventory.TryFindLeaderNearMiss(
                Keys.Q | Keys.Shift, out string key, out string what);

            Assert.True(found);
            Assert.Equal("Q", key);
            Assert.Contains("noise profile", what, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_slice_jump_range_is_bound_data_not_a_near_miss()
        {
            // Shift+B lives inside the advertised Shift+A..Shift+H range, so
            // it must read as BOUND — the range expansion feeding the lookup
            // is exactly what the ExcludedKeys machinery protects.
            Assert.False(KeyInventory.TryFindLeaderNearMiss(Keys.B | Keys.Shift, out _, out _));
        }
    }
}
