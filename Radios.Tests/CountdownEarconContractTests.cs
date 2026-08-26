using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 35 Track G, #261. Pins the two countdown method names that
    /// another track calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because of a collision class git cannot see.</b> The
    /// countdown was split across two tracks on purpose — Track G builds the
    /// sound in EarconPlayer, Track A calls it from the transmit sequencing —
    /// and the two agreed the symbol names up front. Renaming one of these
    /// afterwards produces NO merge conflict: the definition moves in one
    /// file, the call sites stay in another, both branches merge cleanly, and
    /// the build breaks after the merge with nothing to point at what did it.
    /// </para>
    /// <para>
    /// The same shape cost a session on 2026-08-12, when one track was told to
    /// reuse a symbol and another track moved it. Both did the right thing,
    /// both merged with zero textual conflict, and the build then failed.
    /// </para>
    /// <para>
    /// So the agreement is written down somewhere that fails rather than
    /// somewhere that is read. If a name here genuinely needs to change, both
    /// tracks change together and this file changes with them — deliberately,
    /// which is the entire point.
    /// </para>
    /// <para>
    /// Signature, not just name: <c>public static void</c> with no parameters.
    /// A caller written against <c>CountdownRecordTone()</c> breaks just as
    /// hard if the method grows a required argument as if it were renamed.
    /// </para>
    /// </remarks>
    public sealed class CountdownEarconContractTests
    {
        private const string TargetFile = "JJFlexWpf/EarconPlayer.cs";

        /// <summary>
        /// The agreed names. Track A calls exactly these.
        /// </summary>
        public static TheoryData<string> ContractMethods() => new()
        {
            "CountdownRecordTone",
            "CountdownTransmitTone",
        };

        [Theory]
        [MemberData(nameof(ContractMethods))]
        public void CountdownMethodExistsWithTheAgreedSignature(string method)
        {
            string source = ReadTarget();

            var signature = new Regex(
                @"public\s+static\s+void\s+" + Regex.Escape(method) + @"\s*\(\s*\)",
                RegexOptions.Multiline);

            Assert.True(signature.IsMatch(source),
                "EarconPlayer." + method + "() is missing or its signature changed. This is a "
                + "CONTRACT with another track, which calls it by exactly this name and takes "
                + "no arguments. Renaming it produces no merge conflict and a broken build. If "
                + "the name really is wrong, change it in both tracks and here, together.");
        }

        /// <summary>
        /// The positive control. A regex that matches nothing would let both
        /// tests above pass on an empty file, so prove the pattern finds a
        /// signature that is definitely present and definitely not one of the
        /// two under test.
        /// </summary>
        [Fact]
        public void TheSignaturePatternActuallyMatchesSomething()
        {
            string source = ReadTarget();

            var known = new Regex(@"public\s+static\s+void\s+TxStartTone\s*\(\s*\)");
            Assert.True(known.IsMatch(source),
                "The signature pattern did not find TxStartTone(), which is certainly in this "
                + "file. The pattern is broken, so the contract checks above are verifying "
                + "nothing.");

            // And prove it discriminates: a name that is not there must not match.
            Assert.DoesNotMatch(@"public\s+static\s+void\s+NoSuchEarconTone\s*\(\s*\)", source);
        }

        /// <summary>
        /// Both countdowns must reach the Earcon Explorer, which discovers
        /// sounds by reflecting over the <c>[Earcon]</c> attribute. A method
        /// without one is playable by code and by nobody else — the exact gap
        /// #113 was written to close, where the most recognisable sound in the
        /// application could not be played on demand anywhere.
        /// </summary>
        [Theory]
        [MemberData(nameof(ContractMethods))]
        public void CountdownIsRegisteredForTheExplorer(string method)
        {
            string source = ReadTarget();

            int at = source.IndexOf("public static void " + method, StringComparison.Ordinal);
            Assert.True(at > 0, method + " not found; see the contract test above.");

            // The attribute sits immediately above the method, past its doc
            // comment. Look back a generous window rather than counting lines.
            int from = Math.Max(0, at - 4000);
            string preceding = source[from..at];

            int marker = preceding.LastIndexOf("[Earcon(", StringComparison.Ordinal);
            Assert.True(marker >= 0,
                method + " has no [Earcon] attribute, so it will not appear in the Earcon "
                + "Explorer and cannot be auditioned by an operator.");

            // Nothing else may declare a method between the attribute and this
            // one, or the attribute belongs to that other method instead.
            string between = preceding[marker..];
            Assert.False(between.Contains("public static void ", StringComparison.Ordinal),
                "The nearest [Earcon] attribute above " + method + " belongs to a different "
                + "method.");
        }

        private static string ReadTarget()
        {
            string path = Path.Combine(RepoRoot(), TargetFile.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + TargetFile + " (looked at " + path + "). A test that cannot "
                + "find its subject proves nothing about it.");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
