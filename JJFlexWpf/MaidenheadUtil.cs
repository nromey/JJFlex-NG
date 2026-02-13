using System;

namespace JJFlexWpf;

/// <summary>
/// Maidenhead grid square utilities: grid-to-lat/lon conversion,
/// great-circle distance (Haversine), and initial bearing.
/// </summary>
public static class MaidenheadUtil
{
    private const double EarthRadiusMiles = 3958.8;
    private const double EarthRadiusKm = 6371.0;
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    /// <summary>
    /// Validate a Maidenhead grid locator string (4 or 6 characters).
    /// </summary>
    public static bool IsValidGrid(string grid)
    {
        if (string.IsNullOrEmpty(grid))
            return false;

        // Normalize to uppercase for field letters, lowercase for subsquare.
        grid = grid.Trim();
        if (grid.Length != 4 && grid.Length != 6)
            return false;

        // Field: two letters A-R
        char f1 = char.ToUpper(grid[0]);
        char f2 = char.ToUpper(grid[1]);
        if (f1 < 'A' || f1 > 'R' || f2 < 'A' || f2 > 'R')
            return false;

        // Square: two digits 0-9
        if (!char.IsDigit(grid[2]) || !char.IsDigit(grid[3]))
            return false;

        if (grid.Length == 6)
        {
            // Subsquare: two letters a-x
            char s1 = char.ToLower(grid[4]);
            char s2 = char.ToLower(grid[5]);
            if (s1 < 'a' || s1 > 'x' || s2 < 'a' || s2 > 'x')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Convert a Maidenhead grid locator to latitude/longitude (center of grid square).
    /// Supports 4-char (e.g., "FN31") and 6-char (e.g., "FN31pr") locators.
    /// Returns false if the grid is invalid.
    /// </summary>
    public static bool GridToLatLon(string grid, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;

        if (!IsValidGrid(grid))
            return false;

        grid = grid.Trim();
        char f1 = char.ToUpper(grid[0]);
        char f2 = char.ToUpper(grid[1]);
        int sq1 = grid[2] - '0';
        int sq2 = grid[3] - '0';

        // Field: 20° lon, 10° lat blocks starting at -180, -90
        longitude = (f1 - 'A') * 20.0 - 180.0;
        latitude = (f2 - 'A') * 10.0 - 90.0;

        // Square: 2° lon, 1° lat blocks within field
        longitude += sq1 * 2.0;
        latitude += sq2 * 1.0;

        if (grid.Length == 6)
        {
            char ss1 = char.ToLower(grid[4]);
            char ss2 = char.ToLower(grid[5]);

            // Subsquare: 5' lon (2/24°), 2.5' lat (1/24°) blocks within square
            longitude += (ss1 - 'a') * (2.0 / 24.0) + (1.0 / 48.0); // center
            latitude += (ss2 - 'a') * (1.0 / 24.0) + (1.0 / 48.0);  // center
        }
        else
        {
            // Center of 4-char grid (1° lon, 0.5° lat offset)
            longitude += 1.0;
            latitude += 0.5;
        }

        return true;
    }

    /// <summary>
    /// Calculate great-circle distance in miles using the Haversine formula.
    /// </summary>
    public static double DistanceMiles(double lat1, double lon1, double lat2, double lon2)
    {
        return HaversineDistance(lat1, lon1, lat2, lon2, EarthRadiusMiles);
    }

    /// <summary>
    /// Calculate great-circle distance in kilometers using the Haversine formula.
    /// </summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        return HaversineDistance(lat1, lon1, lat2, lon2, EarthRadiusKm);
    }

    /// <summary>
    /// Calculate initial bearing (azimuth) from point 1 to point 2, in degrees (0-360).
    /// </summary>
    public static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        double phi1 = lat1 * DegToRad;
        double phi2 = lat2 * DegToRad;
        double dLambda = (lon2 - lon1) * DegToRad;

        double y = Math.Sin(dLambda) * Math.Cos(phi2);
        double x = Math.Cos(phi1) * Math.Sin(phi2) -
                   Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLambda);

        double theta = Math.Atan2(y, x);
        return (theta * RadToDeg + 360.0) % 360.0;
    }

    private static double HaversineDistance(double lat1, double lon1,
                                             double lat2, double lon2,
                                             double radius)
    {
        double dLat = (lat2 - lat1) * DegToRad;
        double dLon = (lon2 - lon1) * DegToRad;
        double phi1 = lat1 * DegToRad;
        double phi2 = lat2 * DegToRad;

        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(phi1) * Math.Cos(phi2) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return radius * c;
    }
}
