using System;
using System.Diagnostics;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// WPF adapter implementing IFilterControl.
    /// Replaces Flex6300Filters (WinForms UserControl) as the live IFilterControl.
    /// Delegates pan adapter operations to PanAdapterManager.
    /// Sprint 11 Phase 11.2.
    /// </summary>
    public class WpfFilterAdapter : IFilterControl, IDisposable
    {
        private FlexBase rig;
        private PanAdapterManager panManager;

        /// <summary>
        /// Settable pan display callback — forwards braille text to MainWindow.
        /// Set by MainWindow.WirePanDisplay() during PowerNowOn.
        /// </summary>
        public Action<string, int>? PanDisplayCallback { get; set; }

        public WpfFilterAdapter(FlexBase rig)
        {
            this.rig = rig;
        }

        public void RXFreqChange(object o) => panManager?.RXFreqChange(o);

        public void PanSetup()
        {
            Tracing.TraceLine("WpfFilterAdapter.PanSetup", TraceLevel.Info);

            // Create PanAdapterManager — callback forwards to settable PanDisplayCallback
            string opsDir = rig.ConfigDirectory + '\\' + rig.OperatorName;
            panManager = new PanAdapterManager(rig, opsDir, rig.Callouts.BrailleCells,
                (line, pos) => PanDisplayCallback?.Invoke(line, pos));

            // Create WpfMemoryManager and wire into RigFields
            var memMgr = new WpfMemoryManager(rig);
            memMgr.Setup();

            // Set up RigFields_t with simplified constructor
            rig.RigFields = new FlexBase.RigFields_t(UpdateBoxes, memMgr);
        }

        public ulong ZeroBeatFreq() => panManager?.ZeroBeatFreq() ?? 0;

        public void OperatorChangeHandler() => panManager?.OperatorChangeHandler();

        public void Close()
        {
            Tracing.TraceLine("WpfFilterAdapter.Close", TraceLevel.Info);
            panManager?.Close();
        }

        /// <summary>
        /// Access the PanAdapterManager for external consumers (e.g. MainWindow).
        /// </summary>
        public PanAdapterManager PanManager { get { return panManager; } }

        private void UpdateBoxes()
        {
            // RigFields.RigUpdate callback — called every 100ms by poll timer
            // For WPF, this is where combo/number box updates would go
            // Initially empty; Track B wires the WPF controls
        }

        public void Dispose()
        {
            Close();
        }
    }
}
