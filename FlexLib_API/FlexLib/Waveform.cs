// ****************************************************************************
///*!	\file Waveform.cs
// *	\brief Represents a waveform installed on the radio
// *
// *	\copyright	Copyright 2012-2025 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2025-02-18
// */
// ****************************************************************************

namespace Flex.Smoothlake.FlexLib
{
    public record Waveform(string Name, string Version, bool IsContainer)
    {
        public string DisplayName => string.IsNullOrEmpty(Version) ? Name : $"{Name} {Version}";
    }
}
