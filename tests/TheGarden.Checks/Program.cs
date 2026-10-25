using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using TheGarden.Core;
using TheGarden.Core.Climate;

var checks = new List<(string Name, Action Run)>();
void Check(string name, Action run) => checks.Add((name, run));
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Near(double actual, double expected, double tolerance, string message) => Assert(Math.Abs(actual - expected) <= tolerance, $"{message}: {actual} vs {expected}");
TerrainCommand Paint(World world, GeoPoint point, double radius = 500, TerrainTool tool = TerrainTool.Flatten, double target = 1000)
{ var stroke = new TerrainStroke(world, new(tool, radius, target)); stroke.MoveTo(point); return stroke.Complete(); }

Check("Map/vector coordinates round trip, seam and exact poles", () =>
{
    foreach (var lat in new[] { -90.0, -89.9, -45, 0, 45, 89.9, 90 })
    foreach (var lon in new[] { -180.0, -179.9, -70, 0, 130, 179.9, 180 })
    {
        var p = new GeoPoint(lat, lon);
        var back = p.ToVector().ToGeo();
        Near(back.Latitude, lat, 1e-8, "latitude");
        if (Math.Abs(lat) < 90) Near(Geography.WrapLongitude(back.Longitude - lon), 0, 1e-8, "longitude");
        var (u, v) = Geography.ToMap(p); var map = Geography.FromMap(u, v);
        Near(Geography.AngularDistance(p, map), 0, 3e-8, "map round trip");
    }
});
Check("Globe picking agrees with projection at all camera latitudes", () =>
{
    var camera = new GlobeCamera();
    foreach (var lat in new[] { -90.0, -70, 0, 43, 90 })
    foreach (var lon in new[] { -180.0, -45, 0, 179 })
    {
        camera.Latitude = lat; camera.Longitude = lon;
        foreach (var (x, y) in new[] { (0.0, 0.0), (.5, .3), (-.8, .1), (.1, -.85) })
        {
            var p = camera.Pick(x, y)!.Value;
            var projected = camera.Project(p);
            Assert(projected.Visible, "front hemisphere");
            Near(projected.X, x, 1e-9, "screen x"); Near(projected.Y, y, 1e-9, "screen y");
            var (u, v) = Geography.ToMap(p);
            Near(Geography.AngularDistance(Geography.FromMap(u, v), p), 0, 3e-8, "globe to map");
        }
    }
    Assert(camera.Pick(1.1, 0) is null, "outside sphere is not selectable");
});
Check("Brush crosses ±180 without painting Greenwich", () =>
{
    var world = new World("seam", 720, 360, 0, cubeSize: 512);
    Paint(world, new(0, 179.8));
    Assert(world.Sample(new(0, 179)) > 300 && world.Sample(new(0, -179)) > 300, "both seam sides must change");
    Near(world.Sample(new(0, 0)), 0, 0, "opposite side unchanged");
});
Check("Polar brush is longitude-independent and finite", () =>
{
    foreach (var pole in new[] { -90, 90 })
    {
        var world = new World("pole", 720, 360, 0, cubeSize: 512);
        Paint(world, new(pole, 70), 650);
        var first = world.Sample(new(pole,0));
        for (var lon = -180; lon <= 180; lon+=10) Near(world.Sample(new(pole,lon)), first, .001, "polar symmetry");
        Assert(first > 390, "pole must be editable");
    }
});
Check("Same brush radius covers the same physical area at different latitudes", () =>
{
    var areas = new List<double>();
    foreach (var lat in new[] { 0, 45, 80, 90 })
    {
        // Compare a well-resolved half-strength contour, not the arbitrarily tiny
        // tail where a continuous triangle interpolation differs from old pixels.
        var world = new World("area", 1440, 720, -200, cubeSize: 512);
        Paint(world, new(lat, 30), 800, TerrainTool.Flatten, 200);   // half way from -200 to 200: the w = 0.5 contour
        var area = world.Statistics().LandFraction;
        areas.Add(area);
    }
    var expected=(1-Math.Cos(800000/PlanetSettings.Earth.RadiusMeters*Math.Sqrt(1-Math.Sqrt(.5))))/2;
    Assert(areas.All(a=>Math.Abs(a/expected-1)<.025), $"physical contour area: {string.Join(", ",areas)}; expected {expected}");
});
Check("A pole is one sampled location regardless of longitude, including after edits", () =>
{
    var world = TerrainExamples.Create(360, 180, 512);
    foreach (var pole in new[] { -90, 90 })
    {
        Paint(world, new(pole > 0 ? 88 : -88, 30), 300);
        var expected = world.Sample(new(pole, 0));
        for (var lon = -180; lon <= 180; lon += 15) Near(world.Sample(new(pole, lon)), expected, .001, "single pole elevation");
    }
});
Check("Undo/redo restores exact floating point values; new edits clear redo", () =>
{
    var world = TerrainExamples.Create(128, 64, 512); var history = new EditHistory(); var before = world.Fingerprint();
    history.Push(Paint(world, new(30, 170), 1700)); var after = world.Fingerprint();
    Assert(before != after, "stroke must change terrain");
    Assert(history.Undo(world) && world.Fingerprint() == before, "undo bit-exact");
    Assert(history.Redo(world) && world.Fingerprint() == after, "redo bit-exact");
    history.Undo(world); history.Push(Paint(world, new(-40, -20), 1200)); Assert(history.RedoCount == 0, "new edit invalidates redo");
});
Check("Stroke sampling is independent of pointer event count", () =>
{
    var a = new World("a", 360, 180, 0, cubeSize: 512); var b = new World("b", 360, 180, 0, cubeSize: 512);
    var brush = new BrushSettings(TerrainTool.Flatten, 600, 300);
    var sa = new TerrainStroke(a, brush); var sb = new TerrainStroke(b, brush);
    var start = new GeoPoint(0, 170); var end = new GeoPoint(0, -150);
    sa.MoveTo(start); sa.MoveTo(end);
    for (var i = 0; i <= 100; i++) sb.MoveTo(Geography.Interpolate(start, end, i / 100.0));
    for (var lat=-80; lat<=80; lat+=5) for(var lon=-180;lon<180;lon+=2) Near(a.Sample(new(lat,lon)), b.Sample(new(lat,lon)), .01, "stroke density");
});
Check("Flatten converges to target; elevation bounds and smooth seam stay valid", () =>
{
    var world = new World("flatten", 360, 180, -2500, cubeSize: 512);
    var center = new GeoPoint(0, 0);
    Paint(world, center, 700, TerrainTool.Flatten, 1800);
    Near(world.Sample(center), 1800, 5, "target at centre");   // cell centres sit a few km off the brush centre
    for (var i = 0; i < 50; i++) Paint(world, center, 700);
    var stats = world.Statistics(); Assert(float.IsFinite(stats.MinimumMeters) && stats.MaximumMeters <= World.MaxElevation, "bounds");
    Paint(world, new(89, 179), 800, TerrainTool.Smooth);
    Assert(float.IsFinite(world.Statistics().MaximumMeters), "smooth finite");
});
Check("Brush edge softness and ridge tool", () =>
{
    TerrainCommand Brush(World w, TerrainTool tool, double softness, double target, params GeoPoint[] path)
    { var s = new TerrainStroke(w, new(tool, 600, target, softness)); foreach (var p in path) s.MoveTo(p); return s.Complete(); }
    var at80 = new GeoPoint(0, 480 / (Math.PI * 6371 / 180));      // 0.8 of a 600 km radius east of (0, 0)
    var soft = new World("soft", 360, 180, 0, cubeSize: 512); Brush(soft, TerrainTool.Flatten, 1, 400, new GeoPoint(0, 0));
    var hard = new World("hard", 360, 180, 0, cubeSize: 512); Brush(hard, TerrainTool.Flatten, 0, 400, new GeoPoint(0, 0));
    Near(soft.Sample(at80), 400 * Math.Pow(1 - .64, 2), 25, "softness 1 keeps the original bump");
    // ~20 km cells blur the hard edge at 600 km a little
    Assert(hard.Sample(at80) > 340 && hard.Sample(at80) <= 400.01, $"softness 0 is near full strength close to the edge: {hard.Sample(at80):F0}");

    var ridge = new World("ridge", 360, 180, 0, cubeSize: 512);
    Brush(ridge, TerrainTool.Ridge, 1, 3000, new GeoPoint(0, 0), new GeoPoint(0, 20));
    var crest = Enumerable.Range(1, 19).Select(lon => (double)ridge.Sample(new(0, lon))).ToArray();
    Assert(crest.All(h => h > 2300 && h < 3300), $"ridge crest along the line: {crest.Min():F0}..{crest.Max():F0}");
    var once = ridge.Sample(new(0, 10));
    Brush(ridge, TerrainTool.Ridge, 1, 3000, new GeoPoint(0, 0), new GeoPoint(0, 20), new GeoPoint(0, 0));
    Assert(ridge.Sample(new(0, 10)) <= 3300 && ridge.Sample(new(0, 10)) >= once - 1, "ridge strokes do not pile up past the crest");
    Near(ridge.Sample(new(30, 10)), 0, 0, "ridge leaves far terrain");
    Assert(new TerrainStroke(ridge, new(TerrainTool.Pick, 600, 0)).Complete().ChangedCells == 0, "pick never edits");
});
Check("Surface sampling is continuous across the date line", () =>
{
    var world = TerrainExamples.Create(360, 180, 512);
    foreach (var lat in new[] { -89.9, -30, 0, 75, 89.9 })
        Near(world.Sample(new(lat, 180 - 1e-7)), world.Sample(new(lat, -180 + 1e-7)), .01, "seam continuity");
});
Check("Save/load round trip, corrupt checksum rejection, existing file replacement", () =>
{
    var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "checks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "world.garden");
    try
    {
        var world = TerrainExamples.Create(128, 64, 512); Paint(world, new(89, 179), 1700);
        WorldFile.Save(world, path); var loaded = WorldFile.Load(path);
        Assert(loaded.Name == world.Name && loaded.Planet == world.Planet, "metadata round trip");
        Assert(loaded.Fingerprint() == world.Fingerprint(), "bit-exact saved heights");
        Paint(world, new(0, 0), 3000); WorldFile.Save(world, path);
        Assert(WorldFile.Load(path).Fingerprint() == world.Fingerprint(), "replace existing project");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("heights.f32")!; var bytes = new byte[entry.Length];
            using (var stream = entry.Open()) stream.ReadExactly(bytes);
            entry.Delete(); bytes[24] ^= 1;
            using var changed = archive.CreateEntry("heights.f32").Open(); changed.Write(bytes);
        }
        var rejected = false;
        try { WorldFile.Load(path); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "corrupt data must be rejected");
    }
    finally { File.Delete(path); Directory.Delete(folder); }
});
Check("A map file keeps its climate: saved with the map, unpacked on opening, without the model's own files", () =>
{
    var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "checks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
    try
    {
        var world = TerrainExamples.Create(128, 64, 512); var hash = world.Fingerprint();
        var result = AtlasFixture.Write(Path.Combine(folder, "result"), hash, world.Width, world.Height, p => File.WriteAllBytes(p, [9, 8, 7]));
        // the clicked place's own climate: a 4 x 2 raster of 4 seasons and its description
        File.WriteAllBytes(Path.Combine(result, "state-0", "point.bin"), new byte[4 * 2 * (4 * 4 + 4)]);
        File.WriteAllText(Path.Combine(result, "state-0", "point.json"),
            """{"width":4,"height":2,"seasons":4,"file":"point.bin","koppen":["Af"],"koppen_names":["열대 우림"],"biomes":["열대 우림"]}""");
        foreach (var extra in new[] { "input.garden", "launcher.log", "status.json", Path.Combine("state-0", "winds.png") })
            File.WriteAllText(Path.Combine(result, extra), "not kept");
        var map = Path.Combine(folder, "world.map"); WorldFile.Save(world, map, result);
        Assert(WorldFile.Load(map).Fingerprint() == hash, "terrain unchanged by the climate entries");
        var opened = Path.Combine(folder, "opened");
        Assert(WorldFile.ExtractClimate(map, opened), "climate found in the map file");
        var atlas = WorldClimateAtlas.Read(opened);
        Assert(atlas.Matches(hash) && atlas.States.Count == 1, "the unpacked climate belongs to the terrain");
        Assert(atlas.Pixel(0, new GeoPoint(10, 20)) is { Koppen: "Af" }, "the clicked place's own climate comes back, not the cell mean");
        foreach (var file in new[] { "result.json", Path.Combine("state-0", "climate.json"), Path.Combine("state-0", "point.json"), Path.Combine("state-0", "point.bin"), Path.Combine("state-0", "layers", "temperature_annual.png") })
            Assert(File.ReadAllBytes(Path.Combine(opened, file)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(result, file))), "same file: " + file);
        Assert(!File.Exists(Path.Combine(opened, "input.garden")) && !File.Exists(Path.Combine(opened, "launcher.log")) &&
               !File.Exists(Path.Combine(opened, "status.json")) && !File.Exists(Path.Combine(opened, "state-0", "winds.png")), "the model's own files stay out");
        var plain = Path.Combine(folder, "plain.map"); WorldFile.Save(world, plain);
        Assert(!WorldFile.ExtractClimate(plain, Path.Combine(folder, "none")), "a map saved without climate has none");
    }
    finally { Directory.Delete(folder, true); }
});
Check("Invalid grids, nonfinite inputs and unsupported planets rejected", () =>
{
    var cases = new Action[] {
        () => _ = new World("bad", 100, 100),
        () => _ = new World("bad", initialElevation: float.NaN),
        () => _ = new World("bad", 1024, 512, cubeSize: 300),
        () => _ = new World("bad", planet: PlanetSettings.Earth with { RadiusMeters = 1 })
    };
    foreach (var action in cases) { var failed = false; try { action(); } catch (ArgumentException) { failed = true; } Assert(failed, "invalid world accepted"); }
});
Check("Surface area statistics preserve a hemispheric coastline", () =>
{
    var world = new World("hemisphere", 1024, 512, 0, cubeSize: 512); var t = world.Terrain;
    for (var f = 0; f < 6; f++) for (var j = 0; j < t.Size; j++) for (var i = 0; i < t.Size; i++) t[f, i, j] = (float)(1000 * t.CellDirection(f, i, j).Y);
    Near(world.Statistics().LandFraction, .5, 1e-3, "hemisphere area");
});

