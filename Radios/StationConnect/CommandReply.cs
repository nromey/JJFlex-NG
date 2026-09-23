using System;

namespace Radios.StationConnect
{
    /// <summary>
    /// The radio's reply to ONE command we sent: the independent
    /// acknowledgment that FlexLib's setters throw away.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists</b> (Track G2 re-review, section 5; memory
    /// <c>project_flexlib_suppresses_equal_value_success</c>). Every FlexLib
    /// setter assigns its cache first, sends the command, and raises
    /// <c>PropertyChanged</c> synchronously as a local echo. The radio's
    /// success reply is discarded (<c>Slice.SetFreqReply</c> returns on
    /// <c>resp_val == 0</c>; the profile setters use <c>SendCommand</c>
    /// with no handler at all), and the status message that follows carries
    /// the value the cache already holds, so the vendor's equal-value skip
    /// suppresses it. Once our own echo is filtered as a local echo — which
    /// it must be — <b>no ordinary success reaches a confirmation through
    /// PropertyChanged.</b></para>
    /// <para>The reply IS available: <c>Radio.SendReplyCommand(ReplyHandler,
    /// string)</c> is public, and <c>Radio.ParseReply</c> routes the radio's
    /// <c>R&lt;seq&gt;|&lt;hex code&gt;|&lt;text&gt;</c> to the handler by
    /// sequence number. The production port sends its confirmable commands
    /// through that path itself, bypassing the setter, so (a) the radio's
    /// acknowledgment arrives here, and (b) the vendor cache is not
    /// pre-assigned, so the radio's own status for a CHANGED value differs
    /// from the cache and is raised as a genuine radio report. The equal-
    /// value case (the slice was already there; the profile was already
    /// selected) produces no status at all and the acknowledgment alone is
    /// the confirmation.</para>
    /// </remarks>
    public sealed class CommandReply
    {
        public CommandReply(string command, uint code, string text)
        {
            Command = command ?? "";
            Code = code;
            Text = text ?? "";
        }

        /// <summary>The command text as sent, without its sequence prefix.</summary>
        public string Command { get; }

        /// <summary>The radio's hexadecimal response code; 0 is success.</summary>
        public uint Code { get; }

        /// <summary>The reply's message text, if any. On a rejected slice
        /// tune it carries the frequency the radio actually kept.</summary>
        public string Text { get; }

        /// <summary>True when the radio answered with code 0.</summary>
        public bool Acknowledged => Code == 0;

        public static CommandReply Ok(string command) => new CommandReply(command, 0, "");

        public override string ToString() =>
            (Acknowledged ? "acknowledged" : "rejected 0x" + Code.ToString("X"))
            + " '" + Command + "'" + (string.IsNullOrEmpty(Text) ? "" : " (" + Text + ")");
    }
}
