using System.Collections.Concurrent;

namespace TheGarden.Core;

public sealed record PlanetSettings
{
    public string Preset { get; init; } = "Earth-v1";
    public double RadiusMeters { get; init; } = 6_371_000;
    public double GravityMetersPerSecondSquared { get; init; } = 9.80665;
    public double SiderealRotationSeconds { get; init; } = 86164.0905;
    public double ObliquityDegrees { get; init; } = 23.4393;
    public double SolarConstantWattsPerSquareMeter { get; init; } = 1361;
    public double ReferenceSurfacePressurePascals { get; init; } = 101325;
    public double CarbonDioxidePpm { get; init; } = 280;
    public double OrbitalPeriodDays { get; init; } = 365.25636;
    public double OrbitalEccentricity { get; init; } = .0167;
    public static PlanetSettings Earth { get; } = new();
}


/// <summary>
/// The planet's terrain: a cube-sphere height map (<see cref="CubeTerrain"/>, about 4.9 km cells) is the only
/// source. Width/Height describe the derived latitude-longitude display raster, which never feeds back.
/// </summary>
public sealed class World
{
    public const float MinElevation = -12000, MaxElevation = 12000;
    public const int DefaultDisplayWidth = 4096;
    public string Name { get; set; }
    public int Width { get; }
    public int Height { get; }
    public float SeaLevelMeters { get; }
    public PlanetSettings Planet { get; }
    public long Revision { get; private set; }
    public CubeTerrain Terrain { get; }
    private string? fingerprint;
    private (int X0, int Y0, int X1, int Y1)? displayDirty;
    private readonly object displayGate = new();      // a stroke paints on a worker thread while the display takes the region

    public World(string name, int width = DefaultDisplayWidth, int height = DefaultDisplayWidth / 2, float initialElevation = -2500,
        float seaLevelMeters = 0, PlanetSettings? planet = null, int cubeSize = CubeTerrain.DefaultSize)
        : this(name, new CubeTerrain(cubeSize, Validated(initialElevation)), width, height, seaLevelMeters, planet ?? PlanetSettings.Earth) { }
    internal World(string name, CubeTerrain terrain, int width, int height, float seaLevelMeters, PlanetSettings planet)
    {
        if (width < 16 || height < 8 || width > 8192 || height > 4096 || width != height * 2) throw new ArgumentException("표시 크기가 올바르지 않습니다.");
        Validated(seaLevelMeters);
        if (planet != PlanetSettings.Earth) throw new ArgumentException("이 버전은 Earth-v1 고정 조건만 지원합니다.");
        Name = name; Terrain = terrain; Width = width; Height = height; SeaLevelMeters = seaLevelMeters; Planet = planet;
        displayDirty = (0, 0, width - 1, height - 1);
    }
    private static float Validated(float h) =>
        float.IsFinite(h) && h >= MinElevation && h <= MaxElevation ? h : throw new ArgumentException("고도 값이 올바르지 않습니다.");

    /// <summary>Approximate cell spacing on the equator of a face [km].</summary>
    public double SpacingKm => Planet.RadiusMeters / 1000 * Math.PI / 2 / Terrain.Size;
    public GeoPoint DisplayCoordinate(int x, int y) => new(90 - (y + .5) * 180 / Height, (x + .5) * 360 / Width - 180);
    public float Sample(GeoPoint point)
    {
        if (!double.IsFinite(point.Latitude) || !double.IsFinite(point.Longitude)) throw new ArgumentException("위치가 유한하지 않습니다.");
        return (float)Terrain.Sample(point.Normalized().ToVector());
    }

    public float[] CreateDisplayElevations()
    {
        var result = new float[Width * Height];
        FillDisplayElevations(result, 0, 0, Width - 1, Height - 1);
        return result;
    }
    /// <summary>Recomputes the display raster inside the pixel rectangle (inclusive).</summary>
    public void FillDisplayElevations(float[] raster, int x0, int y0, int x1, int y1)
    {
        var sinLon = new double[x1 - x0 + 1]; var cosLon = new double[sinLon.Length];
        for (var x = x0; x <= x1; x++) { var lon = ((x + .5) * 360.0 / Width - 180) * Geography.DegreesToRadians; sinLon[x - x0] = Math.Sin(lon); cosLon[x - x0] = Math.Cos(lon); }
        Parallel.For(y0, y1 + 1, y =>
        {
            var lat = (90 - (y + .5) * 180.0 / Height) * Geography.DegreesToRadians; var c = Math.Cos(lat); var s = Math.Sin(lat);
            for (var x = x0; x <= x1; x++)
                raster[y * Width + x] = (float)Terrain.Sample(new UnitVector(c * sinLon[x - x0], s, c * cosLon[x - x0]));
        });
    }

