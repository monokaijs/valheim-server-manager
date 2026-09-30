using System;

namespace ValheimServerManager.MapSupport;

public static class MapCoordinates
{
    // Keep a margin inside the lethal world edge. X=east, Z=north, Y=height, metres.
    public const float Radius = 10000f;
    public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    public static void Validate(float x, float? y, float z)
    {
        if (!Finite(x) || !Finite(z) || (y.HasValue && !Finite(y.Value))) throw new ArgumentException("Coordinates must be finite numbers.");
        if ((double)x * x + (double)z * z > Radius * Radius) throw new ArgumentException("Destination must be within 10,000 metres of the world centre.");
        if (y.HasValue && (y.Value < -100 || y.Value > 500)) throw new ArgumentException("Y must be between -100 and 500 metres.");
    }
}
