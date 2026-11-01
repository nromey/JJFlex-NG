using System;
using Radios;

namespace JJFlexWpf.Controls;

/// <summary>
/// Put <see cref="RadioPowerFloor"/> on a field an operator enters power in
/// (#859): the arrows stop at the floor and a typed value under it is refused
/// aloud. The field still shows whatever the radio reports. Asked at each press,
/// so a field built before the radio is known needs nothing further, and every
/// other radio is untouched.
/// </summary>
internal static class PowerFieldFloor
{
    /// <param name="field">The power field.</param>
    /// <param name="rig">The radio attached now.</param>
    /// <param name="inWatts">False while the field holds something other than watts. Null: always watts.</param>
    public static void Apply(ValueFieldControl field, Func<FlexBase?> rig, Func<bool>? inWatts = null)
    {
        field.OperatorFloor = () => inWatts?.Invoke() == false ? int.MinValue : RadioPowerFloor.Watts(rig()?.RadioModel);
        field.TypedValueRefusal = typed => inWatts?.Invoke() == false
            ? null : RadioPowerFloor.TypedPowerRefusal(rig()?.RadioModel, typed);
    }
}
