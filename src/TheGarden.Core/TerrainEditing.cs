using System.Collections.Concurrent;

namespace TheGarden.Core;

// Ridge: a mountain range along the stroke, crest at TargetMeters; Pick: reads a height (the app's eyedropper), never edits.
public enum TerrainTool { Navigate, Flatten, Smooth, Ridge, Pick }
// Softness: 1 = the bump (1 - d^2)^2 over the whole radius; 0 = full strength right to the edge.
// Roughness: 0 = exactly the target height; up to 1 = the target varies from place to place with fixed terrain
// noise (features from about 400 km down to the 5 km cells), so the painted land comes out as hills and valleys.
public sealed record BrushSettings(TerrainTool Tool, double RadiusKm, double TargetMeters, double Softness = 1, double Roughness = 0);

/// <summary>One stroke's change: the touched tiles before and after (only tiles that really changed).</summary>
public sealed class TerrainCommand
{
    internal IReadOnlyDictionary<int, float[]> Before { get; }
    internal IReadOnlyDictionary<int, float[]> After { get; }
    public int ChangedCells { get; }
    internal long Cost => 8L * CubeTerrain.TileSize * CubeTerrain.TileSize * Before.Count;
    internal TerrainCommand(IReadOnlyDictionary<int, float[]> before, IReadOnlyDictionary<int, float[]> after, int count)
    { Before = before; After = after; ChangedCells = count; }
}

/// <summary>
/// One brush stroke. Each cell changes once, by its distance to the path the brush has travelled so far: the path is
/// the polyline through every pointer position, so the band is continuous however far apart the positions arrive,
/// and a place the brush passes again is not painted twice. A cell is recomputed only when the path comes closer to it.
/// </summary>
public sealed class TerrainStroke
{
    private readonly World world;
    private readonly BrushSettings brush;
    private readonly Dictionary<int, float[]> before = [];                 // tile -> heights before this stroke
    // per touched tile: each cell's closest distance to the path [rad] and what the brush pulls it toward
    // (Flatten: target plus roughness noise; Smooth: the mean around it; Ridge: crest and relief), computed once
    private readonly ConcurrentDictionary<int, CellState> cells = new();
    private sealed class CellState
    {
        private const int N = CubeTerrain.TileSize * CubeTerrain.TileSize;
        // pooled: a fresh 256 KB array per tile per stroke kept the large-object heap collecting in the middle of drags
        private static readonly System.Buffers.ArrayPool<float> Pool = System.Buffers.ArrayPool<float>.Shared;
        public readonly float[] Distance = Pool.Rent(N), Aim = Pool.Rent(N);
        public readonly float[]? Relief;
        public bool Changed;
        public CellState(bool ridge)
        {
            Array.Fill(Distance, float.PositiveInfinity, 0, N); Array.Fill(Aim, float.NaN, 0, N);
            if (ridge) Relief = Pool.Rent(N);
        }
        public void Release() { Pool.Return(Distance); Pool.Return(Aim); if (Relief is not null) Pool.Return(Relief); }
    }
    private void ReleaseCells() { foreach (var state in cells.Values) state.Release(); cells.Clear(); }
    private GeoPoint? previous;
    public TerrainStroke(World world, BrushSettings brush)
    {
        if (!Enum.IsDefined(brush.Tool) || !double.IsFinite(brush.RadiusKm) || brush.RadiusKm <= 0 || brush.RadiusKm > 20000 ||
            !double.IsFinite(brush.TargetMeters) || brush.TargetMeters < World.MinElevation || brush.TargetMeters > World.MaxElevation ||
            !double.IsFinite(brush.Softness) || brush.Softness < 0 || brush.Softness > 1 ||
            !double.IsFinite(brush.Roughness) || brush.Roughness < 0 || brush.Roughness > 1)
            throw new ArgumentException("붓 설정이 올바르지 않습니다.");
        this.world = world; this.brush = brush;
    }
    public void MoveTo(GeoPoint point)
    {
        if (brush.Tool is TerrainTool.Navigate or TerrainTool.Pick) return;
        if (!double.IsFinite(point.Latitude) || !double.IsFinite(point.Longitude)) throw new ArgumentException("위치가 유한하지 않습니다.");
        point = point.Normalized();
        if (previous is not { } from) { Sweep(point.ToVector(), point.ToVector()); previous = point; return; }
        var distance = Geography.AngularDistance(from, point);
        if (distance < 1e-12) return;
        // a long jump goes in pieces of at most one radius, so each piece's bounding cap stays close around it
        var pieces = (int)Math.Ceiling(distance / Math.Min(Radius, Math.PI / 4));
        var a = from.ToVector();
        for (var k = 1; k <= pieces; k++)
        {
            var b = Geography.Interpolate(from, point, k / (double)pieces).ToVector();
            Sweep(a, b); a = b;
        }
        previous = point;
    }
    private double Radius => Math.Min(Math.PI, brush.RadiusKm * 1000 / world.Planet.RadiusMeters);

