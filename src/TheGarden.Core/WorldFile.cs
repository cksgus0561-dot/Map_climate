using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace TheGarden.Core;

public sealed record WorldManifest(int FormatVersion, string Name, int Width, int Height,
    float SeaLevelMeters, PlanetSettings Planet, string ElevationUnits, string Grid,
    string TerrainSha256, string ClimateStatus, int VertexCount = 0, int FaceCount = 0, int CubeSize = 0,
    string? SourceTerrainSha256 = null);

/// <summary>
/// .garden v3: a ZIP with world.json and heights.f32 (the six cube faces in order, rows of cells, little-endian
/// float32), and with a calculated climate its result under climate/ (see <see cref="Save"/>).
/// Older v2 files (spherical triangle surface) are read by sampling them onto the cube.
/// The climate model still reads the v2 triangle format: <see cref="SaveClimateInput"/> writes one, sampled
/// from the cube, and names the cube terrain's fingerprint as SourceTerrainSha256.
/// </summary>
public static class WorldFile
{
    public const string CubeGrid = "cube-sphere-equal-angle-v1";
    public const string VectorGrid = "spherical-triangle-surface-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Task SaveAsync(World world, string path, string? climate = null)
    {
        var snapshot = world.Clone();
        return Task.Run(() => Save(snapshot, path, climate));
    }
    /// <summary>climate: a result folder of this terrain (the app's map layers and point climate); it is kept in the
    /// file under climate/, so opening the map shows its climate again without anything stored elsewhere.</summary>
    public static void Save(World world, string path, string? climate = null)
    {
        var snapshot = world.Clone();
        AtomicWrite(path, stream =>
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, true);
            var m = new WorldManifest(3, snapshot.Name, snapshot.Width, snapshot.Height, snapshot.SeaLevelMeters,
                snapshot.Planet, "m", CubeGrid, snapshot.Fingerprint(), climate is null ? "not-calculated" : "included", CubeSize: snapshot.Terrain.Size);
            using (var entry = archive.CreateEntry("world.json").Open()) JsonSerializer.Serialize(entry, m, JsonOptions);
            using (var entry = archive.CreateEntry("heights.f32", CompressionLevel.Fastest).Open())
                for (var f = 0; f < 6; f++) entry.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(snapshot.Terrain.Face(f).AsSpan()));
            if (climate is null) return;
            foreach (var file in Directory.EnumerateFiles(climate, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(climate, file).Replace('\\', '/');
                if (!KeptClimateFile(name)) continue;
                var png = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);      // already compressed
                using var entry = archive.CreateEntry(ClimatePrefix + name, png ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open();
                using var source = File.OpenRead(file); source.CopyTo(entry);
            }
        });
    }
    private const string ClimatePrefix = "climate/";
    // what the app reads of a result: result.json and per state climate.json, point.json + point.bin (the clicked
    // place's own climate; without point.json the card falls back to the 480 km cell mean), layers/, legends/
    // (not the model's copy of the terrain, its logs or the summary sheets)
    private static bool KeptClimateFile(string name)
    {
        if (name == "result.json") return true;
        var parts = name.Split('/');
        if (parts.Length < 2 || !parts[0].StartsWith("state-", StringComparison.Ordinal)) return false;
        return parts.Length == 2 ? parts[1] is "climate.json" or "point.json" or "point.bin" : parts.Length == 3 && parts[1] is "layers" or "legends";
    }
    /// <summary>Unpacks the climate result kept in a map file into an empty folder; false when the file has none.</summary>
    public static bool ExtractClimate(string path, string directory)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.GetEntry(ClimatePrefix + "result.json") is null) return false;
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar; long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(ClimatePrefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;
            var name = entry.FullName[ClimatePrefix.Length..];
            if (!KeptClimateFile(name)) continue;
            if ((total += entry.Length) > 1L << 30) throw new InvalidDataException("지도 파일의 기후 결과가 너무 큽니다.");
            var target = Path.GetFullPath(Path.Combine(directory, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("지도 파일의 기후 결과가 올바르지 않습니다.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
        return true;
    }
    private static void AtomicWrite(string path, Action<FileStream> write)
    {
        var full = Path.GetFullPath(path); var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            { write(stream); stream.Flush(true); }
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static WorldManifest ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry("world.json") ?? throw new InvalidDataException("프로젝트 정보가 없습니다.");
        if (entry.Length > 65536) throw new InvalidDataException("프로젝트 정보가 너무 큽니다.");
        using var stream = entry.Open();
        var m = JsonSerializer.Deserialize<WorldManifest>(stream) ?? throw new InvalidDataException("프로젝트 정보를 읽을 수 없습니다.");
        if (m.FormatVersion is not (2 or 3)) throw new InvalidDataException("지원하지 않는 지도 파일 형식입니다.");
        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 200 || m.Planet is null ||
            m.Width < 16 || m.Height < 8 || m.Width > 8192 || m.Height > 4096 || m.Width != 2 * m.Height ||
            m.ElevationUnits != "m" || m.Planet != PlanetSettings.Earth)
            throw new InvalidDataException("프로젝트 설정이 올바르지 않습니다.");
        return m;
    }
    public static World Load(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        try
        {
            var m = ReadManifest(archive);
            return m.FormatVersion == 3 ? LoadCube(archive, m) : ImportVector(archive, m);
        }
        catch (ArgumentException ex) { throw new InvalidDataException("지형 데이터가 올바르지 않습니다.", ex); }
    }
    private static World LoadCube(ZipArchive archive, WorldManifest m)
    {
        // world.json, heights.f32 and nothing else but a kept climate (climate/...)
        var terrainEntries = archive.Entries.Count(e => !e.FullName.StartsWith(ClimatePrefix, StringComparison.Ordinal));
        if (m.Grid != CubeGrid || terrainEntries != 2 || m.CubeSize < CubeTerrain.TileSize || m.CubeSize > 8192 || m.CubeSize % CubeTerrain.TileSize != 0)
            throw new InvalidDataException("지원하지 않거나 올바르지 않은 지형 형식입니다.");
        var terrain = new CubeTerrain(m.CubeSize);
        using (var stream = Entry(archive, "heights.f32", 24L * m.CubeSize * m.CubeSize).Open())
            for (var f = 0; f < 6; f++) stream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(terrain.Face(f).AsSpan()));
        for (var f = 0; f < 6; f++)
            foreach (var h in terrain.Face(f))
                if (!float.IsFinite(h) || h < World.MinElevation || h > World.MaxElevation) throw new InvalidDataException("고도 값이 올바르지 않습니다.");
        var world = new World(m.Name, terrain, m.Width, m.Height, m.SeaLevelMeters, m.Planet);
        if (!string.Equals(world.Fingerprint(), m.TerrainSha256, StringComparison.Ordinal))
            throw new InvalidDataException("지형 검증에 실패했습니다. 파일이 손상되었을 수 있습니다.");
        return world;
    }
    /// <summary>A v2 triangle-surface world, sampled onto a new cube (its own interpolation, every cell).</summary>
    private static World ImportVector(ZipArchive archive, WorldManifest m)
    {
        if (m.Grid != VectorGrid || archive.Entries.Count != 3 ||
            m.VertexCount < 4 || m.VertexCount > TerrainGeometry.MaxVertices || m.FaceCount < 4 || m.FaceCount > TerrainGeometry.MaxFaces)
            throw new InvalidDataException("지원하지 않거나 올바르지 않은 벡터 지형 형식입니다.");
        var points = new UnitVector[m.VertexCount]; var h = new float[m.VertexCount]; var q = new double[m.VertexCount];
        using (var stream = Entry(archive, "vertices.bin", 36L * m.VertexCount).Open())
        using (var r = new BinaryReader(stream))
            for (var i = 0; i < points.Length; i++) { points[i] = new(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()); h[i] = r.ReadSingle(); q[i] = r.ReadDouble(); }
        var faces = new TerrainFace[m.FaceCount];
        using (var stream = Entry(archive, "faces.i32", 12L * m.FaceCount).Open())
        using (var r = new BinaryReader(stream))
            for (var i = 0; i < faces.Length; i++) faces[i] = new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        var geometry = new TerrainGeometry(points, faces, q);
        if (!string.Equals(VectorFingerprint(geometry, h, m.Planet.RadiusMeters, m.SeaLevelMeters), m.TerrainSha256, StringComparison.Ordinal))
            throw new InvalidDataException("벡터 지형 검증에 실패했습니다. 파일이 손상되었을 수 있습니다.");
        // v2 surfaces were at most a few tens of km fine: locate on a 1024 cube (~10 km), then sample onto the 2048 one
        var coarse = new CubeTerrain(1024);
        Parallel.For(0, 6 * coarse.Size, row =>
        {
            var f = row / coarse.Size; var j = row % coarse.Size;
            for (var i = 0; i < coarse.Size; i++)
            {
                var l = geometry.Locate(coarse.CellDirection(f, i, j)); var face = geometry.Face(l.Face);
                coarse[f, i, j] = (float)(h[face.A] * l.A + h[face.B] * l.B + h[face.C] * l.C);
            }
        });
        var world = new World(m.Name, World.DefaultDisplayWidth, World.DefaultDisplayWidth / 2, 0, m.SeaLevelMeters, m.Planet);
        var terrain = world.Terrain;
        Parallel.For(0, 6 * terrain.Size, row =>
        {
            var f = row / terrain.Size; var j = row % terrain.Size; var target = terrain.Face(f);
            for (var i = 0; i < terrain.Size; i++) target[j * terrain.Size + i] = (float)coarse.Sample(terrain.CellDirection(f, i, j));
        });
        world.MarkAllDisplayDirty();
        return world;
    }
    private static ZipArchiveEntry Entry(ZipArchive zip, string name, long length)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException(name + " 데이터가 없습니다.");
        if (entry.Length != length) throw new InvalidDataException(name + " 데이터 길이가 올바르지 않습니다.");
        return entry;
    }

    /// <summary>
    /// The climate model's input: a v2 triangle surface (octahedral sphere, level 8: 262,146 points, ~39 km),
    /// heights sampled from the cube. TerrainSha256 is the triangle file's own checksum (the model verifies it);
    /// SourceTerrainSha256 is the cube world's fingerprint, which the model reports back so the app can match results.
    /// </summary>
    public static void SaveClimateInput(World world, string path, int level = 8)
    {
        var snapshot = world.Clone();
        var geometry = TerrainGeometry.CreateSphere(level);
        var h = new float[geometry.VertexCount];
        Parallel.For(0, h.Length, i => h[i] = (float)snapshot.Terrain.Sample(geometry.Vertex(i)));
        AtomicWrite(path, stream =>
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, true);
            var m = new WorldManifest(2, snapshot.Name, snapshot.Width, snapshot.Height, snapshot.SeaLevelMeters, snapshot.Planet, "m", VectorGrid,
                VectorFingerprint(geometry, h, snapshot.Planet.RadiusMeters, snapshot.SeaLevelMeters), "not-calculated",
                geometry.VertexCount, geometry.FaceCount, SourceTerrainSha256: snapshot.Fingerprint());
            using (var entry = archive.CreateEntry("world.json").Open()) JsonSerializer.Serialize(entry, m, JsonOptions);
            using (var entry = archive.CreateEntry("vertices.bin", CompressionLevel.Fastest).Open())
            using (var writer = new BinaryWriter(entry)) WriteVertices(writer, geometry, h);
            using (var entry = archive.CreateEntry("faces.i32", CompressionLevel.Fastest).Open())
            using (var writer = new BinaryWriter(entry)) WriteFaces(writer, geometry);
        });
    }
    public static Task SaveClimateInputAsync(World world, string path)
    {
        var snapshot = world.Clone();
        return Task.Run(() => SaveClimateInput(snapshot, path));
    }
    private static void WriteVertices(BinaryWriter writer, TerrainGeometry g, float[] h)
    {
        for (var i = 0; i < g.VertexCount; i++)
        { var p = g.Vertex(i); writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); writer.Write(h[i]); writer.Write(g.HeightWeight(i)); }
    }
    private static void WriteFaces(BinaryWriter writer, TerrainGeometry g)
    {
        for (var i = 0; i < g.FaceCount; i++) { var f = g.Face(i); writer.Write(f.A); writer.Write(f.B); writer.Write(f.C); }
    }
    /// <summary>The v2 checksum (also checked by tools/climate/vector_terrain.py).</summary>
    private static string VectorFingerprint(TerrainGeometry g, float[] h, double radius, float sea)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("garden-vector-terrain-v1\0"));
            writer.Write(radius); writer.Write(sea); writer.Write(g.VertexCount); writer.Write(g.FaceCount);
            WriteVertices(writer, g, h); WriteFaces(writer, g);
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))).ToLowerInvariant();
    }
}
