using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #759: the disconnect offer to save the station says which gate closed
    /// whenever it declines, and it is on by default.
    /// </summary>
    /// <remarks>
    /// Until #759 four of the five declined branches were silent, and the one
    /// that traced was the one that passed on the radio where the prompt did
    /// not appear - so the record could not say why. The reason is held on
    /// <c>LastStationSaveOfferDecline</c> with the same words as the trace
    /// line, which is what lets this test read it without capturing a trace.
    /// In the statics collection because <c>OfferStationSaveOnDisconnect</c>
    /// is process-wide.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class StationSaveOfferDeclineTests
    {
        [Fact]
        public void The_offer_defaults_on_before_any_configuration_is_read()
        {
            // The static's own initializer, read through a fresh default rather
            // than the live static another test may have set.
            var property = typeof(FlexBase).GetProperty(nameof(FlexBase.OfferStationSaveOnDisconnect))!;
            Assert.True(property.GetMethod!.IsStatic);
            string source = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "Radios/FlexBase.Profiles.cs"));
            Assert.Contains("public static bool OfferStationSaveOnDisconnect { get; set; } = true;", source);
        }

        [Fact]
        public void Each_declined_gate_names_itself()
        {
            bool saved = FlexBase.OfferStationSaveOnDisconnect;
            FlexBase? rig = null;
            try
            {
                rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

                FlexBase.OfferStationSaveOnDisconnect = false;
                Assert.False(rig.ShouldOfferStationLayoutSave());
                Assert.Contains("switched off", rig.LastStationSaveOfferDecline);

                FlexBase.OfferStationSaveOnDisconnect = true;
                Assert.False(rig.ShouldOfferStationLayoutSave());
                Assert.Contains("nothing on the station changed", rig.LastStationSaveOfferDecline);

                typeof(FlexBase).GetProperty(nameof(FlexBase.OperatorChangedStationThisSession))!
                    .GetSetMethod(nonPublic: true)!.Invoke(rig, new object[] { true });
                Assert.False(rig.ShouldOfferStationLayoutSave());
                Assert.StartsWith("the save is blocked: ", rig.LastStationSaveOfferDecline);
                Assert.EndsWith(rig.StationLayoutSaveBlocker(), rig.LastStationSaveOfferDecline);
            }
            finally
            {
                FlexBase.OfferStationSaveOnDisconnect = saved;
                rig?.Dispose();
            }
        }

        /// <summary>
        /// The class, not the instance: no branch of the method may return
        /// false without going through the one helper that traces it.
        /// </summary>
        [Fact]
        public void No_branch_declines_without_its_trace()
        {
            string source = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "Radios/FlexBase.Profiles.cs"));
            int at = source.IndexOf("public bool ShouldOfferStationLayoutSave()", StringComparison.Ordinal);
            Assert.True(at >= 0);
            int next = source.IndexOf("\n        }", at, StringComparison.Ordinal);
            string body = source.Substring(at, next - at);

            // Exactly one bare "return false" - the one inside Decline itself,
            // after its trace line. Any other is a gate that closes in silence.
            Assert.Single(Regex.Matches(body, @"return\s+false\s*;"));
            Assert.True(Regex.Matches(body, @"return Decline\(").Count >= 5,
                "every gate in ShouldOfferStationLayoutSave declines through Decline(), which traces");

            // And Decline does trace: without this line every gate above
            // would close in silence again while the counts stayed green.
            int decline = body.IndexOf("bool Decline(string why)", StringComparison.Ordinal);
            Assert.True(decline >= 0);
            string helper = body.Substring(decline, body.IndexOf("return false", decline, StringComparison.Ordinal) - decline);
            Assert.Contains("Tracing.TraceLine(\"ShouldOfferStationLayoutSave:not offering, \" + why", helper);
        }

        /// <summary>
        /// The disconnect question carries the ruled stop-asking box, and its
        /// answer is acted on before the disconnect goes on.
        /// </summary>
        [Fact]
        public void The_disconnect_question_offers_the_stop_asking_box_and_honours_it()
        {
            string menu = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf/NativeMenuBar.cs"));
            int at = menu.IndexOf("private void OfferStationSaveBeforeDisconnect(", StringComparison.Ordinal);
            Assert.True(at >= 0);
            string body = menu.Substring(at, menu.IndexOf("\n    }", at, StringComparison.Ordinal) - at);

            Assert.Contains("stopAskingLabel:", body);
            Assert.Contains("disconnect_stop_asking_label", body);
            int chosen = body.IndexOf("if (confirm.StopAskingChosen)", StringComparison.Ordinal);
            int answered = body.IndexOf("if (answer != true) return;", StringComparison.Ordinal);
            Assert.True(chosen > 0 && answered > chosen,
                "stop-asking must be recorded before a No returns, or it would only hold on Yes");
            Assert.Contains("AudioOutputConfig.RecordStationSaveOfferTurnedOff(", body);
        }
    }
}
