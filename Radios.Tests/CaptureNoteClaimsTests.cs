using System;
using System.Linq;
using JJTrace;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #625, repair round one: two notes claimed more than their writers
    /// guarantee. The transmit note said back is written only when both powers
    /// were measured "at the same moment"; the writer divides the latest two
    /// readings, each kept by its own callback. The opening said the reason a
    /// recording ended early is at the end of the file; a failed sink refuses
    /// that very line.
    /// </summary>
    public sealed class CaptureNoteClaimsTests
    {
        [Fact]
        public void The_back_field_claims_no_simultaneity_the_writer_does_not_check()
        {
            string intro = CaptureMeterSet.TxMetersRecord.Introduction;
            string back = intro.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith("back:", StringComparison.Ordinal));
            Assert.DoesNotContain("same moment", back, StringComparison.Ordinal);
            Assert.DoesNotContain("same second", back, StringComparison.Ordinal);
            Assert.Contains("latest forward and reflected readings", back, StringComparison.Ordinal);
            Assert.Contains("can have arrived at slightly different moments", back, StringComparison.Ordinal);

            // Positive control on the writer: back comes from ReflectedFraction,
            // the plain ratio of the two latest fields, not from the paired
            // ReadTransmitPower reading. If the writer is ever changed to pair
            // them, this fails and the note can say so again.
            string flexBase = FlexBasePartials.JoinedText();
            int at = flexBase.IndexOf("private void traceTxMeters()", StringComparison.Ordinal);
            Assert.True(at > 0, "traceTxMeters not found; rewrite this control, do not delete it");
            int end = flexBase.IndexOf("Tracing.TraceRecord(CaptureMeterSet.TxMetersRecord, \"txMeters: state=\"", at, StringComparison.Ordinal);
            Assert.True(end > at, "the txMeters writer no longer writes the line this note describes");
            string writer = flexBase.Substring(at, end - at);
            Assert.Contains("float back = ReflectedFraction;", writer, StringComparison.Ordinal);
            Assert.DoesNotContain("ReadTransmitPower", writer, StringComparison.Ordinal);
            Assert.Contains("TransmitSafety.ReflectedFractionOf(ForwardPowerWatts, ReflectedPowerWatts)", flexBase, StringComparison.Ordinal);
        }

        [Fact]
        public void The_opening_promises_the_ending_only_if_the_recording_could_write_it()
        {
            string opening = Assert.Single(TraceSelfDescription.PartPreamble());
            Assert.DoesNotContain("the reason it ended early", opening, StringComparison.Ordinal);
            Assert.EndsWith("If the recording was able to say how it ended, that is on its last lines.", opening, StringComparison.Ordinal);
        }
    }
}
