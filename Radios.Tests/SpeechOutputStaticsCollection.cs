using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Test classes that drive the process-wide speech statics —
    /// <c>ScreenReaderOutput</c>'s verbosity and coalescer state, and
    /// <c>OutputChannelRecorder</c>'s global configuration.
    ///
    /// <para>Same mechanism, same reason as
    /// <see cref="RadioConfigStaticsCollection"/>: xUnit runs test classes in
    /// parallel, and two classes each calling
    /// <c>OutputChannelRecorder.Configure</c> would repoint the one global
    /// transcript out from under each other mid-test.
    /// OutputChannelRecorderTests carried this constraint the way
    /// KnownRadioRosterTests once did — "all tests live in this one class" — a
    /// rule that holds only until somebody adds a second class. Sprint 35
    /// Track M added one, so the comment became a mechanism.</para>
    ///
    /// <para>Moved into its own file on 2026-08-27 (#285), when
    /// SpeechCoalescerTimingTests was ported to the injected clock and stopped
    /// touching the statics. A collection definition living inside a file whose
    /// own tests no longer join it is the kind of thing that gets deleted by
    /// someone tidying up, taking every other class's serialisation with
    /// it.</para>
    /// </summary>
    [CollectionDefinition(Name)]
    public sealed class SpeechOutputStaticsCollection
    {
        public const string Name = "Speech output statics";
    }
}
