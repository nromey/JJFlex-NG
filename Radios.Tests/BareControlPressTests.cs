using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The bare Control that skips Check your audio's countdown (#835, ruled by
    /// Noel 2026-10-29 01:05): Control pressed and released with no other key.
    /// </summary>
    /// <remarks>
    /// <b>Why bare matters.</b> Ctrl+Enter, which CANCELS the count, and
    /// Ctrl+Space both begin with Control down. A skip on Control-down, or on
    /// any Control release, would turn the cancel into a transmission. These
    /// tests hold that every chord that merely starts with Control stays a
    /// chord. What they cannot see: which keys WPF actually delivers to the
    /// window under each screen reader; that is a bench step.
    /// </remarks>
    public sealed class BareControlPressTests
    {
        [Fact]
        public void Control_pressed_and_released_alone_is_a_bare_press()
        {
            var p = new BareControlPress();
            p.KeyDown(isControl: true, isRepeat: false);
            Assert.True(p.KeyUp(isControl: true));
        }

        [Fact]
        public void A_held_Control_that_repeats_is_still_bare()
        {
            var p = new BareControlPress();
            p.KeyDown(true, false);
            p.KeyDown(true, true);
            p.KeyDown(true, true);
            Assert.True(p.KeyUp(true));
        }

        [Fact]
        public void Ctrl_Enter_the_cancel_is_never_a_bare_press()
        {
            var p = new BareControlPress();
            p.KeyDown(true, false);
            p.KeyDown(false, false);   // Enter
            Assert.False(p.KeyUp(false));
            Assert.False(p.KeyUp(true)); // releasing Control after Enter must not skip
        }

        [Fact]
        public void Ctrl_Space_and_Control_with_any_other_modifier_are_not_bare()
        {
            var space = new BareControlPress();
            space.KeyDown(true, false);
            space.KeyDown(false, false);
            space.KeyDown(false, true);  // Space repeating
            Assert.False(space.KeyUp(true));

            var shift = new BareControlPress();
            shift.KeyDown(false, false); // Shift first
            shift.KeyDown(true, false, otherModifierHeld: true);
            Assert.False(shift.KeyUp(true));

            // And a chord seen only through the modifier state still counts.
            var alt = new BareControlPress();
            alt.KeyDown(true, false);
            alt.KeyDown(true, true, otherModifierHeld: true);
            Assert.False(alt.KeyUp(true));
        }

        [Fact]
        public void A_key_released_without_its_press_being_seen_is_not_bare()
        {
            // Control went down in another window (or before this one had
            // focus) and comes up here: no press was seen, so nothing fires.
            var p = new BareControlPress();
            Assert.False(p.KeyUp(true));
        }

        [Fact]
        public void A_bare_press_fires_once_and_the_next_one_starts_clean()
        {
            var p = new BareControlPress();
            p.KeyDown(true, false);
            Assert.True(p.KeyUp(true));
            Assert.False(p.KeyUp(true));

            // A chord, then a fresh bare press.
            p.KeyDown(true, false);
            p.KeyDown(false, false);
            Assert.False(p.KeyUp(true));
            p.KeyDown(true, false);
            Assert.True(p.KeyUp(true));
        }

        [Fact]
        public void Losing_focus_forgets_a_press_in_progress()
        {
            var p = new BareControlPress();
            p.KeyDown(true, false);
            p.Reset();
            Assert.False(p.KeyUp(true));
        }

        /// <summary>
        /// The window must see every key, INCLUDING the ones it has already
        /// handled. Ctrl+Enter's Enter is handled by the window's own key
        /// handler (it is the cancel); a tracker that did not see it would read
        /// the Control release that follows as bare, and turn the cancel into a
        /// transmission. A source rule, because the window is never built here.
        /// </summary>
        [Fact]
        public void Check_your_audio_feeds_the_tracker_every_key_even_ones_already_handled()
        {
            string path = System.IO.Path.Combine(IntegrationPassTree.Root,
                "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Countdown.cs");
            Assert.True(System.IO.File.Exists(path), "The countdown seam has moved; this rule reads nothing.");
            string src = System.IO.File.ReadAllText(path);
            Assert.Contains("AddHandler(PreviewKeyDownEvent, new KeyEventHandler(CountdownSkipKeyDown), handledEventsToo: true)", src);
            Assert.Contains("AddHandler(PreviewKeyUpEvent, new KeyEventHandler(CountdownSkipKeyUp), handledEventsToo: true)", src);
            // And the skip is reached only from the bare release.
            int up = src.IndexOf("private void CountdownSkipKeyUp", System.StringComparison.Ordinal);
            Assert.True(up > 0);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(src, @"\.Skip\(\)").Count);
            Assert.True(src.IndexOf(".Skip()", System.StringComparison.Ordinal) > up);
            Assert.True(src.IndexOf("if (!bare) return;", up, System.StringComparison.Ordinal) > up);
        }

        [Fact]
        public void Two_Control_keys_held_together_are_still_one_bare_press()
        {
            // Left and Right Control are both Control; holding both and
            // letting go is still nothing but Control. It fires on the first
            // release and not again.
            var p = new BareControlPress();
            p.KeyDown(true, false);
            p.KeyDown(true, false);
            Assert.True(p.KeyUp(true));
            Assert.False(p.KeyUp(true));
        }
    }
}