Check("Climate result belongs to its terrain; edit marks stale and undo restores identity", () =>
{
    var world = TerrainExamples.Create(128, 64, 512);
    var hash=world.Fingerprint();
    var result=new ClimateTrialSummary(hash,DateTimeOffset.UtcNow,"ExoPlaSim","3.4.2",1,112,270,1.2,24,"report.json");
    Assert(result.StateLabel(hash)=="시험 완료 · 미수렴", "completion must not claim convergence");
    var edits=new EditHistory(); edits.Push(Paint(world,new(0,0),2000));
    Assert(!result.Matches(world.Fingerprint()) && result.StateLabel(world.Fingerprint())=="재계산 필요", "edited terrain must be stale");
    edits.Undo(world);
    Assert(result.Matches(world.Fingerprint()), "undo to exact terrain should match result");
});
Check("Measured climate reports parse; failed and timed out runs never become completed results", () =>
{
    var path=Path.Combine(AppContext.BaseDirectory,"Fixtures","measured-climate-report.json");
    var results=ClimateTrialSummary.Read(path);
    Assert(results.Count==2,"two actual completed cases; interrupted case must not load");
    Near(results[0].RunSeconds,112.12569904,1e-7,"preserve measured seconds");
    Near(results[0].SurfaceTemperatureKelvin,268.35588634578414,1e-9,"preserve K, do not mistake for C");
    Assert(results.All(r=>r.StateLabel(r.TerrainSha256)=="시험 완료 · 미수렴"),"never claim equilibrium");
});

