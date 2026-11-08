using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track E, open fact 6: is the noise layer's gated closing
    /// sentence (its DescribeLayerHelp) reachable from H at all? The engine
    /// is asked here exactly as the shipped layer wires it: H plays the cue,
    /// asks ListCommands, and falls back to the spoken help only when that
    /// answers false.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class LayerHelpReachabilityTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(LayerHelpReachabilityTests));
        public LayerHelpReachabilityTests() => Lexicon.Load(Lexicon.Partitions);
        public void Dispose() => _scope.Dispose();

        private static (ValueSubLayer Layer, List<string> Said) Open(Func<bool>? listCommands, Action onHelpAsked)
        {
            var said = new List<string>();
            var def = new ValueSubLayerDefinition
            {
                Id = "noise",
                Selection = ValueLayerSelection.ByLetter,
                Targets = new List<ValueTarget> { new() { Id = "x", Read = () => 0, Apply = _ => { }, Number = _ => "" } },
                InitialTarget = -1,
                DescribeClosed = () => "closed",
                DescribeLayerHelp = layer => { onHelpAsked(); return "the gated sentence"; },
                ListCommands = listCommands,
            };
            var layer = ValueSubLayer.EnterForTest(def, (t, _) => said.Add(t), (t, _) => said.Add(t), t => said.Add(t), () => VerbosityLevel.Chatty);
            return (layer, said);
        }

        [Fact]
        public void With_the_shipped_list_surface_H_never_reaches_the_gated_sentence()
        {
            // The shipped noise layer's ListCommands always answers true
            // (KeyCommands.TryShowLayerCommandList), so this is production.
            int asked = 0;
            var (layer, said) = Open(() => true, () => asked++);
            Assert.Equal(ValueLayerKeyResult.Handled, layer.HandleKey(Keys.H));
            Assert.Equal(0, asked);
            Assert.DoesNotContain("the gated sentence", said);
        }

        [Fact]
        public void Positive_control_without_a_list_surface_H_speaks_the_gated_sentence()
        {
            int asked = 0;
            var (layer, said) = Open(null, () => asked++);
            layer.HandleKey(Keys.H);
            Assert.Equal(1, asked);
            Assert.Contains(said, s => s.Contains("the gated sentence"));
        }
    }
}
