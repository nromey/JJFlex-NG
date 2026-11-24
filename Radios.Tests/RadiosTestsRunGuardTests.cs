using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Pins the two guards in this project's own file that stop a run from
    /// passing having run nothing. An x86 run once built an x64 test DLL against
    /// an x86 Radios.dll; xUnit skipped the whole assembly and the command exited
    /// 0, so an "x86 Radios.Tests green" counted nothing. The proof that each guard
    /// works is the command itself (an x86 run goes red); these pins catch either
    /// guard being dropped. They read the project as XML, so a guard that has been
    /// commented out no longer counts as present.
    /// </summary>
    public sealed class RadiosTestsRunGuardTests
    {
        private static XDocument Project()
        {
            string path = Path.Combine(FieldKeyMapScan.RepoRoot(), "Radios.Tests", "Radios.Tests.csproj");
            Assert.True(File.Exists(path), "Radios.Tests.csproj not found at " + path);
            return XDocument.Load(path);
        }

        [Fact]
        public void A_run_that_discovers_no_tests_is_treated_as_an_error()
        {
            var settings = Project().Descendants()
                .Where(e => e.Name.LocalName == "VSTestCLIRunSettings")
                .Select(e => e.Value.Replace(" ", ""))
                .ToList();

            Assert.Contains(settings, s => s
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Any(part => string.Equals(part, "RunConfiguration.TreatNoTestsAsError=true", StringComparison.OrdinalIgnoreCase)));
        }

        [Fact]
        public void An_x86_run_is_refused_before_anything_builds()
        {
            var refusals = Project().Descendants()
                .Where(e => e.Name.LocalName == "Target")
                .Where(t => ((string?)t.Attribute("Condition") ?? "").Replace(" ", "") == "'$(Platform)'=='x86'")
                .Where(t => !string.IsNullOrEmpty((string?)t.Attribute("BeforeTargets")))
                .Where(t => t.Elements().Any(e => e.Name.LocalName == "Error"
                    && ((string?)e.Attribute("Text") ?? "").Contains("-p:Platform=x64", StringComparison.Ordinal)))
                .ToList();

            Assert.Single(refusals);
        }
    }
}