Check("Worldbuilding climate atlas: reads states and cells, matches terrain, finds nearest cell", () =>
{
    var world=TerrainExamples.Create(); var hash=world.Fingerprint();
    var dir=AtlasFixture.Write(Path.Combine(Path.GetTempPath(),"garden-atlas-"+Guid.NewGuid().ToString("N")),hash,world.Width,world.Height);
    try
    {
        var atlas=WorldClimateAtlas.Read(dir);
        Assert(atlas.Matches(hash) && !atlas.Matches(new string('0',64)),"terrain fingerprint decides the match");
        Assert(atlas.States.Count==1 && atlas.States[0].Layers.Count==1 && atlas.FrameWidth==world.Width,"states, layers and frame");
        Assert(atlas.DeepwaterSites.Count==1 && atlas.DeepwaterSuggestions[0].AreaMillionKm2>11,"deep-water choice and suggestions");
        Assert(atlas.Nearest(0,new GeoPoint(2,3)).Koppen=="Af" && atlas.Nearest(0,new GeoPoint(-80,100)).Glacier,"nearest climate cell");
        Assert(atlas.Nearest(0,new GeoPoint(58,-25)).Koppen is null,"ocean cell has no Koppen type");
        Assert(WorldClimateAtlas.ReadArchive(Path.GetDirectoryName(dir)!).Any(a=>a.Matches(hash)),"archive search");
        var edits=new EditHistory(); edits.Push(Paint(world,new(0,0),2000));
        Assert(!atlas.Matches(world.Fingerprint()),"an edited terrain no longer matches its climate");
    }
    finally { Directory.Delete(dir,true); }
});

