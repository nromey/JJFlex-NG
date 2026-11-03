using System;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// A deliberate disconnect stops PC audio, and so puts back the mic input and
/// the shack speaker, while the connection still answers (#861, #600).
/// </summary>
/// <remarks>
/// <para>
/// Until this, <c>Disconnect()</c> set <c>PCAudio = false</c> only after
/// <c>theRadio.Disconnect()</c> and the wait for the link to fall. The mic
/// input's put-back then went into a closed connection, where FlexLib's send
/// returns without sending, and traced success; and the shack speaker's unmute,
/// which refuses while <c>Disconnecting</c> is set, never ran.
/// </para>
/// <para>
/// A SOURCE-ORDER test, on purpose: <c>Disconnect()</c> speaks and needs a
/// live audio thread to exercise, so what is pinned is the order of the three
/// statements that decide the outcome. The put-backs themselves (only what
/// still matches) are each one condition on the radio's reported state, read
/// in <see cref="The_put_backs_write_only_what_still_matches"/>.
/// </para>
/// </remarks>
public sealed class DisconnectPutBackOrderTests
{
    private static string DisconnectBody()
    {
        string text = FlexBasePartials.JoinedText();
        int start = text.IndexOf("public void Disconnect()", StringComparison.Ordinal);
        Assert.True(start > 0, "FlexBase.Disconnect() moved");
        int end = text.IndexOf("public const int SkFarewellFallbackMs", start, StringComparison.Ordinal);
        Assert.True(end > start, "the end of Disconnect() could not be found");
        return text.Substring(start, end - start);
    }

    [Fact]
    public void PC_audio_stops_before_the_disconnect_flag_and_before_the_hang_up()
    {
        string body = DisconnectBody();

        int stop = body.IndexOf("PCAudio = false;", StringComparison.Ordinal);
        int flag = body.IndexOf("Disconnecting = true;", StringComparison.Ordinal);
        int hangUp = body.IndexOf("theRadio.Disconnect();", StringComparison.Ordinal);
        int profiles = body.IndexOf("PutProfilesBackOnDisconnect();", StringComparison.Ordinal);

        Assert.True(stop > 0 && flag > 0 && hangUp > 0 && profiles > 0, "a statement this test orders has moved");
        Assert.True(stop < flag,
            "PC audio stops after Disconnecting is set, so the speaker's unmute refuses and the room keeps a dead speaker");
        Assert.True(stop < hangUp,
            "PC audio stops after the hang-up, so the mic input is put back into a closed connection");
        Assert.True(profiles < stop, "the profile put-back should keep the last word, so it runs first");
    }

    [Fact]
    public void A_stop_after_the_hang_up_says_it_cannot_reach_the_radio()
    {
        string body = DisconnectBody();
        int hangUp = body.IndexOf("theRadio.Disconnect();", StringComparison.Ordinal);
        int late = body.IndexOf("PCAudio = false;", hangUp, StringComparison.Ordinal);
        if (late < 0) return;   // no backstop at all is also honest
        string around = body.Substring(Math.Max(0, late - 400), 400);
        Assert.Contains("nothing it puts back can reach the radio", around, StringComparison.Ordinal);
    }

    [Fact]
    public void The_put_backs_write_only_what_still_matches()
    {
        string text = FlexBasePartials.JoinedText();

        // The mic input: only while the radio still reports the PC we chose.
        int putBack = text.IndexOf("private void PutBackNow(Radio radio, MicInputSwap swap, string when)", StringComparison.Ordinal);
        Assert.True(putBack > 0, "PutBackNow moved");
        string mic = text.Substring(putBack, 700);
        Assert.Contains("if (string.Equals(nowInput, \"PC\", StringComparison.OrdinalIgnoreCase))", mic, StringComparison.Ordinal);

        // The shack speaker: only while the radio still mutes it.
        int write = text.IndexOf("IsMuteLocalAudioWhenRemoteOn=false on remote audio stop", StringComparison.Ordinal);
        Assert.True(write > 0);
        string speaker = text.Substring(Math.Max(0, write - 300), 300);
        Assert.Contains("if (!speakerRadio.IsMuteLocalAudioWhenRemoteOn)", speaker, StringComparison.Ordinal);
    }
}
