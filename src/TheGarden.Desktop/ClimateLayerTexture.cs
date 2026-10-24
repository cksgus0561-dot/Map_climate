using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TheGarden.Core;

namespace TheGarden.Desktop;

/// <summary>Lays a climate layer image (RGBA, display frame) over the terrain texture, keeping the relief shading.</summary>
internal static class ClimateLayerTexture
{
    public static BitmapSource LoadImage(string path)
    {
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(Path.GetFullPath(path)); image.EndInit(); image.Freeze();
        return image;
    }

    public static BitmapSource Compose(BitmapSource terrain, World world, string layerPath, bool shade)
    {
        BitmapSource layer = new FormatConvertedBitmap(LoadImage(layerPath), PixelFormats.Bgra32, null, 0);
        var width = terrain.PixelWidth; var height = terrain.PixelHeight;
        if (layer.PixelWidth != width || layer.PixelHeight != height)
            layer = new FormatConvertedBitmap(new TransformedBitmap(layer, new ScaleTransform(width / (double)layer.PixelWidth, height / (double)layer.PixelHeight)), PixelFormats.Bgra32, null, 0);
        var basePixels = new byte[width * height * 4]; new FormatConvertedBitmap(terrain, PixelFormats.Bgra32, null, 0).CopyPixels(basePixels, width * 4, 0);
        var top = new byte[width * height * 4]; layer.CopyPixels(top, width * 4, 0);
        var light = shade ? TerrainTexture.Illumination(world) : null;
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4; var a = top[o + 3] / 255.0;
                if (a <= 0) continue;
                var l = light is null ? 1.0 : light[y * width + x];
                for (var k = 0; k < 3; k++)
                    basePixels[o + k] = (byte)Math.Clamp(basePixels[o + k] * (1 - a) + top[o + k] * l * a, 0, 255);
            }
        });
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, basePixels, width * 4);
        result.Freeze(); return result;
    }
}