    /// <summary>Applies the path segment a-b (a point when a = b) to every cell within the radius of it.</summary>
    private void Sweep(UnitVector a, UnitVector b)
    {
        var terrain = world.Terrain; var n = terrain.Size; var radius = Radius; const int T = CubeTerrain.TileSize;
        var half = Math.Acos(Math.Clamp(UnitVector.Dot(a, b), -1, 1)) / 2;
        var arc = half > 1e-12;
        var middle = arc ? (a + b).Normalized() : a;
        var normal = arc ? UnitVector.Cross(a, b).Normalized() : a;
        var cap = Math.Min(Math.PI, half + radius); var cosine = Math.Cos(cap);
        var ridge = brush.Tool == TerrainTool.Ridge; var changed = 0;
        var ranges = terrain.CapRanges(middle, cap);
        // every tile the cap can reach keeps its heights from before the stroke first, so the cells can be written in place
        foreach (var (f, i0, i1, j0, j1) in ranges)
            for (var tj = j0 / T; tj <= j1 / T; tj++)
                for (var ti = i0 / T; ti <= i1 / T; ti++)
                {
                    var tile = terrain.TileIndex(f, ti * T, tj * T);
                    if (!before.ContainsKey(tile)) before[tile] = terrain.CopyTile(tile);
                }
        foreach (var (f, i0, i1, j0, j1) in ranges)
        {
            var h = terrain.Face(f);
            Parallel.For(j0, j1 + 1, j =>
            {
                CellState? state = null; float[] old = null!; var stateTile = -1; var rowChanged = 0;
                for (var i = i0; i <= i1; i++)
                {
                    var p = terrain.CellDirection(f, i, j);
                    if (UnitVector.Dot(middle, p) <= cosine) continue;
                    var d = arc ? DistanceToArc(p, a, b, normal) : Angle(p, a);
                    if (d >= radius) continue;
                    var tile = terrain.TileIndex(f, i, j);
                    if (tile != stateTile) { state = cells.GetOrAdd(tile, _ => new CellState(ridge)); old = before[tile]; stateTile = tile; }
                    var s = (j % T) * T + i % T;
                    if (d >= state!.Distance[s]) continue;           // the path was already this close: nothing new
                    state.Distance[s] = (float)d;
                    var k = j * n + i;
                    var start = old[s];                               // the height before this stroke
                    if (float.IsNaN(state.Aim[s]))
                    {
                        state.Aim[s] = (float)(brush.Tool switch
                        {
                            TerrainTool.Flatten => brush.TargetMeters + (brush.Roughness > 0 ? brush.Roughness * 600 * Terrain(p) : 0),
                            TerrainTool.Smooth => MeanBefore(f, i, j, h, old),
                            _ => Crest(p)
                        });
                        if (ridge) state.Relief![s] = (float)Relief(p);
                    }
                    var aim = state.Aim[s]; var t = d / radius;
                    var next = ridge
                        // a broad massif: cross-section 1 - d^1.5 from the height before the stroke, never below it
                        ? Math.Max(start, start + (aim - start) * (1 - Math.Pow(t, 1.5)) * state.Relief![s])
                        : start + (aim - start) * Weight(t);
                    var value = (float)Math.Clamp(next, World.MinElevation, World.MaxElevation);
                    if (value != h[k]) { h[k] = value; state.Changed = true; rowChanged++; }   // each cell belongs to one row: one writer
                }
                if (rowChanged > 0) Interlocked.Add(ref changed, rowChanged);
            });
        }
        if (changed == 0) return;
        foreach (var (tile, state) in cells) if (state.Changed) { terrain.TileChanged(tile); state.Changed = false; }
        world.MarkChanged(middle, cap);
    }
    private static double Angle(UnitVector p, UnitVector q) => Math.Acos(Math.Clamp(UnitVector.Dot(p, q), -1, 1));
    /// <summary>Angular distance from p to the shorter great-circle arc a-b (normal: unit a x b).</summary>
    private static double DistanceToArc(UnitVector p, UnitVector a, UnitVector b, UnitVector normal)
    {
        // p lies beside the arc (between the planes through a and b) when it is on the inner side of both
        if (UnitVector.Dot(UnitVector.Cross(a, p), normal) >= 0 && UnitVector.Dot(UnitVector.Cross(p, b), normal) >= 0)
            return Math.Asin(Math.Min(1, Math.Abs(UnitVector.Dot(p, normal))));
        return Math.Min(Angle(p, a), Angle(p, b));
    }
    private double Weight(double d)
    {
        var core = 1 - brush.Softness;          // full strength inside this share of the radius
        if (d <= core) return 1;
        var t = (d - core) / (1 - core); return (1 - t * t) * (1 - t * t);
    }
    // Smooth pulls each cell toward the mean of the 7 x 7 cells around it (about 35 km), from the heights before the stroke
    private double MeanBefore(int f, int i, int j, float[] h, float[] own)
    {
        var n = world.Terrain.Size; const int T = CubeTerrain.TileSize; double sum = 0; var count = 0;
        int li = i % T, lj = j % T;
        if (li >= 3 && li < T - 3 && lj >= 3 && lj < T - 3)      // all inside the cell's own tile (most cells)
        {
            for (var y = lj - 3; y <= lj + 3; y++) for (var x = li - 3; x <= li + 3; x++) sum += own[y * T + x];
            return sum / 49;
        }
        for (var y = Math.Max(0, j - 3); y <= Math.Min(n - 1, j + 3); y++)
            for (var x = Math.Max(0, i - 3); x <= Math.Min(n - 1, i + 3); x++)
            {
                sum += before.TryGetValue(world.Terrain.TileIndex(f, x, y), out var old) ? old[(y % T) * T + x % T] : h[y * n + x];
                count++;
            }
        return sum / count;
    }
    // Mountain range: crest varied +-10% around the target along the range; with roughness the ridged noise cuts it into
    // branching ridges and valleys (up to 60% of the height between them).
    private double Crest(UnitVector p) => brush.TargetMeters * (1 + .1 * TerrainNoise.Fractal(p, 2, 3));
    // ridge spurs about half the brush radius apart, finer ones down to the cells
    private double Relief(UnitVector p) => brush.Roughness > 0 ? 1 - brush.Roughness * .75 * (1 - TerrainNoise.Ridged(p, 2 / Radius)) : 1;
    private static double Terrain(UnitVector p) => TerrainNoise.Fractal(p, 16, 7);

