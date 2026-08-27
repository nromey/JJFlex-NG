using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A <c>Radios.X</c> namespace silently shadows <c>System.X</c> for every VB
    /// file that imports <c>Radios</c>. This refuses the collision at the point
    /// of naming, rather than in a file nobody touched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What happened (task #141, 2026-08-19).</b> Sprint 32 Track C named its
    /// new namespace <c>Radios.Diagnostics</c>. The Radios project compiled. The
    /// solution build then failed with four errors in <c>PersonalData.vb</c> — a
    /// file the track had never opened — because <c>Diagnostics.TraceLevel</c>
    /// had been resolving to <c>System.Diagnostics</c> and now resolved into
    /// Radios instead. Three properties made it expensive: the failure is remote
    /// from the cause, the C# side compiles clean so a project-level build looks
    /// fine, and <c>Diagnostics</c> is exactly the word anybody would pick.
    /// </para>
    /// <para>
    /// <b>Why a test and not a note.</b> A note in <c>Radios.csproj</c> already
    /// exists and is good, and it only helps a reader who is editing that file
    /// at that moment. Whoever adds the next subsystem writes
    /// <c>namespace Radios.Threading</c> in a new .cs file and never opens the
    /// project file at all.
    /// </para>
    /// <para>
    /// <b>The rule has no live violations today, and that is the point.</b>
    /// Measured 2026-08-27: the second-level names under <c>Radios</c> are
    /// ChainChecks, DiscoveryChain, Fixer, Properties, SmartLink and Speech; of
    /// those only <c>Speech</c> matches a real <c>System</c> child, and
    /// <c>System.Speech</c> is not referenced by the VB app — it appears only in
    /// <c>tools/refvoice</c>, a separate C# tool. No VB file uses any of these
    /// six names unqualified. So this test was added while green, deliberately:
    /// a guard installed after the next incident would have cost the incident.
    /// </para>
    /// <para>
    /// <b>Reflection rather than a list of framework names.</b> A hand-written
    /// list of <c>System</c> children is one more description that goes stale,
    /// which is the defect class this suite exists to hunt. Deriving the list
    /// from assembly FILE NAMES was tried and rejected: it under-counts, and
    /// silently. There is no <c>System.Timers.dll</c>, no
    /// <c>System.Media.dll</c> and no <c>System.Speech.dll</c> in the shared
    /// framework on this machine, yet all three namespaces are real. An
    /// under-count here is a false negative, and a false negative is the whole
    /// failure mode.
    /// </para>
    /// </remarks>
    public class RadiosNamespaceShadowingTests
    {
        private static readonly Regex NamespaceDecl =
            new(@"(?m)^\s*namespace\s+(Radios(?:\.[A-Za-z0-9_]+)+)\s*[;{]?", RegexOptions.Compiled);

        private static readonly Regex VbImport =
            new(@"<Import\s+Include\s*=\s*""([^""]+)""", RegexOptions.Compiled);

        /// <summary>
        /// Namespaces the VB app carries implicitly, from JJFlexRadio.vbproj.
        /// Read from the project file rather than restated here, so the two
        /// cannot drift apart.
        /// </summary>
        private static IReadOnlyList<string> ProjectLevelVbImports()
        {
            string vbproj = IntegrationPassTree.At("JJFlexRadio.vbproj");
            Assert.True(File.Exists(vbproj), "JJFlexRadio.vbproj is not in the tree.");

            var roots = VbImport.Matches(File.ReadAllText(vbproj))
                                .Select(m => m.Groups[1].Value.Trim())
                                .Where(s => s.Length > 0)
                                .Distinct(StringComparer.Ordinal)
                                .ToList();

            // POSITIVE CONTROL on the reader. An empty or tiny list would make
            // this test find nothing and pass, which reads exactly like "no
            // collisions" — the result it is supposed to be able to deny.
            Assert.True(roots.Count >= 8,
                "only " + roots.Count + " project-level VB imports were read out of "
                + "JJFlexRadio.vbproj, so either the <Import> block has been gutted or this "
                + "reader has stopped recognising it. Either way the shadowing check would "
                + "report clean having compared almost nothing.");
            Assert.Contains("System", roots);
            Assert.Contains("System.Diagnostics", roots);

            return roots;
        }

        /// <summary>
        /// Second-level names declared under <c>Radios</c> in the source tree.
        /// </summary>
        private static IReadOnlyList<string> RadiosChildNamespaces()
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);

            // Committed source AND source git has not been told about yet. The
            // second half is the whole point: a new namespace arrives in a new
            // file, and the first run of this test proved the omission — adding
            // `namespace Radios.Timers` and running reported clean. See
            // IntegrationPassTree.UntrackedSource for why that list is separate
            // from every other sweep's corpus.
            foreach (string file in IntegrationPassTree.AuthoredSource.Concat(IntegrationPassTree.UntrackedSource()))
            {
                if (IntegrationPassTree.IsTest(file)) continue;   // Radios.Tests is not shipped
                foreach (Match m in NamespaceDecl.Matches(IntegrationPassTree.Read(file)))
                    names.Add(m.Groups[1].Value.Split('.')[1]);
            }

            Assert.True(names.Count > 0,
                "no Radios.<X> namespace was found anywhere in the tree, which cannot be true — "
                + "the declaration matcher has broken and this check is comparing nothing.");
            return names.ToList();
        }

        /// <summary>
        /// Every namespace reachable from the loaded framework, exact.
        /// </summary>
        /// <remarks>
        /// Built from the host's own trusted-platform-assembly list rather than
        /// from a directory listing, so it describes what this build actually
        /// references. Assemblies that refuse to load or to enumerate are
        /// skipped — a native or resource-only file in the list is normal, and
        /// the floor assertion below is what catches a probe that has degraded
        /// into skipping everything.
        /// </remarks>
        private static IReadOnlySet<string> EveryFrameworkNamespace()
        {
            var namespaces = new HashSet<string>(StringComparer.Ordinal);

            var paths = new List<string>();
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
                paths.AddRange(tpa.Split(Path.PathSeparator).Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));

            foreach (string path in paths)
            {
                Assembly asm;
                try { asm = Assembly.Load(AssemblyName.GetAssemblyName(path)); }
                catch { continue; }

                Type[] types;
                try { types = asm.GetExportedTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                catch { continue; }

                foreach (Type t in types)
                {
                    string? ns = t.Namespace;
                    while (!string.IsNullOrEmpty(ns))
                    {
                        if (!namespaces.Add(ns)) break;   // ancestors already added
                        int cut = ns.LastIndexOf('.');
                        ns = cut < 0 ? null : ns.Substring(0, cut);
                    }
                }
            }

            // POSITIVE CONTROL, and it is load-bearing. System.Timers and
            // System.Media have no assembly named after them, so they are
            // exactly the entries a cheaper file-name probe would have missed.
            //
            // System.Speech is DELIBERATELY NOT IN THIS LIST, and the first run
            // of this test is why: the control failed on it. The probe sees the
            // namespaces THIS BUILD REFERENCES, and nothing here references the
            // System.Speech package — which is precisely why Radios.Speech does
            // not collide today. That scope is the right one: the question is
            // whether a name collides in this build, not whether it exists
            // somewhere on NuGet. The other test in this file guards the day
            // that changes.
            foreach (string known in new[]
                     { "System.Diagnostics", "System.Threading", "System.IO", "System.Text",
                       "System.Timers", "System.Media", "Microsoft.VisualBasic" })
            {
                Assert.True(namespaces.Contains(known),
                    "the framework probe did not find " + known + ", which certainly exists. It has "
                    + "degraded, and a degraded probe reports every name as safe.");
            }
            Assert.DoesNotContain("System.ChainChecks", namespaces);

            return namespaces;
        }

        [Fact]
        public void No_Radios_namespace_shadows_one_the_VB_app_already_imports()
        {
            IReadOnlyList<string> imports = ProjectLevelVbImports();
            IReadOnlyList<string> children = RadiosChildNamespaces();
            IReadOnlySet<string> framework = EveryFrameworkNamespace();

            var collisions = new List<string>();

            foreach (string child in children)
            {
                foreach (string root in imports)
                {
                    string candidate = root + "." + child;
                    if (!framework.Contains(candidate)) continue;

                    collisions.Add(
                        "Radios." + child + " shadows " + candidate + ". Every VB file with "
                        + "`Imports Radios` resolves a bare `" + child + ".Something` into Radios "
                        + "from now on, and `" + root + "` is imported project-wide by "
                        + "JJFlexRadio.vbproj, so the breakage lands in files nobody touched and "
                        + "the C# build still passes. Pick another word — Track C renamed "
                        + "Radios.Diagnostics to Radios.ChainChecks for exactly this.");
                }
            }

            Assert.True(collisions.Count == 0,
                "A Radios.<X> namespace collides with a namespace the VB app imports:"
                + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ", collisions));
        }

        /// <summary>
        /// The untracked half of the corpus can actually see an untracked file.
        /// </summary>
        /// <remarks>
        /// Without this, an untracked reader that silently returns nothing is
        /// indistinguishable from a clean tree — and that is not hypothetical:
        /// the first two versions of this guard did exactly that and reported
        /// green against a deliberate <c>namespace Radios.Timers</c>. The file
        /// is written at the repository ROOT rather than under Radios\, so no
        /// project's compile glob picks it up if a build runs alongside.
        /// </remarks>
        [Fact]
        public void The_untracked_source_reader_can_see_an_untracked_file()
        {
            string probe = Path.Combine(IntegrationPassTree.Root, "IntegrationPassUntrackedProbe.cs");
            Assert.False(File.Exists(probe), "the probe file is left over from an interrupted run: " + probe);

            try
            {
                File.WriteAllText(probe, "// untracked probe" + Environment.NewLine);
                Assert.Contains(IntegrationPassTree.UntrackedSource(), f =>
                    string.Equals(Path.GetFileName(f), "IntegrationPassUntrackedProbe.cs", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                if (File.Exists(probe)) File.Delete(probe);
            }

            Assert.DoesNotContain(IntegrationPassTree.UntrackedSource(), f =>
                string.Equals(Path.GetFileName(f), "IntegrationPassUntrackedProbe.cs", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The vbproj's <c>System.Speech</c> near-miss, kept honest.
        /// </summary>
        /// <remarks>
        /// <c>Radios.Speech</c> would collide the moment the VB app referenced
        /// System.Speech, and it is a speech application, so somebody will
        /// reasonably try. Today the package is referenced only by
        /// <c>tools/refvoice</c>. This states that out loud so the day it stops
        /// being true, a test says so rather than PersonalData.vb.
        /// </remarks>
        [Fact]
        public void The_VB_app_does_not_reference_System_Speech_while_Radios_Speech_exists()
        {
            if (!RadiosChildNamespaces().Contains("Speech")) return;   // renamed; nothing to guard

            string vbproj = File.ReadAllText(IntegrationPassTree.At("JJFlexRadio.vbproj"));

            Assert.False(
                vbproj.Contains("System.Speech", StringComparison.OrdinalIgnoreCase),
                "JJFlexRadio.vbproj now references System.Speech while a Radios.Speech namespace "
                + "exists. Every VB file with `Imports Radios` will resolve a bare `Speech.` into "
                + "Radios instead, and nothing will warn. Rename Radios.Speech, or reference "
                + "System.Speech only from a project that does not import Radios — which is how "
                + "tools/refvoice does it.");
        }
    }
}
