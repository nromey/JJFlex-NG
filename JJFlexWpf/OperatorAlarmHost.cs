#nullable enable
using System;
using System.Diagnostics;
using JJTrace;
using Radios;
using Radios.Alarms;

namespace JJFlexWpf
{
    /// <summary>
    /// Where the operator-alarm subsystem (#566) lives for the life of a rig:
    /// the feed over the rig, the service, and the delivery through the
    /// warning tone and Critical-plus-Urgent speech — alive whether or not
    /// the dialog is open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attached beside <see cref="MeterToneEngine.AttachToRadio"/>, on the
    /// same rig object, which lives across connect and disconnect cycles;
    /// connection generations come from the rig's own events through the
    /// feed. Detaching disposes everything in dependency order.
    /// </para>
    /// <para>
    /// The dialog reaches the service through <see cref="Service"/> and
    /// never constructs one: a window is a view of the running subsystem,
    /// and closing it leaves monitoring running.
    /// </para>
    /// </remarks>
    public static class OperatorAlarmHost
    {
        private static FlexBaseAlarmFeed? _feed;

        public static AlarmService? Service { get; private set; }
        public static AlarmDelivery? Delivery { get; private set; }
        public static OperatorPresetStore? Presets { get; private set; }

        public static void AttachToRadio(FlexBase rig)
        {
            if (rig == null) return;
            Detach();
            try
            {
                _feed = new FlexBaseAlarmFeed(rig);
                Service = new AlarmService(_feed, AlarmDefinitionStore.Default(), new SystemAlarmClock(), Recorder());
                Delivery = AlarmDelivery.Attach(Service, new ScreenReaderAlarmSpeaker(),
                    EarconPlayer.WarningAlarmTone, () => EarconPlayer.IsOn(EarconPlayer.EarconCategory.Warnings));
                Presets = OperatorPresetStore.Default();
                Tracing.TraceLine("OperatorAlarmHost: attached", TraceLevel.Info);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("OperatorAlarmHost: could not attach — " + ex.Message, TraceLevel.Error);
                Detach();
            }
        }

        /// <summary>The journal the service records into. The null recorder until the journal is wired.</summary>
        private static IAlarmObservationRecorder Recorder() => NullAlarmObservationRecorder.Instance;

        public static void Detach()
        {
            try { Delivery?.Dispose(); } catch { }
            try { Service?.Dispose(); } catch { }
            try { _feed?.Dispose(); } catch { }
            Delivery = null;
            Service = null;
            _feed = null;
        }
    }
}
