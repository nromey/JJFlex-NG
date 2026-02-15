using System;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// Shared element types extracted from Flex6300Filters for use by
    /// FlexTNF, TXControls, FlexMemories, and FlexATUMemories.
    /// Sprint 11 Phase 11.1.
    /// </summary>
    public class trueFalseElement
    {
        private bool val;
        public string Display { get { return val.ToString(); } }
        public bool RigItem { get { return val; } }
        public trueFalseElement(bool v)
        {
            val = v;
        }
    }

    public class offOnElement
    {
        private FlexBase.OffOnValues val;
        public string Display { get { return val.ToString(); } }
        public FlexBase.OffOnValues RigItem { get { return val; } }
        public offOnElement(FlexBase.OffOnValues v)
        {
            val = v;
        }
    }

    public class toneCTCSSElement
    {
        private FlexBase.ToneCTCSSValue val;
        public string Display { get { return val.ToString(); } }
        public FlexBase.ToneCTCSSValue RigItem { get { return val; } }
        public toneCTCSSElement(FlexBase.ToneCTCSSValue v)
        {
            val = v;
        }
    }

    public class toneCTCSSFreqElement
    {
        private float val;
        public string Display { get { return val.ToString(); } }
        public float RigItem { get { return val; } }
        public toneCTCSSFreqElement(float v)
        {
            val = v;
        }
    }

    public class offsetDirectionElement
    {
        private FlexBase.OffsetDirections val;
        public string Display { get { return val.ToString(); } }
        public FlexBase.OffsetDirections RigItem { get { return val; } }
        public offsetDirectionElement(FlexBase.OffsetDirections v)
        {
            val = v;
        }
    }

    /// <summary>
    /// Static filter constants and shared element arrays.
    /// </summary>
    public static class FilterConstants
    {
        public const int filterLowMinimum = -12000;
        public const int filterLowMaximum = 12000;
        public const int filterLowIncrement = 50;
        public const int filterHighMinimum = -12000;
        public const int filterHighMaximum = 12000;
        public const int filterHighIncrement = 50;

        public static readonly offsetDirectionElement[] offsetDirectionValues =
        {
            new offsetDirectionElement(FlexBase.OffsetDirections.off),
            new offsetDirectionElement(FlexBase.OffsetDirections.minus),
            new offsetDirectionElement(FlexBase.OffsetDirections.plus)
        };
    }
}
