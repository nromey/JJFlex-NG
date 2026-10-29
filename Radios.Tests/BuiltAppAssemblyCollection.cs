using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Test classes that must not run while the built app assembly is loaded
    /// into this process, and the one class that loads it.
    /// </summary>
    /// <remarks>
    /// <c>NewLogHeaderTests</c> loads the built app into a collectible load
    /// context to call the VB logger's real code. While it is loaded, its
    /// assemblies are visible to <c>AppDomain.GetAssemblies</c>, which the
    /// namespace-shadowing sweep in <c>IntegrationPassRuleTests</c> reads to
    /// learn the framework's names; the two running at once failed that sweep
    /// once in three full runs (#814 repair). Sharing a collection serialises
    /// them, and the loader unloads before it returns.
    /// </remarks>
    [CollectionDefinition(Name)]
    public sealed class BuiltAppAssemblyCollection
    {
        public const string Name = "Built app assembly loaded";
    }
}
