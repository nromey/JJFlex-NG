// ****************************************************************************
///*!	\file Filter.cs
// *	\brief Filter Helper class to ease databinding
// *
// *	\copyright	Copyright 2012-2026 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2026-02-05
// *	\author Eric Wachsmann, KE5DTO
// *    \author Sam Hoekwater, KJ5NZM
// */
// ****************************************************************************

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Text;


namespace Flex.Smoothlake.FlexLib
{
    /// <summary>
    /// Filter Preset Mode group enums
    /// </summary>
    public enum FilterPresetModeGroup
    {
        SSB = 0,
        CW = 1,
        AM = 2, // Includes DFM as well, other FM modes do not allow changeable filter widths
        Digital = 3,
        RTTY = 4,
    }

    public static class FilterPresetEnumHelpers
    {
        /// <summary>
        /// Helper function to convert slice mode to mode group enum
        /// </summary>
        public static FilterPresetModeGroup GetModeGroupFromSliceMode(string slice_mode)
        {
            return slice_mode.Trim().ToLowerInvariant() switch
            {
                "usb" or "lsb" => FilterPresetModeGroup.SSB,
                "cw" => FilterPresetModeGroup.CW,
                "am" or "ame" or "dfm" or "dsb" or "dstr" or "sam" => FilterPresetModeGroup.AM,
                "digl" or "digu" or "fdv" => FilterPresetModeGroup.Digital,
                "rtty"  => FilterPresetModeGroup.RTTY,
                // Other slice modes (such as FM) have fixed filter widths.
                _ => throw new ArgumentException($"Invalid Slice mode: {slice_mode}")
            };
        }

        /// <summary>
        /// Helper function to convert mode group string to mode group enum
        /// </summary>
        public static FilterPresetModeGroup GetModeGroupFromString(string mode_group)
        {
            return mode_group.Trim().ToLowerInvariant() switch
            {
                "ssb" => FilterPresetModeGroup.SSB,
                "cw" => FilterPresetModeGroup.CW,
                "am" => FilterPresetModeGroup.AM,
                "digital" => FilterPresetModeGroup.Digital,
                "rtty" => FilterPresetModeGroup.RTTY,
                _ => throw new ArgumentException($"Invalid mode group string: {mode_group}")
            };
        }

        /// <summary>
        /// Gets the mode group string from the mode group to be sent in save and reset commands
        /// </summary>
        /// <param name="mode_group"></param>
        /// <returns>The String form of the mode group</returns>
        public static string GetModeGroupString(FilterPresetModeGroup mode_group)
        {
            switch (mode_group)
            {
                case FilterPresetModeGroup.SSB:
                    return "ssb";
                case FilterPresetModeGroup.CW:
                    return "cw";
                case FilterPresetModeGroup.AM:
                    return "am";
                case FilterPresetModeGroup.Digital:
                    return "digital";
                case FilterPresetModeGroup.RTTY:
                    return "rtty";
                default:
                    return "Invalid";
            }
        }
    }

    [Serializable]
    public class Filter
    {
        public string Name { get; set; }
        public int LowCut { get; set; }
        public int HighCut { get; set; }
        public bool IsFavorite { get; set; }

        public Filter(string name, int low, int high)
        {
            Name = name;
            LowCut = low;
            HighCut = high;
        }

        // Parameterless constructor for serialization purposes
        private Filter()
        {
        }

        /// <summary>
        /// Updates the filter fields with the input parameters
        /// </summary>
        /// <param name="presetName"></param>
        /// <param name="low"></param>
        /// <param name="high"></param>
        public void Update(string presetName, int low, int high)
        {
            // Validate these arguments
            if (low > high) return;
            if (presetName.Length > 4) return;

            if (Name == presetName && LowCut == low && HighCut == high) return;

            Name = presetName;
            LowCut = low;
            HighCut = high;
        }
    }
}
