using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #914: jjprobe finds the app's trace under a relocated settings tree.
    /// The tool has no test project of its own and is not referenced here, so
    /// this reads its source: the trace folder must come through the resolver
    /// that consults --config-dir and JJFLEX_CONFIG_DIR, and Program must hand
    /// it the option.
    /// </summary>
    public sealed class JjprobeConfigDirTests
    {
        private static string Read(string name) =>
            File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "tools", "uia-probe", name));

        [Fact]
        public void The_trace_folder_follows_the_config_dir_option_and_the_environment()
        {
            string observe = Read("Observe.cs");
            int at = observe.IndexOf("public static string AppDataDir =>", StringComparison.Ordinal);
            Assert.True(at > 0, "TraceLog.AppDataDir was renamed; rewrite this guard, do not delete it");
            string body = observe.Substring(at, observe.IndexOf(';', at) - at);

            Assert.Contains("ResolveAppDataDir(ConfigDirOption", body, StringComparison.Ordinal);
            Assert.Contains("GetEnvironmentVariable(ConfigDirVariable)", body, StringComparison.Ordinal);
            Assert.Contains("public const string ConfigDirVariable = \"" + RadioConfig.ConfigDirOverrideVariable + "\"",
                            observe, StringComparison.Ordinal);

            string program = Read("Program.cs");
            Assert.Contains("TraceLog.ConfigDirOption = opt.Str(\"config-dir\")", program, StringComparison.Ordinal);
        }
    }
}
