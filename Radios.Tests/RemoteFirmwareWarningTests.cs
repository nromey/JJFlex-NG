#nullable enable

using System;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The warning spoken when remote firmware updates are allowed for a radio.
    /// The old one was eighty words and its delivery died around word forty-two,
    /// so the half that said "leave this off" was never heard at the radio. The
    /// approved replacement leads with the refusal and is short enough to arrive
    /// whole; these tests keep it that way.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RemoteFirmwareWarningTests
    {
        private const string Key = "settings.profile.remote_firmware_allowed_warning";

        [Fact]
        public void The_warning_leads_with_the_refusal()
        {
            string text = Lexicon.Get(Key);
            Assert.StartsWith("Leave this off unless it is your only option", text, StringComparison.Ordinal);
        }

        [Fact]
        public void The_warning_is_short_enough_to_be_heard_whole()
        {
            // The old text was eighty words and was cut off around its
            // forty-second. The approved text is forty-three, and it carries the
            // refusal first, so a cut near the end loses only "Press Apply or OK".
            // This catches the warning growing back, not a word or two.
            int words = Lexicon.Get(Key)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Length;
            Assert.True(words <= 45, $"The warning is {words} words; the old one was 80 and died around word 42.");
        }

        [Fact]
        public void The_warning_still_says_how_to_keep_the_choice()
        {
            Assert.EndsWith("Press Apply or OK to keep it.", Lexicon.Get(Key), StringComparison.Ordinal);
        }
    }
}
