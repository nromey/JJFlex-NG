using System;
using System.IO;
using JJFlex.TxFactAudit;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #937: TxFactAudit's default trace folder follows JJFLEX_CONFIG_DIR, in
    /// jjprobe's order, so an isolated run's readings come from the isolated
    /// run's traces rather than the operator's live ones.
    /// </summary>
    /// <remarks>
    /// Every case drives the pure resolver. None sets the process environment:
    /// the live property is only read, never steered.
    /// </remarks>
    public sealed class TxFactAuditTraceDirectoryTests
    {
        private const string Standard = @"C:\Users\op\AppData\Roaming\JJFlexRadio";

        [Fact]
        public void An_isolated_run_reads_the_throwaway_tree()
        {
            const string temp = @"D:\scratch\jjflex-run-7";
            Assert.Equal(temp, TraceSessions.ResolveDefaultDirectory(temp, Standard));
        }

        [Fact]
        public void The_variable_is_trimmed_and_made_full()
        {
            Assert.Equal(@"D:\scratch\run", TraceSessions.ResolveDefaultDirectory(@"  D:\scratch\x\..\run  ", Standard));
        }

        [Fact]
        public void An_ordinary_run_reads_the_operators_own_folder()
        {
            // Positive control: with no variable, the default is what it always was.
            Assert.Equal(Standard, TraceSessions.ResolveDefaultDirectory(null, Standard));
            Assert.Equal(Standard, TraceSessions.ResolveDefaultDirectory("", Standard));
            Assert.Equal(Standard, TraceSessions.ResolveDefaultDirectory("   ", Standard));
        }

        [Fact]
        public void A_relative_path_is_refused_with_a_message_and_falls_through()
        {
            var warnings = new StringWriter();
            Assert.Equal(Standard, TraceSessions.ResolveDefaultDirectory(@"scratch\run", Standard, warnings));

            string said = warnings.ToString();
            Assert.Contains(TraceSessions.ConfigDirVariable, said);
            Assert.Contains("relative path", said);
            Assert.Contains(Standard, said);
        }

        [Fact]
        public void The_live_default_is_the_rule_applied_to_this_process()
        {
            // Read only: the property must be the resolver over the real
            // variable and the real standard folder, nothing else.
            string? variable = Environment.GetEnvironmentVariable(TraceSessions.ConfigDirVariable);
            Assert.Equal(TraceSessions.ResolveDefaultDirectory(variable, TraceSessions.StandardDirectory),
                         TraceSessions.DefaultDirectory);
            Assert.Equal("JJFLEX_CONFIG_DIR", TraceSessions.ConfigDirVariable);
        }
    }
}