    public TerrainCommand Complete()
    {
        var terrain = world.Terrain; var b = new Dictionary<int, float[]>(); var a = new Dictionary<int, float[]>(); var count = 0;
        foreach (var (tile, old) in before)
        {
            var now = terrain.CopyTile(tile); var differ = 0;
            for (var k = 0; k < now.Length; k++) if (now[k] != old[k]) differ++;
            if (differ == 0) continue;
            b[tile] = old; a[tile] = now; count += differ;
        }
        ReleaseCells();
        return new(b, a, count);
    }
    public void Cancel()
    {
        foreach (var (tile, old) in before)
            if (!world.Terrain.CopyTile(tile).AsSpan().SequenceEqual(old)) { world.Terrain.WriteTile(tile, old); world.MarkTileChanged(tile); }
        ReleaseCells();
    }
}

/// <summary>Fixed gradient (Perlin) noise on the sphere: the same place always gets the same relief.</summary>
public static class TerrainNoise
{
    /// <summary>Sum of octaves in about [-1, 1]; frequency in cycles per planet radius (16: features ~400 km on Earth).
    /// Each finer octave keeps 0.62 of the amplitude (land relief falls off slowly with scale), so 5-20 km hills stay visible.</summary>
    public static double Fractal(UnitVector p, double frequency, int octaves)
    {
        double sum = 0, amplitude = 1, total = 0;
        for (var o = 0; o < octaves; o++) { sum += amplitude * Gradient(p.X * frequency, p.Y * frequency, p.Z * frequency, o); total += amplitude; amplitude *= .62; frequency *= 2; }
        return Math.Clamp(sum / total * 2.9, -1, 1);     // spread (s.d.) about 0.4, about 1% clipped
    }
    /// <summary>Ridged noise in [0, 1]: sharp crests (1) along lines that branch, broad lows between them;
    /// frequency in cycles per planet radius of the coarsest crests.</summary>
    public static double Ridged(UnitVector p, double frequency = 12)
    {
        double sum = 0, amplitude = 1, total = 0, weight = 1;
        for (var o = 0; o < 6; o++)
        {
            var r = 1 - Math.Abs(Gradient(p.X * frequency, p.Y * frequency, p.Z * frequency, 17 + o)); r *= r * weight;
            weight = Math.Clamp(r * 1.5, 0, 1);
            sum += r * amplitude; total += amplitude; amplitude *= .5; frequency *= 2;
        }
        return sum / total;
    }
    private static double Gradient(double x, double y, double z, int seed)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y), z0 = (int)Math.Floor(z);
        double fx = x - x0, fy = y - y0, fz = z - z0, u = Fade(fx), v = Fade(fy), w = Fade(fz);
        double G(int i, int j, int k)
        {
            var hash = Hash(x0 + i, y0 + j, z0 + k, seed);
            double dx = fx - i, dy = fy - j, dz = fz - k;
            return (hash % 12) switch
            {
                0 => dx + dy, 1 => -dx + dy, 2 => dx - dy, 3 => -dx - dy,
                4 => dx + dz, 5 => -dx + dz, 6 => dx - dz, 7 => -dx - dz,
                8 => dy + dz, 9 => -dy + dz, 10 => dy - dz, _ => -dy - dz
            };
        }
        static double L(double a, double b, double t) => a + (b - a) * t;
        return L(L(L(G(0, 0, 0), G(1, 0, 0), u), L(G(0, 1, 0), G(1, 1, 0), u), v),
                 L(L(G(0, 0, 1), G(1, 0, 1), u), L(G(0, 1, 1), G(1, 1, 1), u), v), w);
        static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    }
    private static uint Hash(int x, int y, int z, int seed)
    {
        var h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177 + seed * 974711);
        h = (h ^ (h >> 13)) * 1274126177u; return h ^ (h >> 16);
    }
}

