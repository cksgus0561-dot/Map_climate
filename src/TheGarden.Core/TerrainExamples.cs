namespace TheGarden.Core;

/// <summary>Deterministic sculpting example, not tectonics, observed Earth, or a climate result.</summary>
public static class TerrainExamples
{
    public static World Create(int width = World.DefaultDisplayWidth, int height = World.DefaultDisplayWidth / 2, int cubeSize = CubeTerrain.DefaultSize)
    {
        var world = new World("첫 번째 지도", width, height, cubeSize: cubeSize);
        var centers = new (UnitVector Center, double Radius, double Height)[]
        {
            (new GeoPoint(32, -36).ToVector(), .39, 8000),
            (new GeoPoint(2, -54).ToVector(), .26, 5700),
            (new GeoPoint(-29, -38).ToVector(), .28, 6600),
            (new GeoPoint(28, 69).ToVector(), .43, 8200),
            (new GeoPoint(-9, 102).ToVector(), .31, 6600),
            (new GeoPoint(-42, 126).ToVector(), .27, 6400),
            (new GeoPoint(54, -143).ToVector(), .31, 7700),
            (new GeoPoint(-47, -152).ToVector(), .25, 6100)
        };
        // the formula's finest features are ~50 km: evaluate it on a coarse cube, then sample that onto the fine one
        var coarse = new CubeTerrain(Math.Min(512, cubeSize));
        Parallel.For(0, 6 * coarse.Size, row =>
        {
            var f = row / coarse.Size; var j = row % coarse.Size;
            for (var i = 0; i < coarse.Size; i++)
            {
                var p = coarse.CellDirection(f, i, j);
                var low = Noise(p, 3.2);
                var detail = .56 * Noise(p, 12) + .28 * Noise(p, 28) + .12 * Noise(p, 62) + .04 * Noise(p, 130);
                double elevation = -3800 + low * 800 + detail * 600;
                foreach (var (center, radius, amplitude) in centers)
                {
                    var angle = Math.Acos(Math.Clamp(UnitVector.Dot(p, center), -1, 1));
                    var warped = angle / (radius * 1.65 * (1 + .20 * Noise(p, 7)));
                    elevation += amplitude * Math.Exp(-warped * warped * 1.6);
                }
                if (elevation > -100)
                {
                    var land = Math.Clamp((elevation + 100) / 1800, 0, 1);
                    var ridge = Math.Pow(1 - Math.Abs(Noise(p, 18)), 7);
                    elevation += land * (ridge * 2100 + detail * 350);
                }
                coarse[f, i, j] = (float)Math.Clamp(elevation, World.MinElevation, World.MaxElevation);
            }
        });
        var fine = world.Terrain;
        Parallel.For(0, 6 * fine.Size, row =>
        {
            var f = row / fine.Size; var j = row % fine.Size; var h = fine.Face(f);
            for (var i = 0; i < fine.Size; i++) h[j * fine.Size + i] = (float)coarse.Sample(fine.CellDirection(f, i, j));
        });
        world.MarkAllDisplayDirty();
        return world;
    }
    private static double Noise(UnitVector p, double f) =>
        (Math.Sin((p.X * .83 + p.Y * 1.17 + p.Z * .39) * f + 1.3) +
         Math.Sin((p.X * -.61 + p.Y * .43 + p.Z * 1.21) * f + 4.7) +
         Math.Sin((p.X * 1.31 - p.Y * .71 + p.Z * .57) * f + 2.1)) / 3;
}
