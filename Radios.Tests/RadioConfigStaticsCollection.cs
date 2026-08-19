using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Test classes that write the process-wide statics
    /// <c>RadioConfig.BaseDirectory</c> and <c>KnownRadioRoster.CacheDirectory</c>.
    ///
    /// <para>xUnit runs test CLASSES in parallel by default, so two classes
    /// each pointing those statics at their own temp directory will trample
    /// each other — one class's store vanishes mid-test and its assertions
    /// fail somewhere unrelated to the change that "broke" them. Sharing a
    /// collection serialises them.</para>
    ///
    /// <para>KnownRadioRosterTests carried this constraint as a comment
    /// ("one class, not several") and a rule that only holds while nobody adds
    /// a second class. Sprint 30 Track A added one, hit exactly the predicted
    /// failure, and turned the comment into a mechanism.</para>
    /// </summary>
    [CollectionDefinition(Name)]
    public sealed class RadioConfigStaticsCollection
    {
        public const string Name = "RadioConfig statics";
    }
}
