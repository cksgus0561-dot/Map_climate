using System.Text.Json;

namespace TheGarden.Core.Climate;

/// <summary>One map layer written by tools/climate/worldclimate (RGBA image in the editor's display frame).</summary>
public sealed record WorldClimateLayer(string Id, string Title, string File, string Legend, string Kind)
{
    /// <summary>Categories of a categorical layer (label, #rrggbb), drawn by the app itself; empty for colour scales.</summary>
    public IReadOnlyList<(string Label, string Color)> LegendItems { get; init; } = Array.Empty<(string, string)>();
}

/// <summary>One climate state found for a terrain (e.g. the warm climate and a snowball state).</summary>
public sealed record WorldClimateState(string Folder, string Label, string Start, bool? Stable, double GlobalMeanC,
    double GlobalPrecipitationMmDay, IReadOnlyDictionary<string, double> KoppenGroupShare, IReadOnlyList<WorldClimateLayer> Layers);

/// <summary>A sea suggested by the model as a possible deep-water formation site (the user chooses).</summary>
public sealed record DeepwaterSuggestion(double Latitude, double Longitude, double AreaMillionKm2, bool Circumpolar);

/// <summary>Climate of one climate-grid cell (about 480 km) as stored in state-K/climate.json.</summary>
public sealed record WorldClimateCell(double Latitude, double Longitude, double LandFraction, double HeightMeters,
    double[] TemperatureC, double[] PrecipitationMmDay, double SeaIce, string? Koppen, string? KoppenName, string? Biome,
    int SnowSeasons, bool Glacier, string? Explanation)
{
    public UnitVector Direction { get; } = new GeoPoint(Latitude, Longitude).ToVector();
}

/// <summary>
/// Climate of one map pixel as the layers were drawn (state-K/point.bin): temperature follows the pixel's height,
/// rain its windward slope or rain shadow, and Koppen type and biome are classified per pixel.
/// </summary>
public sealed record WorldClimatePixel(double HeightMeters, double[] TemperatureC, double[] PrecipitationMmDay, string? Koppen, string? KoppenName, string? Biome);

/// <summary>
/// Result folder of the worldbuilding climate model (tools/climate/worldclimate/run.py):
/// result.json + state-K/climate.json + state-K/layers/*.png + state-K/legends/*.png.
/// The model is a simplified seasonal climate for worldbuilding; its own result.json says so in Model.
/// </summary>
public sealed class WorldClimateAtlas
{
    public string DirectoryPath { get; }
    public string Model { get; }
    public string Name { get; }
    public string? TerrainSha256 { get; }
    public DateTimeOffset CreatedUtc { get; }
    public double Seconds { get; }
    public int? FrameWidth { get; }
    public int? FrameHeight { get; }
    public IReadOnlyList<WorldClimateState> States { get; }
    public IReadOnlyList<DeepwaterSuggestion> DeepwaterSuggestions { get; }
    public IReadOnlyList<GeoPoint> DeepwaterSites { get; }
    private readonly Dictionary<int, WorldClimateCell[]> cells = new();

