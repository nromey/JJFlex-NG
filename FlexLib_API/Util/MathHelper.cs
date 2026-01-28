using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Util;

public class MathHelper
{
    public static double RadiansToDegrees(double rad)
    {
        return (180 / Math.PI) * rad;
    }

    public static double DegreesToRadians(double deg)
    {
        return (Math.PI / 180) * deg;
    }
}
