using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track E, E7: the one engine change the noise layer needed.
    /// A layer may take a NAMED set of Shift+Alt chords through
    /// <see cref="ValueSubLayerDefinition.ShiftAltKeys"/>; every plain Alt
    /// chord, Alt+F4 and Shift+Alt+F4 still pass through, and a layer that
    /// sets nothing (the audio and filter layers) is exactly as it was.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ValueLayerShiftAltTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(ValueLayerShiftAltTests));
        public ValueLayerShiftAltTests() => Lexicon.Load(Lexicon.Partitions);
        public void Dispose() => _scope.Dispose();

        private static ValueSubLayer Open(Func<Keys, bool>? shiftAlt, List<Keys> host)
        {
            var def = new ValueSubLayerDefinition
            {
                Id = "noise",
                Selection = ValueLayerSelection.ByLetter,
                Targets = new List<ValueTarget> { new() { Id = "x", Read = () => 0, Apply = _ => { }, Number = _ => "" } },
                InitialTarget = -1,
                DescribeClosed = () => "closed",
                HostKeys = k => { host.Add(k); return false; },
                ShiftAltKeys = shiftAlt,
            };
            return ValueSubLayer.EnterForTest(def, (_, _) => { }, (_, _) => { }, _ => { }, () => VerbosityLevel.Terse);
        }

        private static readonly Keys[] Claimed =
        {
            Keys.A | Keys.Shift | Keys.Alt, Keys.F | Keys.Shift | Keys.Alt, Keys.R | Keys.Shift | Keys.Alt,
            Keys.OemCloseBrackets | Keys.Shift | Keys.Alt, Keys.OemOpenBrackets | Keys.Shift | Keys.Alt,
        };

        [Fact]
        public void The_named_Shift_Alt_chords_reach_the_layer_and_every_other_Alt_chord_passes_through()
        {
            var taken = new List<Keys>();
            var layer = Open(k => { if (Array.IndexOf(Claimed, k) < 0) return false; taken.Add(k); return true; }, new List<Keys>());
            foreach (var k in Claimed) Assert.Equal(ValueLayerKeyResult.Handled, layer.HandleKey(k));
            Assert.Equal(Claimed, taken);

            // Plain Alt chords: the menu bar's, and AM, FM, the Radio menu and filter presets outside.
            foreach (var k in new[] { Keys.A | Keys.Alt, Keys.F | Keys.Alt, Keys.R | Keys.Alt, Keys.OemCloseBrackets | Keys.Alt,
                                      Keys.F4 | Keys.Alt, Keys.F4 | Keys.Alt | Keys.Shift, Keys.T | Keys.Alt | Keys.Shift,
                                      Keys.Up | Keys.Alt | Keys.Shift, Keys.F | Keys.Alt | Keys.Control | Keys.Shift })
                Assert.Equal(ValueLayerKeyResult.PassThrough, layer.HandleKey(k));
            Assert.Equal(Claimed, taken);
            Assert.True(layer.IsLive);
        }

        [Fact]
        public void Alt_F4_is_never_offered_even_to_a_hook_that_would_take_anything()
        {
            var offered = new List<Keys>();
            var layer = Open(k => { offered.Add(k); return true; }, new List<Keys>());
            Assert.Equal(ValueLayerKeyResult.PassThrough, layer.HandleKey(Keys.F4 | Keys.Alt));
            Assert.Equal(ValueLayerKeyResult.PassThrough, layer.HandleKey(Keys.F4 | Keys.Alt | Keys.Shift));
            Assert.Equal(ValueLayerKeyResult.PassThrough, layer.HandleKey(Keys.F | Keys.Alt));
            Assert.Empty(offered);
        }

        [Fact]
        public void A_layer_that_names_no_Shift_Alt_chords_passes_every_Alt_chord_through_as_before()
        {
            var host = new List<Keys>();
            var layer = Open(null, host);
            foreach (var k in Claimed) Assert.Equal(ValueLayerKeyResult.PassThrough, layer.HandleKey(k));
            Assert.Empty(host); // the Alt pass-through still comes before the host's keys
        }
    }
}
