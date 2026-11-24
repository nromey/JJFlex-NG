using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The account UPnP hook is retired (the tiers plan, decision 2, ruled by
    /// Noel 2026-11-19): JJ Flexible makes, renews and releases no router
    /// mapping, and never removes one. <c>UPnPPortMapper</c> is kept, unused.
    /// </summary>
    public sealed class AccountUPnPHookRetiredTests
    {
        private static string[] ProductSources() =>
            new[] { "Radios", "JJFlexWpf" }
                .SelectMany(d => Directory.GetFiles(Path.Combine(IntegrationPassTree.Root, d), "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(IntegrationPassTree.Root, "*.vb", SearchOption.TopDirectoryOnly))
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                         && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .ToArray();

        [Fact]
        public void Nothing_in_the_product_constructs_the_mapper_or_maps_a_port()
        {
            foreach (var file in ProductSources())
            {
                if (file.EndsWith("UPnPPortMapper.cs", StringComparison.OrdinalIgnoreCase)) continue;
                string text = File.ReadAllText(file);
                Assert.False(text.Contains("new UPnPPortMapper(", StringComparison.Ordinal), file + " constructs UPnPPortMapper");
                Assert.False(text.Contains("TryAddMapping(", StringComparison.Ordinal), file + " adds a router mapping");
                Assert.False(text.Contains("TryRemoveMapping(", StringComparison.Ordinal), file + " removes a router mapping");
            }
        }

        [Fact]
        public void The_hook_and_its_helpers_are_gone()
        {
            string flexBase = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "Radios", "FlexBase.cs"));
            Assert.DoesNotContain("void ApplyAccountUPnPPreferenceIfAny(", flexBase);
            Assert.DoesNotContain("void ReleaseUPnPMappingsIfAny(", flexBase);
            Assert.DoesNotContain("bool IsPrivateIPv4(", flexBase);
        }
    }
}