public sealed class EditHistory
{
    private readonly LinkedList<TerrainCommand> undo = [];
    private readonly Stack<TerrainCommand> redo = [];
    private long memoryCost;
    private const long MemoryBudget = 512L * 1024 * 1024;
    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;
    public void Push(TerrainCommand command)
    {
        if (command.ChangedCells == 0) return;
        foreach (var old in redo) memoryCost -= old.Cost;
        redo.Clear(); undo.AddLast(command); memoryCost += command.Cost;
        while ((undo.Count > 64 || memoryCost > MemoryBudget) && undo.Count > 1)
        { memoryCost -= undo.First!.Value.Cost; undo.RemoveFirst(); }
    }
    public bool Undo(World world)
    {
        if (undo.Last is null) return false;
        var command = undo.Last.Value; undo.RemoveLast(); redo.Push(command); Apply(world, command.Before); return true;
    }
    public bool Redo(World world)
    {
        if (!redo.TryPop(out var command)) return false;
        undo.AddLast(command); Apply(world, command.After); return true;
    }
    private static void Apply(World world, IReadOnlyDictionary<int, float[]> tiles)
    {
        foreach (var (tile, values) in tiles) { world.Terrain.WriteTile(tile, values); world.MarkTileChanged(tile); }
    }
    public void Clear() { undo.Clear(); redo.Clear(); memoryCost = 0; }
}
