using System;
using System.IO;
using System.Xml.Serialization;

namespace Radios
{
    /// <summary>
    /// Double-tap timing tolerance for input interactions that use double-press
    /// semantics (filter-edge bracket double-tap, Escape-collapse-all, any future
    /// double-tap consumer).
    ///
    /// Sprint 28 Phase 1 primitive. Enum values are the millisecond counts
    /// themselves — casting to int gives the tolerance directly, no translation
    /// table required. Adding a future step is a one-line enum change; nothing
    /// else needs to touch.
    /// </summary>
    public enum DoubleTapTolerance
    {
        /// <summary>250 ms — for fast typists; close to the pre-Sprint-28 hardcoded behavior.</summary>
        Quick = 250,

        /// <summary>500 ms — the recommended default. Balanced feel for most users.</summary>
        Normal = 500,

        /// <summary>750 ms — for deliberate input cadence.</summary>
        Relaxed = 750,

        /// <summary>1000 ms — slowest; suits users who verify speech between each press.</summary>
        Leisurely = 1000
    }

    /// <summary>
    /// Which way the Slice field's Up and Down arrows walk the slice letters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Noticed by Noel 2026-08-11 (#318):</b> the slice list navigated
    /// bottom-to-top — you arrowed UP to go A, B, C — so Down-arrow walked
    /// backwards through the alphabet. Reading order says Down should do that,
    /// and a list is the one control where a screen-reader operator has no
    /// visual layout to correct the mismatch against.
    /// </para>
    /// <para>
    /// <b>His ruling the same day was to make it a setting, not to flip it.</b>
    /// We pick the better default; the operator keeps the choice. Anyone who
    /// has spent months with the old direction in their fingers keeps it with
    /// one radio button, and nobody's muscle memory is confiscated by our
    /// opinion about reading order.
    /// </para>
    /// </remarks>
    public enum SliceArrowOrder
    {
        /// <summary>Down-arrow moves A to B to C. Reading order, and the
        /// default.</summary>
        TopToBottom = 0,

        /// <summary>Up-arrow moves A to B to C — how the field behaved before
        /// this setting existed.</summary>
        BottomToTop = 1
    }

    /// <summary>
    /// Per-operator accessibility preferences — input timing, UI pacing, and other
    /// accessibility-tuning knobs that may accumulate in future sprints (Sprint 28 ships
    /// this with just DoubleTapTolerance; future phases may add hold-to-repeat delay,
    /// announcement timing defaults, etc.).
    ///
    /// Follows the PttConfig persistence pattern: XML-serialized per-operator file at
    /// <c>{BaseConfigDir}\{opName}_accessibilityConfig.xml</c>. Loaded at app start
    /// (ApplicationEvents.vb), saved when user commits Settings dialog changes.
    ///
    /// A static <see cref="Current"/> reference exposes the active config to any
    /// consumer that doesn't want to thread the object through its call chain — useful
    /// for UI-layer key handlers that read DoubleTapToleranceMs at unpredictable times.
    /// </summary>
    [XmlRoot("AccessibilityConfig")]
    public class AccessibilityConfig
    {
        /// <summary>
        /// The active double-tap tolerance. Default <see cref="DoubleTapTolerance.Normal"/>
        /// (500 ms) — chosen as a reasonable midpoint for screen-reader users whose input
        /// cadence is often slower than sighted typists but who also don't want unwieldy
        /// delays.
        /// </summary>
        public DoubleTapTolerance DoubleTapTolerance { get; set; } = DoubleTapTolerance.Normal;

        /// <summary>
        /// The tolerance expressed as an integer millisecond count. Convenience accessor
        /// so callers that don't need the enum value (most of them) don't have to cast.
        /// Not serialized — derived from <see cref="DoubleTapTolerance"/>.
        /// </summary>
        [XmlIgnore]
        public int DoubleTapToleranceMs => (int)DoubleTapTolerance;

        /// <summary>
        /// Which way the Slice field's arrows walk the letters. Default
        /// <see cref="Radios.SliceArrowOrder.TopToBottom"/> — Down goes A, B, C.
        /// </summary>
        public SliceArrowOrder SliceArrowOrder { get; set; } = SliceArrowOrder.TopToBottom;

        /// <summary>
        /// The step Down-arrow should pass to the slice cycler: +1 toward the
        /// next letter, -1 toward the previous one. Up-arrow takes the
        /// negation.
        /// </summary>
        /// <remarks>
        /// A property rather than a conditional at each key handler, because
        /// there are two Up/Down slice sites in the app — the Slice field and
        /// the Slice Operations field — and a setting honoured by one of them
        /// is worse than no setting at all: it would be lying about what it
        /// controls. Derived, so it is never serialised and can never disagree
        /// with <see cref="SliceArrowOrder"/>.
        /// </remarks>
        [XmlIgnore]
        public int SliceStepForDownArrow =>
            SliceArrowOrder == SliceArrowOrder.TopToBottom ? 1 : -1;

        /// <summary>
        /// The active (most recently loaded or saved) accessibility config. UI-layer
        /// consumers that don't take an AccessibilityConfig parameter (e.g., double-tap
        /// detectors in FreqOutHandlers or ScreenFieldsPanel) read this. Updated by
        /// <see cref="Load"/> and <see cref="Save"/>; starts at defaults until the first
        /// Load call.
        /// </summary>
        public static AccessibilityConfig Current { get; private set; } = new AccessibilityConfig();

        public static AccessibilityConfig Load(string configDirectory, string operatorName)
        {
            var filePath = GetFilePath(configDirectory, operatorName);

            if (!File.Exists(filePath))
            {
                Current = CreateDefault();
                return Current;
            }

            try
            {
                using var fs = File.OpenRead(filePath);
                var serializer = new XmlSerializer(typeof(AccessibilityConfig));
                var config = (AccessibilityConfig?)serializer.Deserialize(fs);
                if (config == null)
                {
                    Current = CreateDefault();
                    return Current;
                }
                config.Validate();
                Current = config;
                return config;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"AccessibilityConfig.Load failed: {ex.Message}");
                Current = CreateDefault();
                return Current;
            }
        }

        public void Save(string configDirectory, string operatorName)
        {
            Validate();
            var filePath = GetFilePath(configDirectory, operatorName);

            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using var fs = File.Create(filePath);
                var serializer = new XmlSerializer(typeof(AccessibilityConfig));
                serializer.Serialize(fs, this);

                Current = this;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"AccessibilityConfig.Save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Validate and clamp out-of-range values. Called before serialization and after
        /// deserialization to defend against manually-edited config files.
        /// </summary>
        public void Validate()
        {
            if (!Enum.IsDefined(typeof(DoubleTapTolerance), DoubleTapTolerance))
                DoubleTapTolerance = DoubleTapTolerance.Normal;

            if (!Enum.IsDefined(typeof(SliceArrowOrder), SliceArrowOrder))
                SliceArrowOrder = SliceArrowOrder.TopToBottom;
        }

        private static AccessibilityConfig CreateDefault()
        {
            var config = new AccessibilityConfig();
            config.Validate();
            return config;
        }

        private static string GetFilePath(string configDirectory, string operatorName)
        {
            return Path.Combine(configDirectory, $"{operatorName}_accessibilityConfig.xml");
        }
    }
}
