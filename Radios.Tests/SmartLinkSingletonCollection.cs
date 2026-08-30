using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Test classes that replace the process-wide
    /// <c>SmartLinkServices.Coordinator</c> singleton (via
    /// <c>SmartLinkServices.Override</c>) and exercise FlexBase's static
    /// presence-intake state.
    ///
    /// <para>xUnit runs test classes in parallel by default. Two classes each
    /// installing their own coordinator would trample each other's override
    /// mid-test — pushes land on the wrong class's mock and the failures point
    /// somewhere unrelated. Same disease, same cure as
    /// <see cref="RadioConfigStaticsCollection"/>: sharing a collection
    /// serialises them.</para>
    /// </summary>
    [CollectionDefinition(Name)]
    public sealed class SmartLinkSingletonCollection
    {
        public const string Name = "SmartLinkServices singleton";
    }
}
