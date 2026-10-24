using System.IO.Compression;
using System.Text.Json;
using TheGarden.Core;

internal static class CubeChecks
{
    private static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Near(double a, double b, double tolerance, string message) => Assert(Math.Abs(a - b) <= tolerance, $"{message}: {a:R} vs {b:R}");
    private static World Small(string name, float initial = 0) => new(name, 1024, 512, initial, cubeSize: 512);
    private static TerrainCommand Paint(World w, GeoPoint p, double radius, TerrainTool tool = TerrainTool.Flatten, double roughness = 0)
    { var s = new TerrainStroke(w, new(tool, radius, 1000, 1, roughness)); s.MoveTo(p); return s.Complete(); }

    public static void Register(Action<string, Action> check)
    {
        check("Cube cells: a cell centre locates back to its own cell on every face", () =>
        {
            var t = new CubeTerrain(512); var random = new Random(7);
            for (var k = 0; k < 3000; k++)
            {
                var f = random.Next(6); var i = random.Next(512); var j = random.Next(512);
                var (face, x, y) = t.Locate(t.CellDirection(f, i, j));
                Assert(face == f && Math.Abs(x - i) < 1e-6 && Math.Abs(y - j) < 1e-6, $"cell {f},{i},{j} -> {face},{x},{y}");
            }
            // equal-angle cells: the largest is at most ~1.5x the smallest in width (corners vs face centres)
            var centre = Math.Acos(UnitVector.Dot(t.CellDirection(0, 255, 255), t.CellDirection(0, 256, 255)));
            var corner = Math.Acos(UnitVector.Dot(t.CellDirection(0, 0, 0), t.CellDirection(0, 1, 0)));
            Assert(corner / centre is > .5 and < 1.5, $"cell size ratio {corner / centre}");
        });
        check("Brush cap ranges contain every cell inside the cap (poles, face corners, seams, large caps)", () =>
        {
            var t = new CubeTerrain(256);
            foreach (var (lat, lon, km) in new[] { (0.0, 0.0, 300.0), (90, 0, 800), (-89.5, 40, 200), (35.26, 45, 500), (0, 180, 650), (10, -135, 2000), (-35.26, -45, 3000) })
            {
                var c = new GeoPoint(lat, lon).ToVector(); var r = km / 6371; var cos = Math.Cos(r);
                var ranges = t.CapRanges(c, r);
                for (var f = 0; f < 6; f++)
                    for (var j = 0; j < 256; j++)
                        for (var i = 0; i < 256; i++)
                        {
                            if (UnitVector.Dot(c, t.CellDirection(f, i, j)) <= cos) continue;
                            Assert(ranges.Any(x => x.Face == f && i >= x.I0 && i <= x.I1 && j >= x.J0 && j <= x.J1), $"cap {lat},{lon},{km}: cell {f},{i},{j} missed");
                        }
            }
        });
        check("Undo, redo and cancel restore heights bit-exactly; a no-op stroke changes nothing", () =>
        {
            var w = TerrainExamples.Create(1024, 512, 512); var history = new EditHistory(); var start = w.Fingerprint();
            history.Push(Paint(w, new(20, 30), 900, roughness: .7)); var one = w.Fingerprint();
            history.Push(Paint(w, new(-45, 179.9), 400, TerrainTool.Smooth)); var two = w.Fingerprint();
            Assert(start != one && one != two, "strokes change the terrain");
            Assert(history.Undo(w) && w.Fingerprint() == one && history.Undo(w) && w.Fingerprint() == start, "undo bit-exact");
            Assert(history.Redo(w) && w.Fingerprint() == one && history.Redo(w) && w.Fingerprint() == two, "redo bit-exact");
            var s = new TerrainStroke(w, new(TerrainTool.Flatten, 1500, -3000)); s.MoveTo(new(0, 0)); s.MoveTo(new(0, 40)); s.Cancel();
            Assert(w.Fingerprint() == two, "cancel restores");
            var flat = Small("flat", 1000); var hash = flat.Fingerprint();
            Assert(Paint(flat, new(15, 40), 300, TerrainTool.Flatten).ChangedCells == 0 && flat.Fingerprint() == hash, "flatten to the same height is a no-op");
            Assert(Paint(flat, new(-15, 40), 300, TerrainTool.Smooth).ChangedCells == 0, "smoothing a constant is a no-op");
        });
        check("The display redraws only the changed region, identical to a full redraw", () =>
        {
            var w = TerrainExamples.Create(1024, 512, 512); var display = w.CreateDisplayElevations(); w.TakeDisplayDirty();
            foreach (var p in new GeoPoint[] { new(10, 20), new(70, 179), new(-88, -60), new(0, -180) })
            {
                Paint(w, p, 700, roughness: .5);
                var d = w.TakeDisplayDirty()!.Value; w.FillDisplayElevations(display, d.X0, d.Y0, d.X1, d.Y1);
            }
            var full = w.CreateDisplayElevations();
            Assert(full.SequenceEqual(display), "incremental display matches");
        });
        check("Roughness: painted land varies with fixed noise around the target height", () =>
        {
            var smooth = Small("smooth"); Paint(smooth, new(0, 0), 600);
            var rough = Small("rough"); Paint(rough, new(0, 0), 600, roughness: 1);
            var pts = Enumerable.Range(-6, 13).SelectMany(i => Enumerable.Range(-6, 13).Select(j => new GeoPoint(i * .3, j * .3))).ToArray();
            var a = pts.Select(p => (double)smooth.Sample(p)).ToArray(); var b = pts.Select(p => (double)rough.Sample(p)).ToArray();
            double Sd(double[] x) { var m = x.Average(); return Math.Sqrt(x.Average(v => (v - m) * (v - m))); }
            // the roughness part alone: the rough result minus the smooth one varies by hundreds of metres around 0
            var relief = b.Zip(a, (x, y) => x - y).ToArray();
            Assert(Sd(relief) > 100, $"relief spread {Sd(relief):F0} m");
            Near(relief.Average(), 0, 80, "relief centred on the target");
            var again = Small("again"); Paint(again, new(0, 0), 600, roughness: 1);
            Assert(again.Fingerprint() == rough.Fingerprint(), "the same stroke gives the same relief");
        });
        check("Climate input: a v2 triangle surface the model verifies, naming the cube world; v2 files import", () =>
        {
            var dir = Path.Combine(Environment.CurrentDirectory, "artifacts", "checks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "input.garden");
            try
            {
                var w = TerrainExamples.Create(1024, 512, 512);
                WorldFile.SaveClimateInput(w, path, level: 5);
                using (var zip = ZipFile.OpenRead(path))
                {
                    Assert(zip.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "faces.i32", "vertices.bin", "world.json" }), "three entries");
                    using var json = JsonDocument.Parse(zip.GetEntry("world.json")!.Open());
                    Assert(json.RootElement.GetProperty("FormatVersion").GetInt32() == 2 && json.RootElement.GetProperty("Grid").GetString() == WorldFile.VectorGrid, "v2 triangle grid");
                    Assert(json.RootElement.GetProperty("SourceTerrainSha256").GetString() == w.Fingerprint(), "names the cube world");
                }
                var back = WorldFile.Load(path);                   // the v2 importer checks the triangle checksum
                var worst = Enumerable.Range(0, 400).Max(k => { var p = new GeoPoint(k % 140 - 70, k * 37 % 360 - 180); return Math.Abs(back.Sample(p) - w.Sample(p)); });
                Assert(worst < 2500, $"sampled triangle surface follows the cube: worst {worst:F0} m");
            }
            finally { Directory.Delete(dir, true); }
        });
    }
}
