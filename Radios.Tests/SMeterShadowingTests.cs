using System;
using System.Linq;
using System.Reflection;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The S-meter reaches its readers through ONE field and ONE property
    /// (#295). Structural, by reflection, because the defect it guards is
    /// structural and produced no failure of any kind at run time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What went wrong.</b> <c>FlexBase</c> declared its own
    /// <c>_SMeter</c> field and its own <c>SMeter</c> property, neither marked
    /// <c>new</c> nor <c>override</c>, so both SHADOWED the ones in
    /// <c>AllRadios</c>. The meter handler filled the shadow. Anything reading
    /// through the base class — <c>AllRadios.RawSMeter</c>, which is the band
    /// panner's signal-strength column — read the base field, which nothing on
    /// a Flex ever wrote. It was permanently zero on every radio this
    /// application supports, and a column of zeros looks exactly like data.
    /// </para>
    /// <para>
    /// <b>Why nothing caught it.</b> Shadowing produces compiler warnings
    /// CS0108 and CS0114, and <c>.editorconfig</c> demotes both to
    /// <c>suggestion</c>, which never reaches build output. Re-arming them for
    /// one sweep reported 86 hits, every one of them in <c>FlexBase</c>: the
    /// class shadows the base's whole surface, so this is one member of a
    /// pattern, not an isolated slip. Fixing all 86 is its own task; this test
    /// nails down the one with a proven-wrong reader.
    /// </para>
    /// </remarks>
    public sealed class SMeterShadowingTests
    {
        private const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly;

        [Fact]
        public void TheInstrumentFindsAFieldItIsMeantToFind()
        {
            // Positive control. A reflection test that finds nothing is
            // indistinguishable from a reflection test that cannot see, so
            // prove the lookup works on a field known to exist before
            // trusting it to report that another one does not.
            Assert.NotNull(typeof(AllRadios).GetField("_SMeter", Declared));
            Assert.NotNull(typeof(FlexBase).GetField("_PowerDBM", Declared));
        }

        [Fact]
        public void FlexBaseDeclaresNoSMeterFieldOfItsOwn()
        {
            FieldInfo? shadow = typeof(FlexBase).GetField("_SMeter", Declared);
            Assert.True(shadow == null,
                "FlexBase has re-declared _SMeter. That shadows AllRadios._SMeter, "
              + "the meter handler will fill the shadow, and AllRadios.RawSMeter "
              + "goes back to reading a field nothing writes — a permanent zero "
              + "that reads as a measurement (#295).");
        }

        [Fact]
        public void FlexBaseOverridesSMeterRatherThanHidingIt()
        {
            PropertyInfo? p = typeof(FlexBase).GetProperty("SMeter", Declared);
            Assert.NotNull(p);

            MethodInfo getter = p!.GetMethod!;
            Assert.True(getter.IsVirtual,
                "FlexBase.SMeter must be an override, not a new declaration.");
            Assert.Equal(typeof(AllRadios),
                getter.GetBaseDefinition().DeclaringType);
        }

        [Fact]
        public void RawSMeterReadsTheFieldTheMeterHandlerWrites()
        {
            // The two must resolve to the same field. Reading through the base
            // type is precisely what was broken, so the assertion is made from
            // the base type's own metadata.
            PropertyInfo raw = typeof(AllRadios).GetProperty("RawSMeter")!;
            Assert.Equal(typeof(int), raw.PropertyType);
            Assert.Null(raw.SetMethod);

            // And there is exactly one field with this name in the hierarchy.
            int declarations = 0;
            for (Type? t = typeof(FlexBase); t != null && t != typeof(object); t = t.BaseType)
                if (t.GetField("_SMeter", Declared) != null) declarations++;
            Assert.Equal(1, declarations);
        }

        [Fact]
        public void TheDuplicateSMeterRawAccessorIsGone()
        {
            // FlexBase carried SMeterRaw — a second name for RawSMeter on the
            // same object, with no callers, built beside the base property
            // precisely BECAUSE the base property always answered zero. With
            // the shadow gone there is one name; two would invite the next
            // author to pick the wrong one.
            Assert.Null(typeof(FlexBase).GetProperty("SMeterRaw", Declared));
        }
    }
}
