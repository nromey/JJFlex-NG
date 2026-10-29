using POpusCodec;
using POpusCodec.Enums;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #807: libopus hands back plain 8-bit C strings, and the wrapper used to
/// read them with Marshal.PtrToStringAuto, which on Windows reads them as
/// UTF-16 and returns garbage. Both readers in OpusWrapper.cs are covered:
/// the version string (OpusEncoder.Version / OpusDecoder.Version) and
/// opus_strerror (the message of every OpusException).
///
/// These load the real libopus.dll from runtimes\win-x64\native through the
/// same once-only resolver as RecordedQsoOggTests, so they run on x64.
/// </summary>
public class OpusStringMarshallingTests
{
    [Fact]
    public void The_encoder_and_decoder_report_the_same_version_string_as_OpusInfo()
    {
        RecordedQsoOggTests.OpusLibraryReady();
        string expected = OpusInfo.VersionString();
        Assert.StartsWith("libopus ", expected);

        using var encoder = new OpusEncoder(SamplingRate.Sampling48000, Channels.Mono);
        using var decoder = new OpusDecoder(SamplingRate.Sampling48000, Channels.Mono);
        Assert.Equal(expected, encoder.Version);
        Assert.Equal(expected, decoder.Version);
    }

    [Fact]
    public void An_opus_error_carries_libopus_own_readable_message()
    {
        RecordedQsoOggTests.OpusLibraryReady();
        using var encoder = new OpusEncoder(SamplingRate.Sampling48000, Channels.Mono);

        // Complexity runs 0 to 10; libopus answers anything else with
        // OPUS_BAD_ARG, whose opus_strerror text is "invalid argument".
        var ex = Assert.Throws<OpusException>(() => encoder.Complexity = (Complexity)42);
        Assert.Equal(OpusStatusCode.BadArguments, ex.StatusCode);
        Assert.Equal("invalid argument", ex.Message);
    }
}
