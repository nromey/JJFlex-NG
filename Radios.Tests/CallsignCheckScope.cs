using System;

namespace Radios.Tests
{
    /// <summary>
    /// Sets the callsign check (<see cref="FlexBase.CallsignCheckOn"/>, #1306)
    /// for one test or one test class, and puts back whatever it was.
    /// </summary>
    /// <remarks>
    /// <para>The switch is one process-wide static, so every class that sets it
    /// shares <see cref="RadioConfigStaticsCollection"/>: xUnit runs classes in
    /// parallel, and a class turning the check off while another expects a
    /// refusal would fail somewhere unrelated to the change that broke it.</para>
    /// <para>Since 2026-11-24 the check is off by default (ruled by Noel), so a
    /// test that expects a refusal for a missing callsign turns it ON first,
    /// explicitly, with this.</para>
    /// </remarks>
    internal sealed class CallsignCheckScope : IDisposable
    {
        private readonly bool _was;

        internal CallsignCheckScope(bool on)
        {
            _was = FlexBase.CallsignCheckOn;
            FlexBase.CallsignCheckOn = on;
        }

        public void Dispose() => FlexBase.CallsignCheckOn = _was;
    }
}
