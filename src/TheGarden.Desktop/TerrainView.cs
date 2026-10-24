using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TheGarden.Core;

namespace TheGarden.Desktop;
public abstract class TerrainView : Grid
{
    public event Action<GeoPoint>? Pointed;     // click (mouse down): selects a point
    public event Action<GeoPoint>? Hovered;     // mouse move: readout only
    public event Action<GeoPoint>? StrokeStarted;
    public event Action<GeoPoint>? StrokeMoved;
    public event Action? StrokeFinished;
    public TerrainTool Tool { get; set; } = TerrainTool.Navigate;
    public double BrushRadiusKm { get; set; } = 650;
    public double PlanetRadiusMeters { get; set; } = PlanetSettings.Earth.RadiusMeters;
    public GeoPoint? Marker { get; set; }
    /// <summary>Under the mouse: the aim (every tool) and the brush circle (brush tools).</summary>
    public GeoPoint? BrushCenter { get; private set; }
    public bool ShowGrid { get; set; } = true;
    public double TextScale { get; set; } = 1;   // map margin labels follow the app's text size
    protected BitmapSource? Texture;
    private bool navigating;
    private bool drawing;
    private Point previous;
    protected TerrainView()
    {
        Background = Brushes.Transparent; ClipToBounds = true; Focusable = true;
    }
    // the live terrain bitmap redraws itself where it changed: only a different image needs a new render
    public virtual void SetTexture(BitmapSource texture) { if (ReferenceEquals(Texture, texture)) return; Texture = texture; InvalidateVisual(); }
    public abstract GeoPoint? Pick(Point point);
    protected abstract void Navigate(Vector delta);
    public abstract void ResetView();
    protected abstract void ZoomAt(double factor, Point point);
    public virtual void RefreshOverlay() => InvalidateVisual();
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Right) return;
        Focus(); previous = e.GetPosition(this);
        var p = Pick(previous);
        if (p.HasValue) Pointed?.Invoke(p.Value);
        navigating = e.ChangedButton == MouseButton.Right || Tool == TerrainTool.Navigate;
        drawing = !navigating && p.HasValue;
        if (drawing) StrokeStarted?.Invoke(p!.Value);
        if (navigating || drawing) CaptureMouse();
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var current = e.GetPosition(this);
        if (navigating) Navigate(current - previous);
        previous = current;
        var point = Pick(current);
        BrushCenter = point; RefreshOverlay();          // the aim follows the mouse with every tool
        if (point.HasValue)
        {
            Hovered?.Invoke(point.Value);
            if (drawing) StrokeMoved?.Invoke(point.Value);
        }
    }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (BrushCenter is not null && !drawing) { BrushCenter = null; RefreshOverlay(); }
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Right) return;
        EndInteraction(); e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e); EndInteraction();
    }
    public void EndInteraction()
    {
        var wasDrawing = drawing;
        drawing = navigating = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (wasDrawing) StrokeFinished?.Invoke();
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e); ZoomAt(e.Delta > 0 ? 1.12 : 1 / 1.12, e.GetPosition(this)); e.Handled = true;
    }
    protected IEnumerable<GeoPoint> BrushCircle()
    {
        if (Tool is TerrainTool.Navigate or TerrainTool.Pick || BrushCenter is not { } center) yield break;   // the eyedropper shows only the aim
        for (var i = 0; i <= 100; i++)
            yield return Geography.Destination(center, i * 2 * Math.PI / 100, BrushRadiusKm * 1000 / PlanetRadiusMeters);
    }
    internal static Pen MakePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness); pen.Freeze(); return pen;
    }
    internal static void DrawPath(DrawingContext dc, IEnumerable<Point?> points, Pen pen)
    {
        Point? last = null;
        foreach (var point in points)
        {
            if (point.HasValue && last.HasValue) dc.DrawLine(pen, last.Value, point.Value);
            last = point;
        }
    }
}
