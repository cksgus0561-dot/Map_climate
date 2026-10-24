using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TheGarden.Core;

namespace TheGarden.Desktop;
/// <summary>
/// The terrain colour map in the world's display frame. One live bitmap per world: an edit redraws only the
/// pixels the world reports as changed (World.TakeDisplayDirty), so painting stays fast on the large raster.
/// </summary>
internal static class TerrainTexture
{
    private static readonly (double Height, double R, double G, double B)[] Stops =
    [(-6000,18,44,73), (-2000,41,91,117), (-30,112,155,160), (0,182,198,160),
     (700,139,168,132), (1800,114,139,110), (3000,164,144,102), (6000,224,197,160), (12000,237,218,190)];

    private sealed class Cache
    {
        public required float[] Heights;
        public required byte[] Pixels;
        public required WriteableBitmap Bitmap;
        public bool Shade;
    }
    private static readonly ConditionalWeakTable<World, Cache> Caches = new();

    public static BitmapSource Build(World world, bool shade)
    {
        var width = world.Width; var height = world.Height;
        var dirty = world.TakeDisplayDirty();
        if (!Caches.TryGetValue(world, out var cache))
        {
            cache = new Cache { Heights = new float[width * height], Pixels = new byte[width * height * 4],
                Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null), Shade = shade };
            Caches.Add(world, cache);
            dirty = (0, 0, width - 1, height - 1);
        }
        if (cache.Shade != shade) { cache.Shade = shade; dirty = (0, 0, width - 1, height - 1); }
        if (cache.Bitmap.IsFrozen)
        {
            // something froze the shown bitmap (an image derived from it and frozen): continue on a fresh one
            cache.Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            cache.Bitmap.WritePixels(new Int32Rect(0, 0, width, height), cache.Pixels, width * 4, 0, 0);
        }
        if (dirty is { } d)
        {
            world.FillDisplayElevations(cache.Heights, d.X0, d.Y0, d.X1, d.Y1);
            // shading reads the neighbours: colour one pixel beyond the refreshed heights
            var x0 = Math.Max(0, d.X0 - 1); var y0 = Math.Max(0, d.Y0 - 1); var x1 = Math.Min(width - 1, d.X1 + 1); var y1 = Math.Min(height - 1, d.Y1 + 1);
            Colour(world, cache, x0, y0, x1, y1);
            cache.Bitmap.WritePixels(new Int32Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1), cache.Pixels, width * 4, x0, y0);
        }
        return cache.Bitmap;
    }

    private static void Colour(World world, Cache cache, int x0, int y0, int x1, int y1)
    {
        var width = world.Width; var height = world.Height; var heights = cache.Heights; var bytes = cache.Pixels; var shade = cache.Shade;
        var sea = world.SeaLevelMeters; var metersPerPixel = world.Planet.RadiusMeters * Math.PI / height;
        Parallel.For(y0, y1 + 1, y =>
        {
            var latitude = world.DisplayCoordinate(0, y).Latitude * Geography.DegreesToRadians;
            var distanceX = Math.Max(.08, Math.Cos(latitude));
            for (var x = x0; x <= x1; x++)
            {
                var h = heights[y * width + x] - sea;
                var stop = 1;
                while (stop < Stops.Length - 1 && h > Stops[stop].Height) stop++;
                var a = Stops[stop - 1]; var b = Stops[stop];
                var t = Math.Clamp((h - a.Height) / (b.Height - a.Height), 0, 1);
                var illumination = shade ? Light(heights, width, height, x, y, distanceX, metersPerPixel) : 1;
                var offset = (y * width + x) * 4;
                bytes[offset] = (byte)Math.Clamp((a.B + (b.B - a.B) * t) * illumination, 0, 255);
                bytes[offset + 1] = (byte)Math.Clamp((a.G + (b.G - a.G) * t) * illumination, 0, 255);
                bytes[offset + 2] = (byte)Math.Clamp((a.R + (b.R - a.R) * t) * illumination, 0, 255);
                bytes[offset + 3] = 255;
            }
        });
    }

    // relief shading from the height differences of the four neighbours, scaled by the pixel's ground distance
    // (the factor 6000 m per 39 km pixel of the original 1024-wide map keeps the same look at any display size)
    private static float Light(float[] heights, int width, int height, int x, int y, double distanceX, double metersPerPixel)
    {
        var left = heights[y * width + (x + width - 1) % width]; var right = heights[y * width + (x + 1) % width];
        var top = heights[Math.Max(0, y - 1) * width + x]; var bottom = heights[Math.Min(height - 1, y + 1) * width + x];
        var scale = 6000 * metersPerPixel / 39_000;
        return (float)Math.Clamp(1 + ((right - left) / distanceX + (bottom - top)) / scale, .64, 1.18);
    }

    /// <summary>The same relief shading factor as Build, per display pixel (for climate layers).</summary>
    public static float[] Illumination(World world)
    {
        Build(world, true);
        var cache = Caches.TryGetValue(world, out var c) ? c : throw new InvalidOperationException();
        var width = world.Width; var height = world.Height; var light = new float[width * height];
        var metersPerPixel = world.Planet.RadiusMeters * Math.PI / height;
        Parallel.For(0, height, y =>
        {
            var distanceX = Math.Max(.08, Math.Cos(world.DisplayCoordinate(0, y).Latitude * Geography.DegreesToRadians));
            for (var x = 0; x < width; x++) light[y * width + x] = Light(cache.Heights, width, height, x, y, distanceX, metersPerPixel);
        });
        return light;
    }
}
