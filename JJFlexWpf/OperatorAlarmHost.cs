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
    /// the feed over the rig, the service, the delivery through the warning
    /// tone and Critical-plus-Urgent speech, and the journal — alive whether
    /// or not the dialog is open.
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
    /// <para>
    /// <b>The capture link.</b> A detailed capture starting or stopping is
    /// the one state change the diagnostics plumbing announces, through
    /// <see cref="DiagnosticsBridge.StateChanged"/>; the host reads the
    /// capture flag on each and tells the journal, which links its running
    /// segment and writes the pre-roll rather than starting a second logger.
    /// </para>
    /// </remarks>
    public static class OperatorAlarmHost
    {
        private static FlexBaseAlarmFeed? _feed;
        private static AlarmJournal? _journal;
        private static FlexBase? _rig;
        private static bool _captureWasOn;

        public static AlarmService? Service { get; private set; }
        public static AlarmDelivery? Delivery { get; private set; }
        public static OperatorPresetStore? Presets { get; private set; }

        /// <summary>The journal, for the Diagnostics readout of rates, bytes and drops.</summary>
        public static AlarmJournal? Journal => _journal;

        public static void AttachToRadio(FlexBase rig)
        {
            if (rig == null) return;
            Detach();
            try
            {
                _feed = new FlexBaseAlarmFeed(rig);
                _journal = AlarmJournal.Default();
                Service = new AlarmService(_feed, AlarmDefinitionStore.Default(), new SystemAlarmClock(),
                    (IAlarmObservationRecorder?)_journal ?? NullAlarmObservationRecorder.Instance);
                Delivery = AlarmDelivery.Attach(Service, new ScreenReaderAlarmSpeaker(),
                    EarconPlayer.WarningAlarmTone, () => EarconPlayer.IsOn(EarconPlayer.EarconCategory.Warnings));
                if (_journal != null) Delivery.Reported += _journal.RecordDelivery;
                Presets = OperatorPresetStore.Default();

                // THE ONE SELECTION (#566, folded 2026-10-01): the service's
                // recorded set is also what the diagnostic capture records for
                // temperature and supply voltage. Pushed to the rig now and on
                // every change, so CaptureMeterSet has no list of its own.
                _rig = rig;
                Service.Changed += PushCaptureSelection;
                PushCaptureSelection();

                _captureWasOn = DiagnosticsBridge.IsCapturing?.Invoke() ?? false;
                DiagnosticsBridge.StateChanged += OnDiagnosticsStateChanged;
                Tracing.TraceLine("OperatorAlarmHost: attached" + (_journal == null ? " (no journal: settings root unresolved)" : ""),
                    TraceLevel.Info);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("OperatorAlarmHost: could not attach — " + ex.Message, TraceLevel.Error);
                Detach();
            }
        }

        /// <summary>The recorded set, as meter names, to the rig's capture writer. Cheap; the service already built the list.</summary>
        private static void PushCaptureSelection()
        {
            AlarmService? service = Service;
            FlexBase? rig = _rig;
            if (service == null || rig == null) return;
            try
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (MeterDescriptor d in service.RecordedMetersResolved) names.Add(d.Name);
                rig.SetCaptureSelection(names);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("OperatorAlarmHost: could not push the capture selection — " + ex.Message, TraceLevel.Warning);
            }
        }

        private static void OnDiagnosticsStateChanged(object? sender, EventArgs e)
        {
            bool on = DiagnosticsBridge.IsCapturing?.Invoke() ?? false;
            if (on == _captureWasOn) return;
            _captureWasOn = on;
            try
            {
                if (on) _journal?.CaptureStarted(DiagnosticsBridge.LiveLogPath?.Invoke() ?? "");
                else _journal?.CaptureStopped();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("OperatorAlarmHost: capture link failed — " + ex.Message, TraceLevel.Warning);
            }
        }

        public static void Detach()
        {
            DiagnosticsBridge.StateChanged -= OnDiagnosticsStateChanged;
            if (Service != null) Service.Changed -= PushCaptureSelection;
            _rig = null;
            try { Delivery?.Dispose(); } catch { }
            try { Service?.Dispose(); } catch { }
            try { _journal?.Dispose(); } catch { }
            try { _feed?.Dispose(); } catch { }
            Delivery = null;
            Service = null;
            _journal = null;
            _feed = null;
        }
    }
}