    private WorldClimateAtlas(string directory, JsonElement root)
    {
        DirectoryPath = directory;
        Model = root.GetProperty("model").GetString() ?? "";
        Name = root.GetProperty("name").GetString() ?? "";
        TerrainSha256 = root.TryGetProperty("terrain_sha256", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
        CreatedUtc = DateTimeOffset.Parse(root.GetProperty("created_utc").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Seconds = root.GetProperty("seconds").GetDouble();
        if (root.TryGetProperty("frame", out var frame) && frame.ValueKind == JsonValueKind.Object)
        { FrameWidth = frame.GetProperty("width").GetInt32(); FrameHeight = frame.GetProperty("height").GetInt32(); }
        States = root.GetProperty("states").EnumerateArray().Select(s => new WorldClimateState(
            s.GetProperty("folder").GetString()!, s.GetProperty("label").GetString()!, s.GetProperty("start").GetString()!,
            s.GetProperty("stable").ValueKind is JsonValueKind.True or JsonValueKind.False ? s.GetProperty("stable").GetBoolean() : null,
            s.GetProperty("global_mean_c").GetDouble(), s.GetProperty("global_precip_mm_day").GetDouble(),
            s.GetProperty("koppen_group_share").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble()),
            s.GetProperty("layers").EnumerateArray().Select(l => new WorldClimateLayer(l.GetProperty("id").GetString()!,
                l.GetProperty("title").GetString()!, l.GetProperty("file").GetString()!, l.GetProperty("legend").GetString()!,
                l.GetProperty("kind").GetString()!) { LegendItems = l.TryGetProperty("legend_items", out var items)
                    ? items.EnumerateArray().Select(i => (i[0].GetString()!, i[1].GetString()!)).ToArray() : Array.Empty<(string, string)>() }).ToArray())).ToArray();
        DeepwaterSuggestions = root.GetProperty("deepwater_suggestions").EnumerateArray().Select(d => new DeepwaterSuggestion(
            d.GetProperty("lat").GetDouble(), d.GetProperty("lon").GetDouble(), d.GetProperty("area_mkm2").GetDouble(),
            d.TryGetProperty("circumpolar", out var c) && c.GetBoolean())).ToArray();
        DeepwaterSites = root.GetProperty("deepwater_sites").EnumerateArray()
            .Select(p => new GeoPoint(p[0].GetDouble(), p[1].GetDouble())).ToArray();
    }

    public static WorldClimateAtlas Read(string directory)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "result.json")));
        return new WorldClimateAtlas(directory, doc.RootElement.Clone());
    }

    public bool Matches(string terrainSha256) => string.Equals(TerrainSha256, terrainSha256, StringComparison.Ordinal);

    public string LayerPath(int state, WorldClimateLayer layer) => Path.Combine(DirectoryPath, States[state].Folder, layer.File.Replace('/', Path.DirectorySeparatorChar));
    public string LegendPath(int state, WorldClimateLayer layer) => Path.Combine(DirectoryPath, States[state].Folder, layer.Legend.Replace('/', Path.DirectorySeparatorChar));

    public IReadOnlyList<WorldClimateCell> Cells(int state)
    {
        lock (cells)
        {
            if (cells.TryGetValue(state, out var known)) return known;
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(DirectoryPath, States[state].Folder, "climate.json")));
            static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var list = doc.RootElement.GetProperty("cells").EnumerateArray().Select(c => new WorldClimateCell(
                c.GetProperty("lat").GetDouble(), c.GetProperty("lon").GetDouble(), c.GetProperty("land").GetDouble(), c.GetProperty("height_m").GetDouble(),
                c.GetProperty("T_c").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
                c.GetProperty("P_mm_day").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
                c.GetProperty("sea_ice").GetDouble(), Text(c, "koppen"), Text(c, "koppen_name"), Text(c, "biome"),
                c.TryGetProperty("snow_seasons", out var sn) ? sn.GetInt32() : 0,
                c.TryGetProperty("glacier", out var gl) && gl.GetBoolean(), Text(c, "explanation"))).ToArray();
            cells[state] = list; return list;
        }
    }

    /// <summary>The climate cell nearest to a point (cells are Voronoi regions of the climate grid).</summary>
    public WorldClimateCell Nearest(int state, GeoPoint point)
    {
        var p = point.ToVector(); WorldClimateCell? best = null; var bestDot = double.NegativeInfinity;
        foreach (var c in Cells(state))
        {
            var d = c.Direction.X * p.X + c.Direction.Y * p.Y + c.Direction.Z * p.Z;
            if (d > bestDot) { bestDot = d; best = c; }
        }
        return best!;
    }

    private sealed record PointRaster(int Width, int Height, int Seasons, byte[] Data, string[] Koppen, string[] KoppenNames, string[] Biomes);
    private readonly Dictionary<int, PointRaster?> points = new();

    /// <summary>The map pixel's own climate at a point (what the layers show), or null for results written before point.bin existed.</summary>
    public WorldClimatePixel? Pixel(int state, GeoPoint point)
    {
        PointRaster? r;
        lock (points)
        {
            if (!points.TryGetValue(state, out r))
            {
                r = null; var folder = Path.Combine(DirectoryPath, States[state].Folder); var meta = Path.Combine(folder, "point.json");
                if (File.Exists(meta))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(meta)); var e = doc.RootElement;
                    static string[] List(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToArray();
                    int w = e.GetProperty("width").GetInt32(), h = e.GetProperty("height").GetInt32(), s = e.GetProperty("seasons").GetInt32();
                    var data = File.ReadAllBytes(Path.Combine(folder, e.GetProperty("file").GetString()!));
                    if (data.Length == (long)w * h * (4 * s + 4)) r = new PointRaster(w, h, s, data, List(e, "koppen"), List(e, "koppen_names"), List(e, "biomes"));
                }
                points[state] = r;
            }
        }
        if (r is null) return null;
        var p = point.Normalized(); var n = r.Width * r.Height;
        var x = Math.Clamp((int)Math.Floor((p.Longitude + 180) / 360 * r.Width), 0, r.Width - 1);
        var y = Math.Clamp((int)Math.Floor((90 - p.Latitude) / 180 * r.Height), 0, r.Height - 1); var i = y * r.Width + x;
        double F2(long offset) => (double)BitConverter.ToHalf(r.Data, (int)offset);
        var t = Enumerable.Range(0, r.Seasons).Select(s => Math.Round(F2(2L * (s * n + i)), 1)).ToArray();
        var pr = Enumerable.Range(0, r.Seasons).Select(s => Math.Round(F2(2L * ((r.Seasons + s) * n + i)), 2)).ToArray();
        var at = 4L * r.Seasons * n; var height = BitConverter.ToInt16(r.Data, (int)(at + 2L * i));
        int k = r.Data[at + 2L * n + i], b = r.Data[at + 3L * n + i];
        return new WorldClimatePixel(height, t, pr, k < r.Koppen.Length ? r.Koppen[k] : null, k < r.KoppenNames.Length ? r.KoppenNames[k] : null, b < r.Biomes.Length ? r.Biomes[b] : null);
    }

    /// <summary>Result folders under a root (each holding result.json), newest first; unreadable folders are skipped.</summary>
    public static IReadOnlyList<WorldClimateAtlas> ReadArchive(string root)
    {
        var list = new List<WorldClimateAtlas>();
        if (!Directory.Exists(root)) return list;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!File.Exists(Path.Combine(dir, "result.json"))) continue;
            try { list.Add(Read(dir)); }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
        }
        return list.OrderByDescending(a => a.CreatedUtc).ToArray();
    }
}