    public void MarkChanged() { Revision++; fingerprint = null; }
    /// <summary>An edit inside a spherical cap: the fingerprint is stale and the cap's pixels need redrawing.</summary>
    internal void MarkChanged(UnitVector center, double angularRadius)
    {
        MarkChanged();
        var c = center.ToGeo(); var r = angularRadius / Geography.DegreesToRadians;
        var latMax = c.Latitude + r; var latMin = c.Latitude - r;
        int x0 = 0, x1 = Width - 1;
        if (latMax < 89 && latMin > -89)
        {
            var s = Math.Sin(angularRadius) / Math.Cos(c.Latitude * Geography.DegreesToRadians);
            if (s < 1)
            {
                var dLon = Math.Asin(s) / Geography.DegreesToRadians;
                var lon0 = c.Longitude - dLon; var lon1 = c.Longitude + dLon;
                if (lon0 >= -180 && lon1 <= 180) { x0 = (int)Math.Floor((lon0 + 180) / 360 * Width); x1 = (int)Math.Ceiling((lon1 + 180) / 360 * Width); }
            }
        }
        var y0 = (int)Math.Floor((90 - Math.Min(90, latMax)) / 180 * Height); var y1 = (int)Math.Ceiling((90 - Math.Max(-90, latMin)) / 180 * Height);
        AddDirty(x0 - 2, y0 - 2, x1 + 2, y1 + 2);     // a pixel of margin for relief shading
    }
    /// <summary>A whole tile was rewritten (undo, redo, cancel): redraw the display around it, not the whole planet.</summary>
    internal void MarkTileChanged(int tile)
    {
        const int T = CubeTerrain.TileSize; var (f, ti, tj) = Terrain.TileOf(tile);
        var center = Terrain.CellDirection(f, ti * T + T / 2, tj * T + T / 2);
        var radius = 0.0;
        foreach (var (i, j) in new[] { (0, 0), (T - 1, 0), (0, T - 1), (T - 1, T - 1) })
            radius = Math.Max(radius, Math.Acos(Math.Clamp(UnitVector.Dot(center, Terrain.CellDirection(f, ti * T + i, tj * T + j)), -1, 1)));
        // the display samples between cells: a few cells of margin past the tile's corner cells
        MarkChanged(center, radius + 3 * Math.PI / Terrain.Size);
    }
    public void MarkAllDisplayDirty() { MarkChanged(); lock (displayGate) displayDirty = (0, 0, Width - 1, Height - 1); }
    private void AddDirty(int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(Width - 1, x1); y1 = Math.Min(Height - 1, y1);
        lock (displayGate)
            displayDirty = displayDirty is { } d ? (Math.Min(d.X0, x0), Math.Min(d.Y0, y0), Math.Max(d.X1, x1), Math.Max(d.Y1, y1)) : (x0, y0, x1, y1);
    }
    /// <summary>The display pixels changed since the last call (null: none).</summary>
    public (int X0, int Y0, int X1, int Y1)? TakeDisplayDirty() { lock (displayGate) { var d = displayDirty; displayDirty = null; return d; } }

    public World Clone() => new(Name, Terrain.Clone(), Width, Height, SeaLevelMeters, Planet) { fingerprint = fingerprint };

    public WorldStatistics Statistics()
    {
        var n = Terrain.Size; var sea = SeaLevelMeters; var half = n / 2;
        var weights = AreaWeights(n);
        double land = 0, all = 0; float min = float.MaxValue, max = float.MinValue; var gate = new object();
        Parallel.For(0, 6 * n, row =>
        {
            var f = row / n; var j = row % n; var h = Terrain.Face(f);
            double l = 0, a = 0; float lo = float.MaxValue, hi = float.MinValue;
            var wRow = (j < half ? j : n - 1 - j) * half;
            for (var i = 0; i < n; i++)
            {
                var w = weights[wRow + (i < half ? i : n - 1 - i)];
                var v = h[j * n + i]; a += w; if (v >= sea) l += w; lo = Math.Min(lo, v); hi = Math.Max(hi, v);
            }
            lock (gate) { land += l; all += a; min = Math.Min(min, lo); max = Math.Max(max, hi); }
        });
        return new(land / all, min, max, Terrain.CellCount, SpacingKm);
    }

    // Equal-angle cell area ~ sec^2(a_i) sec^2(a_j) / (1 + tan^2 a_i + tan^2 a_j)^(3/2), one quarter of a face (the rest
    // mirrors it), computed once per grid size: the statistics run after every stroke and the power was most of their cost.
    private static readonly ConcurrentDictionary<int, double[]> AreaWeightTables = new();
    private static double[] AreaWeights(int n) => AreaWeightTables.GetOrAdd(n, size =>
    {
        var half = size / 2; var w = new double[half * half];
        var tan = Enumerable.Range(0, half).Select(i => Math.Tan(((i + .5) / size - .5) * Math.PI / 2)).ToArray();
        Parallel.For(0, half, j =>
        {
            for (var i = 0; i < half; i++)
            { var ti = tan[i]; var tj = tan[j]; w[j * half + i] = (1 + ti * ti) * (1 + tj * tj) / Math.Pow(1 + ti * ti + tj * tj, 1.5); }
        });
        return w;
    });

    public string Fingerprint() => fingerprint ??= Terrain.Fingerprint(SeaLevelMeters, Planet.RadiusMeters);
}
public sealed record WorldStatistics(double LandFraction, float MinimumMeters, float MaximumMeters, int CellCount, double SpacingKm);