Check("Worldbuilding climate atlas: point card reads the map pixel's own climate, older results fall back to cells", () =>
{
    var dir=AtlasFixture.Write(Path.Combine(Path.GetTempPath(),"garden-atlas-"+Guid.NewGuid().ToString("N")),new string('a',64),4,2);
    try
    {
        Assert(WorldClimateAtlas.Read(dir).Pixel(0,new GeoPoint(10,10)) is null,"no point raster: no pixel climate");
        // 4x2 frame, 4 seasons; pixel x=2,y=0 (lat 0..90, lon 0..90) is a 1500 m Dfb highland, the rest sea
        const int n=8, s=4; var data=new byte[n*(4*s+4)]; var i=2;
        void Half(int offset,double v) => BitConverter.TryWriteBytes(data.AsSpan(offset,2),(Half)v);
        for(var k=0;k<s;k++) { Half(2*(k*n+i),-12+8*k); Half(2*((s+k)*n+i),1.5+.25*k); }
        BitConverter.TryWriteBytes(data.AsSpan(4*s*n+2*i,2),(short)1500);
        for(var j=0;j<n;j++) { data[4*s*n+2*n+j]=255; data[4*s*n+3*n+j]=255; }
        data[4*s*n+2*n+i]=1; data[4*s*n+3*n+i]=0;
        File.WriteAllBytes(Path.Combine(dir,"state-0","point.bin"),data);
        File.WriteAllText(Path.Combine(dir,"state-0","point.json"),"""{"width":4,"height":2,"seasons":4,"file":"point.bin","koppen":["Af","Dfb"],"koppen_names":["열대 우림","냉대 습윤(서늘한 여름)"],"biomes":["타이가(냉대림)"]}""");
        var atlas=WorldClimateAtlas.Read(dir); var px=atlas.Pixel(0,new GeoPoint(10,10))!;
        Assert(px.Koppen=="Dfb" && px.Biome=="타이가(냉대림)" && px.HeightMeters==1500,"pixel Koppen, biome and height");
        Assert(px.TemperatureC.SequenceEqual(new[]{-12.0,-4,4,12}) && Math.Abs(px.PrecipitationMmDay[3]-2.25)<1e-9,"pixel seasonal temperature and rain");
        Assert(atlas.Pixel(0,new GeoPoint(-10,10))!.Koppen is null && atlas.Pixel(0,new GeoPoint(10,370))!.Koppen=="Dfb","sea pixel; longitude wraps");
    }
    finally { Directory.Delete(dir,true); }
});

