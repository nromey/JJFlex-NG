using System.Globalization;
using System.Text;

namespace JJFlex.UiaProbe;

/// <summary>
/// What a control's value read found, before it is put into words. Filled by
/// <see cref="Observe.ReadValue"/>; kept free of UI Automation types so the
/// wording can be tested without a desktop.
/// </summary>
/// <param name="HasValuePattern">The control exposes ValuePattern.</param>
/// <param name="Value">ValuePattern's value. An empty string here is a real, empty value.</param>
/// <param name="HasRangeValue">The control exposes RangeValuePattern.</param>
/// <param name="RangeValue">RangeValuePattern's value.</param>
/// <param name="RangeMinimum">RangeValuePattern's minimum.</param>
/// <param name="RangeMaximum">RangeValuePattern's maximum.</param>
/// <param name="Failure">Why the read itself failed, or null.</param>
internal sealed record ValueReading(
    bool HasValuePattern,
    string? Value,
    bool HasRangeValue,
    double RangeValue,
    double RangeMinimum,
    double RangeMaximum,
    string? Failure = null);

/// <summary>
/// The words for a value read (#989). A read that finds nothing says what it
/// did not find, never an empty string: an empty line reads as an empty field,
/// which is a tool claiming an outcome it did not earn.
/// </summary>
internal static class ValueRead
{
    /// <summary>No control matched.</summary>
    internal const string NoSuchControl = "NO SUCH CONTROL";

    /// <summary>The control matched but has no value to read.</summary>
    internal const string NoValuePattern =
        "NO VALUE PATTERN: the control exposes neither ValuePattern nor RangeValuePattern, so it has no value"
        + " to read. That is not the same as an empty value.";

    /// <summary>The read, in words. Null means no control matched.</summary>
    internal static string Describe(ValueReading? reading)
    {
        if (reading == null) return NoSuchControl;
        if (reading.Failure != null) return "COULD NOT READ: " + reading.Failure;
        if (!reading.HasValuePattern && !reading.HasRangeValue) return NoValuePattern;

        var sb = new StringBuilder();
        if (reading.HasValuePattern)
        {
            sb.AppendLine(string.IsNullOrEmpty(reading.Value)
                ? "value: (empty: the control has a value pattern and its value is empty)"
                : "value: \"" + reading.Value + "\"");
        }
        if (reading.HasRangeValue)
        {
            sb.AppendLine("range value: " + Number(reading.RangeValue)
                + " (minimum " + Number(reading.RangeMinimum) + ", maximum " + Number(reading.RangeMaximum) + ")");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// What <c>jjprobe focus</c> and <c>watch</c> have always shown for the
    /// focused element: its ValuePattern value, or the empty string. Unchanged
    /// on purpose: Observe compares it before and after a press.
    /// </summary>
    internal static string FocusText(ValueReading reading) =>
        reading.HasValuePattern ? reading.Value ?? "" : "";

    private static string Number(double n) => n.ToString("G", CultureInfo.InvariantCulture);
}
