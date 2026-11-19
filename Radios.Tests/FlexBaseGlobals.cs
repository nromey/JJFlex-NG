using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Flex.Smoothlake.FlexLib;
using Xunit.Sdk;

// #1160: every test in this assembly ends with FlexBase's three process-wide
// statics put back as they were when the assembly loaded. See FlexBaseGlobals.
[assembly: Radios.Tests.FlexBaseGlobalsRestored]

namespace Radios.Tests
{
    /// <summary>
    /// The three process-wide statics <see cref="FlexBase"/>'s constructor
    /// writes, captured and put back (#1160).
    /// </summary>
    /// <remarks>
    /// <para><b>Why.</b> The constructor sets <c>Vita.VitaSocket.TraceSink</c>,
    /// <c>API.ProgramName</c> and <c>API.IsGUI = true</c>, and nothing in the
    /// tests put them back. <c>API.IsGUI</c> changes how
    /// <c>Radio.ActiveSlice</c> decides which client owns a slice, so a test
    /// that ran after any test that built a rig ran under a different ownership
    /// policy from one that ran first: a hidden dependency on test order.</para>
    /// <para><b>The copy in JJFlexWpf.Tests</b>
    /// (<c>JJFlexWpf.Tests/Infrastructure/FlexBaseGlobals.cs</c>, Sprint 49)
    /// is a separate copy on purpose: the two test projects do not share
    /// source.</para>
    /// <para><b>Where it is applied.</b> Not test by test: about eighty sites in
    /// sixty files build a rig, and a site added tomorrow would be missed.
    /// <see cref="FlexBaseGlobalsRestored"/> is applied to the whole assembly,
    /// so every test ends with the statics as the assembly found them, and it
    /// records which tests changed them, which is the count by running.</para>
    /// </remarks>
    internal static class FlexBaseGlobals
    {
        /// <summary>The three values at one moment.</summary>
        internal readonly record struct Snapshot(Action<string>? TraceSink, string? ProgramName, bool IsGUI)
        {
            /// <summary>Put these values back.</summary>
            public void Restore()
            {
                Vita.VitaSocket.TraceSink = TraceSink;
                API.ProgramName = ProgramName;
                API.IsGUI = IsGUI;
            }

            /// <summary>Which of the three now differ from these values, by name. Empty when none do.</summary>
            public List<string> Differences()
            {
                var changed = new List<string>();
                if (!ReferenceEquals(Vita.VitaSocket.TraceSink, TraceSink)) changed.Add("Vita.VitaSocket.TraceSink");
                if (!string.Equals(API.ProgramName, ProgramName, StringComparison.Ordinal)) changed.Add("API.ProgramName");
                if (API.IsGUI != IsGUI) changed.Add("API.IsGUI");
                return changed;
            }
        }

        /// <summary>The three values now.</summary>
        internal static Snapshot Capture() =>
            new(Vita.VitaSocket.TraceSink, API.ProgramName, API.IsGUI);

        /// <summary>
        /// The values as the assembly found them, taken when it loaded and
        /// before any test (or any test class's constructor) could build a rig.
        /// </summary>
        internal static Snapshot Pristine { get; private set; }

        [ModuleInitializer]
        internal static void TakePristine() => Pristine = Capture();
    }

    /// <summary>
    /// Applied to the whole assembly: after each test, the three statics go
    /// back to <see cref="FlexBaseGlobals.Pristine"/>, and a test that left
    /// them changed is recorded in <see cref="Changers"/>.
    /// </summary>
    /// <remarks>
    /// Compared against the assembly's pristine values, not against a capture
    /// taken before the test: xUnit builds the test class (and runs its field
    /// initializers, where some tests build their rig) before this attribute's
    /// <see cref="Before"/> runs, so a before-capture would take that rig's
    /// values as the baseline and never put them back.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
    internal sealed class FlexBaseGlobalsRestored : BeforeAfterTestAttribute
    {
        private static readonly object Gate = new();
        private static readonly List<string> s_changers = new();

        /// <summary>The test now running, as this attribute saw it start; null between tests.</summary>
        internal static MethodInfo? Current { get; private set; }

        /// <summary>Every test, by class and method, that ended with a static changed, in run order.</summary>
        internal static IReadOnlyList<string> Changers
        {
            get { lock (Gate) return s_changers.ToArray(); }
        }

        public override void Before(MethodInfo methodUnderTest) => Current = methodUnderTest;

        public override void After(MethodInfo methodUnderTest)
        {
            var changed = FlexBaseGlobals.Pristine.Differences();
            if (changed.Count > 0)
            {
                lock (Gate)
                    s_changers.Add(methodUnderTest.DeclaringType?.Name + "." + methodUnderTest.Name + ": " + string.Join(", ", changed));
                FlexBaseGlobals.Pristine.Restore();
            }
            Current = null;
            Report();
        }

        /// <summary>
        /// Only for counting: with <c>JJFLEX_TEST_GLOBALS_REPORT</c> set to a
        /// file path, the list of tests that changed a static is written there
        /// after each test. Unset, nothing is written.
        /// </summary>
        private static void Report()
        {
            string? path = Environment.GetEnvironmentVariable("JJFLEX_TEST_GLOBALS_REPORT");
            if (string.IsNullOrWhiteSpace(path)) return;
            try { System.IO.File.WriteAllLines(path, Changers); } catch { }
        }
    }
}
