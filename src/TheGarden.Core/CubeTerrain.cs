using System.Security.Cryptography;

namespace TheGarden.Core;

/// <summary>
/// Heights on a cube-sphere: the six faces of a cube, each an N x N grid of cells, pushed out onto the sphere
/// with the equal-angle mapping (a cell spans the same angle along each face axis), so cells are nearly the
/// same size everywhere (N = 2048: about 4.9 km, within +-30%) and nothing crowds at the poles.
/// Heights are float32 metres at cell centres; between centres the surface is bilinear within a face.
/// Faces are split into square tiles for change tracking (undo, redraw, fingerprint).
/// </summary>
public sealed class CubeTerrain
{
    public const int DefaultSize = 2048, TileSize = 256;
    public int Size { get; }
    public int TilesPerEdge { get; }
    public int CellCount => 6 * Size * Size;
    private readonly float[][] faces;
    private readonly double[] tangent;                    // tan of each cell centre's angle along a face axis
    private readonly byte[]?[] tileHashes;                // cached per tile, cleared when the tile changes

    // face f: points n + u A + v B with u, v = tan(angle) in [-1, 1]
    private static readonly (UnitVector N, UnitVector A, UnitVector B)[] Frames =
    [
        (new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)),
        (new(-1, 0, 0), new(0, -1, 0), new(0, 0, 1)),
        (new(0, 1, 0), new(-1, 0, 0), new(0, 0, 1)),
        (new(0, -1, 0), new(1, 0, 0), new(0, 0, 1)),
        (new(0, 0, 1), new(0, 1, 0), new(-1, 0, 0)),
        (new(0, 0, -1), new(0, 1, 0), new(1, 0, 0)),
    ];

    public CubeTerrain(int size = DefaultSize, float initialHeight = 0)
    {
        if (size < TileSize || size % TileSize != 0 || size > 8192) throw new ArgumentException("지형 격자 크기가 올바르지 않습니다.");
        Size = size; TilesPerEdge = size / TileSize;
        faces = Enumerable.Range(0, 6).Select(_ => new float[size * size]).ToArray();
        if (initialHeight != 0) foreach (var f in faces) Array.Fill(f, initialHeight);
        tangent = Enumerable.Range(0, size).Select(i => Math.Tan(((i + .5) / size - .5) * Math.PI / 2)).ToArray();
        tileHashes = new byte[]?[6 * TilesPerEdge * TilesPerEdge];
    }

    public float this[int face, int i, int j]
    {
        get => faces[face][j * Size + i];
        set => faces[face][j * Size + i] = value;
    }
    public float[] Face(int face) => faces[face];

    /// <summary>Direction of the centre of cell (i, j) on a face.</summary>
    public UnitVector CellDirection(int face, int i, int j)
    {
        var (n, a, b) = Frames[face];
        return (n + a * tangent[i] + b * tangent[j]).Normalized();
    }

    /// <summary>Face and continuous cell coordinates (cell centres at integers) of a direction.</summary>
    public (int Face, double X, double Y) Locate(UnitVector p)
    {
        int face; var ax = Math.Abs(p.X); var ay = Math.Abs(p.Y); var az = Math.Abs(p.Z);
        if (ax >= ay && ax >= az) face = p.X >= 0 ? 0 : 1;
        else if (ay >= az) face = p.Y >= 0 ? 2 : 3;
        else face = p.Z >= 0 ? 4 : 5;
        var (n, a, b) = Frames[face]; var d = UnitVector.Dot(p, n);
        var x = (Math.Atan(UnitVector.Dot(p, a) / d) / (Math.PI / 2) + .5) * Size - .5;
        var y = (Math.Atan(UnitVector.Dot(p, b) / d) / (Math.PI / 2) + .5) * Size - .5;
        return (face, x, y);
    }

    /// <summary>Bilinear height between the four nearest cell centres of the face (clamped at face edges).</summary>
    public double Sample(UnitVector p)
    {
        var (face, x, y) = Locate(p);
        var i = Math.Clamp((int)Math.Floor(x), 0, Size - 2); var j = Math.Clamp((int)Math.Floor(y), 0, Size - 2);
        var tx = Math.Clamp(x - i, 0, 1); var ty = Math.Clamp(y - j, 0, 1);
        var h = faces[face]; var k = j * Size + i;
        return (h[k] * (1 - tx) + h[k + 1] * tx) * (1 - ty) + (h[k + Size] * (1 - tx) + h[k + Size + 1] * tx) * ty;
    }

    /// <summary>Cell ranges (inclusive) of each face that a spherical cap can touch.</summary>
    public List<(int Face, int I0, int I1, int J0, int J1)> CapRanges(UnitVector center, double angularRadius)
    {
        var result = new List<(int, int, int, int, int)>();
        var r = Math.Min(angularRadius, Math.PI);
        // boundary points of the cap plus its centre, projected onto every face they lie on
        var east = Math.Abs(center.Y) < .99 ? UnitVector.Cross(new UnitVector(0, 1, 0), center).Normalized() : new UnitVector(1, 0, 0);
        var north = UnitVector.Cross(center, east);
        var points = new List<UnitVector> { center };
        for (var k = 0; k < 48; k++)
        {
            var t = k * Math.PI * 2 / 48;
            points.Add((center * Math.Cos(r) + (east * Math.Cos(t) + north * Math.Sin(t)) * Math.Sin(r)).Normalized());
        }
        for (var f = 0; f < 6; f++)
        {
            var (n, a, b) = Frames[f];
            // the cap reaches this face's sector only if it comes within the sector's angular extent (corner: 54.74 deg)
            if (Math.Acos(Math.Clamp(UnitVector.Dot(center, n), -1, 1)) > r + .9553 + 1e-9) continue;
            double u0 = 1, u1 = -1, v0 = 1, v1 = -1; var whole = false;
            foreach (var p in points)
            {
                var d = UnitVector.Dot(p, n);
                if (d <= 1e-6) { whole = true; break; }        // part of the cap lies beyond this face's plane: take it all
                var u = Math.Atan(UnitVector.Dot(p, a) / d); var v = Math.Atan(UnitVector.Dot(p, b) / d);
                u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
            }
            // a cap containing the face centre's axis direction, or wider than the sampled outline, covers the middle too
            if (UnitVector.Dot(center, n) >= Math.Cos(r)) { u0 = Math.Min(u0, 0); u1 = Math.Max(u1, 0); v0 = Math.Min(v0, 0); v1 = Math.Max(v1, 0); }
            int Index(double angle) => (int)Math.Floor((angle / (Math.PI / 2) + .5) * Size);
            int i0, i1, j0, j1;
            if (whole || r > .6) { i0 = j0 = 0; i1 = j1 = Size - 1; }
            else
            {
                const double pad = 2;                           // a cell of margin on each side for the outline's straight segments
                i0 = Math.Max(0, Index(u0) - (int)pad); i1 = Math.Min(Size - 1, Index(u1) + (int)pad);
                j0 = Math.Max(0, Index(v0) - (int)pad); j1 = Math.Min(Size - 1, Index(v1) + (int)pad);
                if (i0 > i1 || j0 > j1) continue;
            }
            result.Add((f, i0, i1, j0, j1));
        }
        return result;
    }

    // ---- tiles
    public int TileIndex(int face, int i, int j) => (face * TilesPerEdge + j / TileSize) * TilesPerEdge + i / TileSize;
    public (int Face, int TileI, int TileJ) TileOf(int tile) => (tile / (TilesPerEdge * TilesPerEdge), tile % TilesPerEdge, tile / TilesPerEdge % TilesPerEdge);
    public float[] CopyTile(int tile)
    {
        var (f, ti, tj) = TileOf(tile); var copy = new float[TileSize * TileSize]; var h = faces[f];
        for (var r = 0; r < TileSize; r++) Array.Copy(h, (tj * TileSize + r) * Size + ti * TileSize, copy, r * TileSize, TileSize);
        return copy;
    }
    public void WriteTile(int tile, float[] values)
    {
        var (f, ti, tj) = TileOf(tile); var h = faces[f];
        for (var r = 0; r < TileSize; r++) Array.Copy(values, r * TileSize, h, (tj * TileSize + r) * Size + ti * TileSize, TileSize);
        tileHashes[tile] = null;
    }
    public void TileChanged(int tile) => tileHashes[tile] = null;

    /// <summary>SHA-256 of the size and every height (little-endian float32), face by face, tile by tile:
    /// the hash of the tile hashes, so after an edit only the changed tiles are hashed again.</summary>
    public string Fingerprint(float seaLevel, double radius)
    {
        Parallel.For(0, tileHashes.Length, t =>
        {
            if (tileHashes[t] is not null) return;
            var (f, ti, tj) = TileOf(t); var h = faces[f];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var row = new byte[TileSize * 4];
            for (var r = 0; r < TileSize; r++)
            { Buffer.BlockCopy(h, ((tj * TileSize + r) * Size + ti * TileSize) * 4, row, 0, row.Length); hash.AppendData(row); }
            tileHashes[t] = hash.GetHashAndReset();
        });
        using var all = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        all.AppendData(System.Text.Encoding.ASCII.GetBytes("garden-cube-terrain-v1\0"));
        all.AppendData(BitConverter.GetBytes(radius)); all.AppendData(BitConverter.GetBytes(seaLevel)); all.AppendData(BitConverter.GetBytes(Size));
        foreach (var t in tileHashes) all.AppendData(t!);
        return Convert.ToHexString(all.GetHashAndReset()).ToLowerInvariant();
    }

    public CubeTerrain Clone()
    {
        var copy = new CubeTerrain(Size);
        for (var f = 0; f < 6; f++) Array.Copy(faces[f], copy.faces[f], faces[f].Length);
        Array.Copy(tileHashes, copy.tileHashes, tileHashes.Length);
        return copy;
    }
}
