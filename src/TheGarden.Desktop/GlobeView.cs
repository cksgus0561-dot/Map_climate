using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using TheGarden.Core;

namespace TheGarden.Desktop;
public sealed class GlobeView : TerrainView
{
    public GlobeCamera CameraModel { get; } = new();
    private readonly Viewport3D viewport = new() { IsHitTestVisible = false };
    private readonly GeometryModel3D sphere;
    private readonly OrthographicCamera camera = new();
    private readonly Overlay overlay;
    private readonly DirectionalLight sunlight = new(Colors.White, new Vector3D(-1, -.5, -2));
    private readonly AmbientLight ambient = new(Color.FromRgb(118, 126, 136));
    private readonly Backdrop backdrop;
    private bool space;
    /// <summary>The planet seen from space: dark sky with stars, a glowing atmosphere rim, a darker night side.</summary>
    public bool Space
    {
        get => space;
        set
        {
            if (space == value) return;
            space = value;
            ambient.Color = value ? Color.FromRgb(46, 50, 60) : Color.FromRgb(118, 126, 136);
            sunlight.Color = value ? Color.FromRgb(255, 250, 240) : Colors.White;
            backdrop.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            backdrop.InvalidateVisual(); overlay.InvalidateVisual();
        }
    }
    private double Radius => Math.Max(1, Math.Min(ActualWidth, ActualHeight) * .405 * CameraModel.Zoom);
    public GlobeView()
    {
        sphere = new GeometryModel3D(CreateSphere(), new DiffuseMaterial(Brushes.SlateGray));
        var group = new Model3DGroup();
        group.Children.Add(ambient);
        group.Children.Add(sunlight); group.Children.Add(sphere);
        viewport.Children.Add(new ModelVisual3D { Content = group });
        viewport.Camera = camera;
        backdrop = new Backdrop(this) { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Children.Add(backdrop);
        Children.Add(viewport);
        overlay = new Overlay(this) { IsHitTestVisible = false };
        Children.Add(overlay);
        SizeChanged += (_, _) => UpdateCamera();
        IsVisibleChanged += (_, _) => { if (IsVisible && pendingTexture) ApplyTexture(); };
    }
    public override void SetTexture(BitmapSource texture)
    {
        // the same live bitmap updates the sphere where it changed; a new material would upload the whole 4096 x 2048 image
        if (ReferenceEquals(Texture, texture) && !pendingTexture) return;
        base.SetTexture(texture);
        // a hidden globe (flat map view) takes the texture when it is shown: no 3D upload per brush step
        if (IsVisible) ApplyTexture(); else pendingTexture = true;
    }
    private bool pendingTexture;
    private void ApplyTexture()
    {
        pendingTexture = false;
        if (Texture is null) return;
        // the terrain texture is a live bitmap (redrawn in place while painting), so the brush stays unfrozen
        var brush = new ImageBrush(Texture) { ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.None };
        var material = new DiffuseMaterial(brush);
        if (Texture.IsFrozen) { brush.Freeze(); material.Freeze(); }   // freezing the brush would freeze a live bitmap too
        sphere.Material = material;
    }
    public override GeoPoint? Pick(Point point) => CameraModel.Pick((point.X - ActualWidth / 2) / Radius, (ActualHeight / 2 - point.Y) / Radius);
    protected override void Navigate(Vector delta)
    {
        CameraModel.Longitude = Geography.WrapLongitude(CameraModel.Longitude - delta.X / Radius * 70);
        CameraModel.Latitude = Math.Clamp(CameraModel.Latitude + delta.Y / Radius * 70, -90, 90);
        UpdateCamera();
    }
    public override void ResetView()
    { CameraModel.Longitude = 10; CameraModel.Latitude = 18; CameraModel.Zoom = 1; UpdateCamera(); }
    protected override void ZoomAt(double factor, Point point) { CameraModel.Zoom = Math.Clamp(CameraModel.Zoom * factor, .75, 2.7); UpdateCamera(); }
    public override void RefreshOverlay() => overlay.InvalidateVisual();
    private void UpdateCamera()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var (right, up, forward) = CameraModel.Basis();
        camera.Position = new(forward.X * 4, forward.Y * 4, forward.Z * 4);
        camera.LookDirection = new(-forward.X, -forward.Y, -forward.Z);
        camera.UpDirection = new(up.X, up.Y, up.Z);
        camera.Width = ActualWidth / Radius;
        camera.NearPlaneDistance = .1; camera.FarPlaneDistance = 10;
        var light = (forward * -2 + right * -.8 + up * -.6).Normalized();
        sunlight.Direction = new(light.X, light.Y, light.Z);
        overlay.InvalidateVisual(); backdrop.InvalidateVisual();
    }
    private static MeshGeometry3D CreateSphere()
    {
        const int columns = 192, rows = 96;
        var mesh = new MeshGeometry3D();
        for (var y = 0; y <= rows; y++)
        for (var x = 0; x <= columns; x++)
        {
            var p = new GeoPoint(90 - y * 180.0 / rows, x * 360.0 / columns - 180).ToVector();
            mesh.Positions.Add(new(p.X, p.Y, p.Z));
            mesh.Normals.Add(new(p.X, p.Y, p.Z));
            mesh.TextureCoordinates.Add(new(x / (double)columns, y / (double)rows));
        }
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < columns; x++)
        {
            var a = y * (columns + 1) + x; var c = a + columns + 1;
            mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(a + 1);
            mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(c + 1);
        }
        mesh.Freeze(); return mesh;
    }
    /// <summary>Behind the globe in space mode: night sky, a fixed star field and the glow of the atmosphere.</summary>
    private sealed class Backdrop(GlobeView owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            var sky = new LinearGradientBrush(Color.FromRgb(5, 7, 14), Color.FromRgb(12, 16, 30), 90); sky.Freeze();
            dc.DrawRoundedRectangle(sky, null, new Rect(0, 0, ActualWidth, ActualHeight), 14, 14);
            var random = new Random(20261021);
            var count = (int)(ActualWidth * ActualHeight / 1400);
            for (var i = 0; i < count; i++)
            {
                var x = random.NextDouble() * ActualWidth; var y = random.NextDouble() * ActualHeight;
                var bright = random.NextDouble(); var size = bright > .97 ? 1.3 : bright > .85 ? .9 : .55;
                var star = new SolidColorBrush(Color.FromArgb((byte)(70 + 170 * bright), 235, 238, 255)); star.Freeze();
                dc.DrawEllipse(star, null, new Point(x, y), size, size);
            }
            var center = new Point(ActualWidth / 2, ActualHeight / 2); var r = owner.Radius; var outer = r * 1.10;
            var glow = new RadialGradientBrush { GradientStops = { new(Color.FromArgb(210, 120, 175, 255), r / outer), new(Color.FromArgb(70, 80, 140, 255), (r / outer + 1) / 2), new(Color.FromArgb(0, 40, 80, 200), 1) } };
            glow.Freeze(); dc.DrawEllipse(glow, null, center, outer, outer);
        }
    }
    private sealed class Overlay(GlobeView owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            var radius = owner.Radius;
            if (owner.Space)
            {
                // thin haze of air seen edge-on near the limb
                var haze = new RadialGradientBrush { GradientStops = { new(Color.FromArgb(0, 150, 195, 255), .80), new(Color.FromArgb(70, 150, 195, 255), .96), new(Color.FromArgb(150, 190, 220, 255), 1) } };
                haze.Freeze(); dc.DrawEllipse(haze, null, center, radius, radius);
            }
            else
            {
                dc.DrawEllipse(null, MakePen(Color.FromArgb(46, 115, 173, 204), 8), center, radius + 3, radius + 3);
                dc.DrawEllipse(null, MakePen(Color.FromArgb(85, 139, 190, 215), 1), center, radius + 1, radius + 1);
            }
            Point? Project(GeoPoint point)
            {
                var (x, y, visible) = owner.CameraModel.Project(point);
                return visible ? new Point(center.X + x * radius, center.Y - y * radius) : null;
            }
            if (owner.ShowGrid && !owner.Space)   // no map lines on the planet seen from space
            {
                var pen = MakePen(Color.FromArgb(50, 207, 229, 232), .65);
                for (var lat = -60; lat <= 60; lat += 30)
                    DrawPath(dc, Enumerable.Range(0, 181).Select(i => Project(new GeoPoint(lat, -180 + i * 2))), pen);
                for (var lon = -180; lon < 180; lon += 30)
                    DrawPath(dc, Enumerable.Range(0, 91).Select(i => Project(new GeoPoint(-90 + i * 2, lon))), pen);
            }
            DrawPath(dc, owner.BrushCircle().Select(Project), MakePen(Color.FromArgb(235, 229, 244, 215), 1.4));
            // the aim follows the mouse with every tool; the selected place is a small dot while looking around
            if (owner.Tool == TerrainTool.Navigate && owner.Marker is { } selected && Project(selected) is { } spot) MapView.DrawSelection(dc, spot);
            if (owner.BrushCenter is { } at && Project(at) is { } point) MapView.DrawMarker(dc, point);
        }
    }
}
