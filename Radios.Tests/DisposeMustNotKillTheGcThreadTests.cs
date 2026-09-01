using System;
using System.Collections.Generic;
using System.Reflection;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Task #423: <c>FlexBase.Dispose</c> and the finalizer could throw a
    /// <see cref="NullReferenceException"/> on the GC thread.
    ///
    /// <para><b>The measured case.</b> With <c>theRadio</c> non-null and
    /// <c>Callouts.Profiles</c> null, disposal walked
    /// <c>saveNewGlobalProfile</c> into <c>GetDefaultProfiles</c>, which
    /// enumerated the null list. On the finalizer thread that is an unhandled
    /// exception, and <b>it took down the whole test host</b> when it fired.
    /// 2098 tests had never seen it because no prior test had attached a radio
    /// to a FlexBase — the #418 tests then worked around it, detaching their
    /// prop radio in a finally and handing <c>MakeRig</c> an empty operator
    /// list rather than a null one, which left the suite green and the defect
    /// untouched underneath.</para>
    ///
    /// <para><b>Why the shape matters more than the line.</b> A null list and
    /// an empty list are different states, and code that treats them as one is
    /// this project's most productive source of bugs. Here the confusion sat in
    /// a FINALIZER, where the punishment is process death with no window, no
    /// speech and a stack belonging to the collector rather than to whatever
    /// caused it — unrecoverable and unattributable at once.</para>
    /// </summary>
    public sealed class DisposeMustNotKillTheGcThreadTests
    {
        /// <summary>
        /// A rig whose operator profile list is genuinely absent — the state
        /// the #418 tests had to avoid creating.
        /// </summary>
        private static FlexBase RigWithNoProfileList()
            => new FlexBase(new FlexBase.OpenParms
            {
                ProgramName = "JJFlexTests",
                Profiles = null,
            });

        // ------------------------------------------------------------------
        // The null-list confusion, at its source
        // ------------------------------------------------------------------

        /// <summary>
        /// No profile list means no default profiles — the same answer an empty
        /// list gives. What was wrong was arriving there through an NRE.
        /// </summary>
        [Fact]
        public void AnAbsentProfileListReportsNoDefaultsInsteadOfThrowing()
        {
            var rig = RigWithNoProfileList();
            try
            {
                Assert.Null(rig.Callouts.Profiles);
                Assert.Empty(rig.GetDefaultProfiles());
            }
            finally
            {
                rig.theRadio = null;
                GC.SuppressFinalize(rig);
            }
        }

        /// <summary>
        /// The positive control. An empty result means nothing unless the same
        /// call returns something when there IS something — otherwise the test
        /// above would pass just as well against a method that always returns
        /// an empty list.
        /// </summary>
        [Fact]
        public void ARealDefaultProfileIsStillFound()
        {
            var rig = RigWithNoProfileList();
            try
            {
                rig.Callouts.Profiles = new List<Profile_t>
                {
                    new Profile_t("TheDefault", ProfileTypes.global, true),
                    new Profile_t("NotDefault", ProfileTypes.global, false),
                };

                var defaults = rig.GetDefaultProfiles();

                Assert.Single(defaults);
                Assert.Equal("TheDefault", defaults[0].Name);
            }
            finally
            {
                rig.theRadio = null;
                GC.SuppressFinalize(rig);
            }
        }

        // ------------------------------------------------------------------
        // Disposal, with the exact state that killed the test host
        // ------------------------------------------------------------------

        /// <summary>
        /// The reproduction: a radio attached, no profile list, disposed. This
        /// is the call that used to throw.
        /// </summary>
        [Fact]
        public void DisposingARigWithARadioAndNoProfileListDoesNotThrow()
        {
            var rig = RigWithNoProfileList();
            rig.theRadio = OfflineRadio();

            // No try/finally around this on purpose — the whole point is that
            // Dispose itself is now safe to call in this state.
            rig.Dispose();
        }

        /// <summary>
        /// **Nothing may leave the finalizer**, whatever it is. The finalizer is
        /// invoked directly here rather than waited for, so the test is a
        /// statement about the guard and not about when the collector feels
        /// like running.
        ///
        /// <para>A subclass that throws unconditionally stands in for every
        /// future fault: the guard has to hold for the failure nobody predicted,
        /// which is the one that killed the host.</para>
        /// </summary>
        [Fact]
        public void TheFinalizerContainsAnythingDisposeThrows()
        {
            var rig = new AlwaysThrowsOnDispose();
            GC.SuppressFinalize(rig); // we invoke it by hand, once

            MethodInfo finalizer = typeof(FlexBase).GetMethod(
                "Finalize", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(finalizer != null,
                "FlexBase has no finalizer any more. If that is deliberate this test should "
                + "go; if it is not, #423's whole hazard is back.");

            // Reflection wraps anything that escapes in TargetInvocationException,
            // so an escaping exception fails here loudly rather than silently.
            finalizer!.Invoke(rig, null);
        }

        /// <summary>
        /// The positive control for the test above: the subclass really does
        /// throw, so "the finalizer did not throw" is a statement about the
        /// guard rather than about a stand-in that never fired.
        /// </summary>
        [Fact]
        public void TheStandInReallyThrows()
        {
            var rig = new AlwaysThrowsOnDispose();
            GC.SuppressFinalize(rig);

            Assert.Throws<InvalidOperationException>(() => rig.ThrowFromDispose());
        }

        private sealed class AlwaysThrowsOnDispose : FlexBase
        {
            public AlwaysThrowsOnDispose()
                : base(new OpenParms { ProgramName = "JJFlexTests", Profiles = null })
            {
            }

            protected override void Dispose(bool disposing)
                => throw new InvalidOperationException("stand-in teardown fault (#423)");

            /// <summary>Reach the override without going through the guard.</summary>
            public void ThrowFromDispose() => Dispose(true);
        }

        /// <summary>
        /// A FlexLib Radio with no network behind it — the same offline prop the
        /// #418 tests use. Its internal constructor initializes lists and
        /// sub-objects only; nothing connects until Connect(), which nothing
        /// here calls.
        /// </summary>
        private static Flex.Smoothlake.FlexLib.Radio OfflineRadio()
        {
            var radio = (Flex.Smoothlake.FlexLib.Radio)Activator.CreateInstance(
                typeof(Flex.Smoothlake.FlexLib.Radio),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { false },
                culture: null);
            Assert.NotNull(radio);
            return radio;
        }
    }
}
