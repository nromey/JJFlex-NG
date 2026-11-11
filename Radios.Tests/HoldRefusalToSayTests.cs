#nullable enable
using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The hold's refusal for a caller that says it itself, later (#1043 F2,
    /// F5): the Fix tool after its page re-renders, a menu row after the menu
    /// closes. One source with <c>GuardRefuses</c>: the same trace and the same
    /// sentence, and nothing spoken here.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class HoldRefusalToSayTests : IDisposable
    {
        private readonly FlexBase _rig = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        public void Dispose() => _rig.Dispose();

        private static string Sentence(string action) =>
            Lexicon.Get("settings.guard.refused", ("action", Lexicon.Get(action)));

        [Fact]
        public void With_the_hold_off_there_is_no_refusal()
        {
            Assert.False(_rig.ChangeNothingActive);
            Assert.Null(_rig.HoldRefusalToSay("settings.guard.action.profile_load"));
        }

        [Fact]
        public void With_the_hold_on_the_holds_own_sentence_is_returned()
        {
            // That nothing is SPOKEN is held from source below: this test
            // process has no speech backend, so speech leaves no record here
            // to compare.
            _rig.SetChangeNothingActive(true);
            Assert.Equal(Sentence("settings.guard.action.profile_load"), _rig.HoldRefusalToSay("settings.guard.action.profile_load"));
        }

        [Fact]
        public void With_this_radios_speech_suppressed_it_still_refuses_but_has_nothing_to_say()
        {
            _rig.SetChangeNothingActive(true);
            _rig.SuppressSpeech = true;
            Assert.Equal("", _rig.HoldRefusalToSay("settings.guard.action.full_duplex"));
        }

        [Fact]
        public void The_spoken_refusal_and_the_returned_one_share_one_source()
        {
            string src = Source("Radios", "FlexBase.cs");
            string say = Between(src, "private void SayTheHoldRefused(string actionKey)", "\n        }");
            Assert.Contains("TheHoldRefused(actionKey)", say, StringComparison.Ordinal);
            Assert.DoesNotContain("settings.guard.refused", say, StringComparison.Ordinal);
            string returned = Between(src, "public string HoldRefusalToSay(string actionKey)", ";");
            Assert.Contains("TheHoldRefused(actionKey)", returned, StringComparison.Ordinal);
            Assert.Contains("ChangeNothingActive", returned, StringComparison.Ordinal);
            // Neither the returned refusal nor its source speaks: the caller does.
            string traced = Between(src, "private string TheHoldRefused(string actionKey)", "\n        }");
            foreach (string part in new[] { returned, traced })
            {
                Assert.DoesNotContain("Speak(", part, StringComparison.Ordinal);
                Assert.DoesNotContain("GuardRefuses(", part, StringComparison.Ordinal);
                Assert.DoesNotContain("SayTheHoldRefused(", part, StringComparison.Ordinal);
            }
            Assert.Contains("ChangeNothing: refused ", traced, StringComparison.Ordinal);
        }

        private static string Between(string text, string start, string end)
        {
            int at = text.IndexOf(start, StringComparison.Ordinal);
            Assert.True(at >= 0, "not found: " + start);
            int stop = text.IndexOf(end, at + start.Length, StringComparison.Ordinal);
            Assert.True(stop > at, "no end after: " + start);
            return text.Substring(at, stop - at);
        }

        private static string Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
        }
    }
}
