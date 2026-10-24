namespace TheGarden.Core;

/// <summary>Latitude/longitude in degrees, north/east positive. Seam is ±180°.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    public GeoPoint Normalized() => new(Math.Clamp(Latitude, -90, 90), Geography.WrapLongitude(Longitude));
    public UnitVector ToVector()
    {
        var lat = Latitude * Geography.DegreesToRadians;
        var lon = Longitude * Geography.DegreesToRadians;
        return new(Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat), Math.Cos(lat) * Math.Cos(lon));
    }
}

public readonly record struct UnitVector(double X, double Y, double Z)
{
    public static double Dot(UnitVector a, UnitVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static UnitVector Cross(UnitVector a, UnitVector b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static UnitVector operator +(UnitVector a, UnitVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static UnitVector operator -(UnitVector a, UnitVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static UnitVector operator *(UnitVector a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    public UnitVector Normalized()
    {
        var length = Math.Sqrt(Dot(this, this));
        return length > 1e-12 ? this * (1 / length) : new(0, 0, 1);
    }
    public GeoPoint ToGeo() => new(Math.Asin(Math.Clamp(Y, -1, 1)) / Geography.DegreesToRadians,
        Geography.WrapLongitude(Math.Atan2(X, Z) / Geography.DegreesToRadians));
}

public static class Geography
{
    public const double DegreesToRadians = Math.PI / 180;
    public static double WrapLongitude(double degrees) => (degrees % 360 + 540) % 360 - 180;
    public static double AngularDistance(GeoPoint a, GeoPoint b) =>
        Math.Acos(Math.Clamp(UnitVector.Dot(a.ToVector(), b.ToVector()), -1, 1));
    public static GeoPoint Interpolate(GeoPoint a, GeoPoint b, double t)
    {
        var av = a.ToVector();
        var bv = b.ToVector();
        var angle = Math.Acos(Math.Clamp(UnitVector.Dot(av, bv), -1, 1));
        if (angle < 1e-8) return a;
        if (angle > Math.PI - 1e-6) return t < .5 ? a : b;
        var sine = Math.Sin(angle);
        return (av * (Math.Sin((1 - t) * angle) / sine) + bv * (Math.Sin(t * angle) / sine)).Normalized().ToGeo();
    }
    public static GeoPoint FromMap(double u, double v) => new(Math.Clamp(90 - v * 180, -90, 90), WrapLongitude(u * 360 - 180));
    public static (double U, double V) ToMap(GeoPoint point) => ((WrapLongitude(point.Longitude) + 180) / 360, (90 - point.Latitude) / 180);
    /// <summary>Destination on a sphere, used for the same physical brush outline in both views.</summary>
    public static GeoPoint Destination(GeoPoint start, double bearing, double angle)
    {
        var lat = start.Latitude * DegreesToRadians;
        var lon = start.Longitude * DegreesToRadians;
        var lat2 = Math.Asin(Math.Clamp(Math.Sin(lat) * Math.Cos(angle) + Math.Cos(lat) * Math.Sin(angle) * Math.Cos(bearing), -1, 1));
        var lon2 = lon + Math.Atan2(Math.Sin(bearing) * Math.Sin(angle) * Math.Cos(lat), Math.Cos(angle) - Math.Sin(lat) * Math.Sin(lat2));
        return new(lat2 / DegreesToRadians, WrapLongitude(lon2 / DegreesToRadians));
    }
}

/// <summary>Orthographic sphere camera shared by picking and projection. No UI dependencies.</summary>
public sealed class GlobeCamera
{
    public double Longitude { get; set; } = 10;
    public double Latitude { get; set; } = 18;
    public double Zoom { get; set; } = 1;
    public (UnitVector Right, UnitVector Up, UnitVector Forward) Basis()
    {
        var lat = Latitude * Geography.DegreesToRadians;
        var lon = Longitude * Geography.DegreesToRadians;
        return (new(Math.Cos(lon), 0, -Math.Sin(lon)),
            new(-Math.Sin(lat) * Math.Sin(lon), Math.Cos(lat), -Math.Sin(lat) * Math.Cos(lon)),
            new(Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat), Math.Cos(lat) * Math.Cos(lon)));
    }
    public GeoPoint? Pick(double x, double y)
    {
        var r2 = x * x + y * y;
        if (r2 > 1) return null;
        var (right, up, forward) = Basis();
        return (right * x + up * y + forward * Math.Sqrt(1 - r2)).Normalized().ToGeo();
    }
    public (double X, double Y, bool Visible) Project(GeoPoint point)
    {
        var (right, up, forward) = Basis();
        var p = point.ToVector();
        return (UnitVector.Dot(p, right), UnitVector.Dot(p, up), UnitVector.Dot(p, forward) >= 0);
    }
}
