using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>The executed command route, frozen when its transport attempt begins.</summary>
    public sealed class RadioConnectionBinding
    {
        internal RadioConnectionBinding(CommandConnection connection, ConnectionLifetime.Token lifetime,
            string serial, bool isWan, string accountId)
        {
            Connection = connection; Lifetime = lifetime; Serial = serial;
            IsWan = isWan; AccountId = accountId;
        }
        public CommandConnection Connection { get; }
        public ConnectionLifetime.Token Lifetime { get; }
        public string Serial { get; }
        public bool IsWan { get; }
        /// <summary>The session that actually returned the WAN handle. Null means unknown or LAN.</summary>
        public string AccountId { get; }
    }

    /// <summary>Radio-reader evidence plus its executed route; suitable for retaining or queuing.</summary>
    public sealed class ConnectionClientReport
    {
        internal ConnectionClientReport(RadioConnectionBinding binding, RadioClientReport report)
        { Binding = binding; Report = report; }
        public RadioConnectionBinding Binding { get; }
        public RadioClientReport Report { get; }
    }
}