CubeChecks.Register(Check);
ClimateSolutionChecks.Register(Check);
var failures = 0;
foreach (var (name, run) in checks)
{
    try { var checkWatch=Stopwatch.StartNew(); run(); Console.WriteLine($"PASS  {name} ({checkWatch.Elapsed.TotalSeconds:F2}s)"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL  {name}: {ex}"); }
}
var watch = Stopwatch.StartNew(); var example = TerrainExamples.Create(); var generation = watch.Elapsed.TotalMilliseconds;
watch.Restart(); var benchmarkStroke = Paint(example, new(15, 179), 650); var stamp = watch.Elapsed.TotalMilliseconds;
Console.WriteLine($"\n{checks.Count - failures}/{checks.Count} checks passed.");
Console.WriteLine($"Local core benchmark (not climate): example world (cube 2048, ~4.9 km) {generation:F0} ms; 650 km brush stamp {stamp:F2} ms; {benchmarkStroke.ChangedCells:N0} changed cells; land {example.Statistics().LandFraction:P1}.");
Environment.ExitCode = failures == 0 ? 0 : 1;

static class AtlasFixture
{
    public static string Write(string dir, string hash, int width, int height, System.Action<string>? writeLayer = null)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "state-0", "layers"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "state-0", "legends"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.json"), $$"""
        {"model":"worldclimate test fixture","name":"fixture","terrain_sha256":"{{hash}}","created_utc":"2026-10-21T00:00:00+00:00","seconds":1.5,
         "frame":{"width":{{width}},"height":{{height}}},"land_fraction":0.3,"deepwater_sites":[[60.0,-20.0]],
         "deepwater_suggestions":[{"lat":63.0,"lon":-18.0,"area_mkm2":11.2,"circumpolar":false}],
         "states":[{"folder":"state-0","label":"따뜻한 기후안","start":"warm","stable":true,"floquet":[0.8],"global_mean_c":14.0,"global_precip_mm_day":2.5,
           "koppen_group_share":{"A":0.2,"B":0.3},"koppen_land_share":{},"biome_land_share":{},"closed_basins":0,
           "layers":[{"id":"temperature_annual","title":"연평균 기온","file":"layers/temperature_annual.png","legend":"legends/temperature_annual.png","kind":"field"}]}],
         "not_converged":[]}
        """);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "state-0", "climate.json"), """
        {"summary":{},"cells":[
         {"lat":0.0,"lon":0.0,"land":1.0,"height_m":120,"T_c":[25.0,26.0,25.5,24.5],"P_mm_day":[5.0,6.0,5.5,4.5],"sea_ice":0.0,"koppen":"Af","koppen_name":"열대 우림","biome":"열대 우림","snow_seasons":0,"glacier":false,"soil_wetness_min":0.9,"explanation":"열대 우림: 시험 설명"},
         {"lat":60.0,"lon":-20.0,"land":0.0,"height_m":0,"T_c":[4.0,9.0,6.0,2.0],"P_mm_day":[2.0,2.0,3.0,3.0],"sea_ice":0.0},
         {"lat":-70.0,"lon":120.0,"land":1.0,"height_m":2500,"T_c":[-30.0,-40.0,-30.0,-15.0],"P_mm_day":[0.2,0.1,0.2,0.3],"sea_ice":0.9,"koppen":"EF","koppen_name":"빙설","biome":"빙원","snow_seasons":4,"glacier":true,"soil_wetness_min":1.0,"explanation":"빙설"}]}
        """);
        writeLayer?.Invoke(System.IO.Path.Combine(dir, "state-0", "layers", "temperature_annual.png"));
        writeLayer?.Invoke(System.IO.Path.Combine(dir, "state-0", "legends", "temperature_annual.png"));
        return dir;
    }
}
