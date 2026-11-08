using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track E repair round 1, B2: first refusal.
    /// <see cref="ValueSubLayer.OfferFirst"/> runs a key as
    /// <see cref="ValueSubLayer.HandleKey(Keys)"/> does up to the point where
    /// every key the layer binds has been tried, and hands anything else back
    /// untouched. The contrast is the point: asked with HandleKey, a layer
    /// REFUSES a bracket it does not bind (the in-place refusal, #778b), which
    /// on Home would swallow the filter keys. The host side is
    /// <c>JJFlexWpf.Tests.NoiseLayerRouteTests</c>.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ValueLayerFirstRefusalTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(ValueLayerFirstRefusalTests));
        public ValueLayerFirstRefusalTests() => Lexicon.Load(Lexicon.Partitions);
        public void Dispose() => _scope.Dispose();

        private static ValueSubLayer Open(List<Keys> took, List<string> said)
        {
            var def = new ValueSubLayerDefinition
            {
                Id = "noise",
                Selection = ValueLayerSelection.ByLetter,
                Targets = new List<ValueTarget> { new() { Id = "x", Read = () => 0, Apply = _ => { }, Number = _ => "" } },
                InitialTarget = -1,
                DescribeClosed = () => "closed",
                // The noise layer's own bracket keys, and nothing else.
                HostKeys = k => { if (k != Keys.OemCloseBrackets && k != Keys.OemOpenBrackets) return false; took.Add(k); return true; },
                // As the shipped host answers on Home: a bracket acts in place
                // elsewhere but nothing here would act on it, so it is refused.
                PassThroughKeys = _ => false,
                DescribeUnavailableHere = k => (k & Keys.KeyCode) is Keys.OemOpenBrackets or Keys.OemCloseBrackets
                    ? "You can't use that filter key here." : null,
            };
            return ValueSubLayer.EnterForTest(def, (_, _) => { }, (_, s) => said.Add(s), s => said.Add(s), () => VerbosityLevel.Chatty);
        }

        [Fact]
        public void First_refusal_takes_a_key_the_layer_binds()
        {
            var took = new List<Keys>(); var said = new List<string>();
            var layer = Open(took, said);
            Assert.Equal(ValueLayerKeyResult.Handled, layer.OfferFirst(Keys.OemCloseBrackets));
            Assert.Equal(ValueLayerKeyResult.Handled, layer.OfferFirst(Keys.OemOpenBrackets));
            Assert.Equal(new[] { Keys.OemCloseBrackets, Keys.OemOpenBrackets }, took);
            Assert.True(layer.IsLive);
        }

        [Fact]
        public void First_refusal_hands_back_a_key_the_layer_does_not_bind_where_HandleKey_would_refuse_it()
        {
            var took = new List<Keys>(); var said = new List<string>();
            var layer = Open(took, said);
            int before = said.Count;
            foreach (var k in new[] { Keys.OemOpenBrackets | Keys.Shift, Keys.OemCloseBrackets | Keys.Control, Keys.Q })
            {
                Assert.Equal(ValueLayerKeyResult.PassThrough, layer.OfferFirst(k));
                Assert.True(layer.IsLive);
            }
            Assert.Equal(before, said.Count);   // nothing refused, nothing said
            Assert.Empty(took);

            // The contrast: the same bracket asked the ordinary way is refused
            // and consumed, which is why Home must not simply ask HandleKey first.
            Assert.Equal(ValueLayerKeyResult.Handled, layer.HandleKey(Keys.OemOpenBrackets | Keys.Shift));
            Assert.Contains("You can't use that filter key here.", said);
        }
    }
}
