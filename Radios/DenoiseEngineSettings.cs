using System.Collections.Generic;

namespace Radios
{
    public enum DenoiseDirection { Receive, Transmit }

    // Values only: no catalog, config owner, profile or radio dependency.
    public sealed class DenoiseEngineSettings
    {
        public string EngineId { get; set; } = "";
        public DenoiseDirection Direction { get; set; }
        public float Strength { get; set; } = 1f;
        public List<DenoiseNamedValue> Values { get; set; } = new();
    }

    public sealed class DenoiseNamedValue
    {
        public string Name { get; set; } = "";
        public double Number { get; set; }
        public string? Choice { get; set; }
    }
}
