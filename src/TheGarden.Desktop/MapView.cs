using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TheGarden.Core;

namespace TheGarden.Desktop;
public sealed class MapView : TerrainView
{
    private double zoom = 1;
    private double panX, panY;
    private Rect MapRect
    {
        get
        {
            var width = Math.Max(1, Math.Min(ActualWidth - 24, (ActualHeight - 60) * 2)) * zoom;
            return new((ActualWidth - width) / 2 + panX, (ActualHeight - width / 2) / 2 + panY, width, width / 2);
        }
    }
    public override GeoPoint? Pick(Point point)
    {
        var r = MapRect;
        if (point.X < 0 || point.X > ActualWidth || point.Y < r.Top || point.Y > r.Bottom) return null;
        return Geography.FromMap((point.X - r.Left) / r.Width, (point.Y - r.Top) / r.Height);
    }
    protected override void Navigate(Vector delta)
    {
        var r = MapRect;
        panX = (panX + delta.X) % r.Width;
        panY = Math.Clamp(panY + delta.Y, -Math.Max(0, (r.Height - ActualHeight + 60) / 2), Math.Max(0, (r.Height - ActualHeight + 60) / 2));
        InvalidateVisual();
    }
    public override void ResetView() { zoom = 1; panX = panY = 0; InvalidateVisual(); }
    protected override void ZoomAt(double factor, Point point)
    {
        var oldZoom = zoom;
        zoom = Math.Clamp(zoom * factor, 1, 8);
        var ratio = zoom / oldZoom;
        panX = point.X - ActualWidth / 2 - (point.X - ActualWidth / 2 - panX) * ratio;
        panY = point.Y - ActualHeight / 2 - (point.Y - ActualHeight / 2 - panY) * ratio;
        Navigate(new Vector());
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        var r = MapRect;
        var gridPen = MakePen(Color.FromArgb(60, 174, 207, 222), .65);
        var strongPen = MakePen(Color.FromArgb(110, 177, 215, 212), .9);
        for (var tile = -2; tile <= 2; tile++)
        {
            var rect = new Rect(r.X + tile * r.Width, r.Y, r.Width, r.Height);
            if (rect.Right < 0 || rect.Left > ActualWidth) continue;
            if (Texture is not null) dc.DrawImage(Texture, rect);
            if (!ShowGrid) continue;
            for (var lat = -90; lat <= 90; lat += 30)
            {
                var y = rect.Top + (90 - lat) / 180.0 * rect.Height;
                dc.DrawLine(lat == 0 ? strongPen : gridPen, new(rect.Left, y), new(rect.Right, y));
            }
            for (var lon = -180; lon <= 180; lon += 30)
            {
                var x = rect.Left + (lon + 180) / 360.0 * rect.Width;
                dc.DrawLine(lon == 0 ? strongPen : gridPen, new(x, rect.Top), new(x, rect.Bottom));
            }
        }
        if (zoom <= 1.01)
        {
            var k = TextScale;
            Label(dc, "90° N", 3, r.Top - 23 * k);
            Label(dc, "90° S", 3, r.Bottom + 26 * k);
            Label(dc, "180° W", Math.Max(0, r.Left), r.Bottom + 10 * k);
            Label(dc, "0°", ActualWidth / 2 - 8 * k, r.Bottom + 10 * k);
            Label(dc, "180° E", ActualWidth - 46 * k, r.Bottom + 10 * k);
        }
        if (BrushCenter is { } brush)
        {
            var (u, bv0) = Geography.ToMap(brush);
            var circle = BrushCircle().Select(Geography.ToMap).ToArray();
            var pen = MakePen(Color.FromArgb(235, 229, 244, 215), 1.4);
            for (var tile = -2; tile <= 2; tile++)
            {
                var centerX = r.Left + (u + tile) * r.Width;
                Point? previous = null;
                foreach (var (bu, bv) in circle)
                {
                    // Unwrap around brush centre to avoid a line across the entire map.
                    var du = bu - u; du -= Math.Round(du);
                    var point = new Point(centerX + du * r.Width, r.Top + bv * r.Height);
                    if (previous.HasValue && Math.Abs(point.X - previous.Value.X) < r.Width / 2)
                        dc.DrawLine(pen, previous.Value, point);
                    previous = point;
                }
                DrawMarker(dc, new Point(centerX, r.Top + bv0 * r.Height));   // the aim follows the mouse
            }
        }
        // the selected (clicked) place, while looking around: a small dot, so it never reads as the aim
        if (Marker is { } marker && Tool == TerrainTool.Navigate)
        {
            var (u, v) = Geography.ToMap(marker);
            for (var tile = -2; tile <= 2; tile++) DrawSelection(dc, new Point(r.Left + (u + tile) * r.Width, r.Top + v * r.Height));
        }
        dc.Pop();
    }
    private void Label(DrawingContext dc, string text, double x, double y)
    {
        var value = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9.5 * TextScale, TryFindResource("Muted") as Brush ?? new SolidColorBrush(Color.FromRgb(110, 104, 92)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(value, new(x, y));
    }
    internal static void DrawSelection(DrawingContext dc, Point p)
    {
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(222, 145, 84)), MakePen(Color.FromRgb(255, 255, 255), 1.2), p, 3.5, 3.5);
    }
    internal static void DrawMarker(DrawingContext dc, Point p)
    {
        var pen = MakePen(Color.FromRgb(223, 243, 210), 1.25);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 12, 25, 34)), pen, p, 4, 4);
        dc.DrawLine(pen, new(p.X - 8, p.Y), new(p.X - 5, p.Y));
        dc.DrawLine(pen, new(p.X + 5, p.Y), new(p.X + 8, p.Y));
        dc.DrawLine(pen, new(p.X, p.Y - 8), new(p.X, p.Y - 5));
        dc.DrawLine(pen, new(p.X, p.Y + 5), new(p.X, p.Y + 8));
    }
}
