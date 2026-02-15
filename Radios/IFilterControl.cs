namespace Radios
{
    /// <summary>
    /// Interface for rig filter/DSP control operations.
    ///
    /// Sprint 10 Phase 10.2: Extracted from Flex6300Filters to decouple FlexBase.cs
    /// from the concrete WinForms type. FlexBase calls these 5 methods on FilterObj;
    /// any implementation (WinForms Flex6300Filters, future WPF adapter) must provide them.
    ///
    /// Methods:
    /// - RXFreqChange(object): called when active slice frequency/mode changes,
    ///   or when copying a panadapter. Argument is Slice or List&lt;Slice&gt;.
    /// - PanSetup(): called once when the radio is ready for pan adapter display.
    /// - ZeroBeatFreq(): returns the zero-beat frequency from waterfall data.
    /// - OperatorChangeHandler(): called when the operator profile changes.
    /// - Close(): detaches event handlers during shutdown.
    /// </summary>
    public interface IFilterControl
    {
        /// <summary>
        /// Handle a frequency or mode change on the active slice, or a pan copy.
        /// Argument is a Flex.Smoothlake.FlexLib.Slice or List&lt;Slice&gt;.
        /// </summary>
        void RXFreqChange(object o);

        /// <summary>
        /// Initialize the pan adapter display after radio connection is established.
        /// </summary>
        void PanSetup();

        /// <summary>
        /// Query waterfall data to find the zero-beat frequency for CW.
        /// Returns 0 if no valid frequency found.
        /// </summary>
        ulong ZeroBeatFreq();

        /// <summary>
        /// Reload configuration when the operator profile changes.
        /// </summary>
        void OperatorChangeHandler();

        /// <summary>
        /// Detach event handlers and clean up during shutdown.
        /// </summary>
        void Close();
    }
}
