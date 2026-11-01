using Flex.Smoothlake.FlexLib;
using Radios.StationConnect;

namespace Radios.Tests.StationConnect;

/// <summary>A binding for handing a status notification to a rig by hand, as the tests of its handler do.</summary>
internal sealed class ObservationBindingFor
{
    internal readonly ObservationBinding Binding;
    internal ObservationBindingFor(Radio radio) => Binding = new ObservationBinding(radio, new ConnectionAttempt("test-serial"));
}
