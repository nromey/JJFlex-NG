using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Test classes that must not run while the built app assembly is loaded
    /// into this process, and the one class that loads it.
    /// </summary>
    /// <remarks>
    /// <c>NewLogHeaderTests</c> loads the built app into a collectible load
    /// context to call the VB logger's real code. While it is loaded, and
    /// until its unload completes, its assemblies are visible to
    /// <c>AppDomain.GetAssemblies</c>, which the namespace-shadowing sweep in
    /// <c>IntegrationPassRuleTests</c> reads to learn the framework's names.
    /// That sweep failed once in three full runs after the loader was added
    /// (#814 repair); the failure did not recur and its message was not
    /// captured, so the cause is UNCONFIRMED. The likeliest explanation is an
    /// unload still pending from the earlier test, which the loader now forces
    /// to complete before it returns. It was NOT two classes running at once:
    /// <c>TestParallelism.cs</c> already disables parallel test execution for
    /// this assembly, so this collection changes no ordering today. It is kept
    /// so the pairing stays explicit if parallelism is ever turned back on.
    /// If the sweep fails again, capture its message and the loaded
    /// assemblies' names and load contexts.
    /// </remarks>
    [CollectionDefinition(Name)]
    public sealed class BuiltAppAssemblyCollection
    {
        public const string Name = "Built app assembly loaded";
    }
}
