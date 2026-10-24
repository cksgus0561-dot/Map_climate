using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TheGarden.Desktop;

/// <summary>Small climate diagram: rain as bars (mm per month) and temperature as a line, four seasons.</summary>
public sealed class Climograph : FrameworkElement
{
    private static readonly string[] Seasons = ["3–5월", "6–8월", "9–11월", "12–2월"];
    private double[]? temperature, precipitation;

    public void Show(double[]? temperatureC, double[]? precipitationMmDay)
    {
        temperature = temperatureC; precipitation = precipitationMmDay; InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 300 : available.Width, 132);

    protected override void OnRender(DrawingContext dc)
    {
        if (temperature is not { Length: 4 } t || precipitation is not { Length: 4 } p) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(new FontFamily("Noto Sans KR, Malgun Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Brush Theme(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
        var ink = Theme("Muted", Color.FromRgb(0x7B, 0x74, 0x66)); var rule = new Pen(Theme("Rule", Color.FromRgb(0xDD, 0xD4, 0xC3)), 1);
        var rain = new SolidColorBrush(Color.FromRgb(0x5E, 0x8F, 0xB0)); var heat = new Pen(Theme("Ochre", Color.FromRgb(0xB8, 0x68, 0x2C)), 2.2);
        var dot = Theme("Card", Colors.White);
        FormattedText Text(string s, double size = 10) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, ink, dpi);

        double left = 34, right = ActualWidth - 34, top = 8, bottom = ActualHeight - 22, w = right - left, h = bottom - top;
        var monthly = p.Select(x => x * 30.44).ToArray();
        var rainMax = Math.Max(100, Math.Ceiling(monthly.Max() / 50) * 50);
        var tMin = Math.Min(-10, Math.Floor((t.Min() - 3) / 10) * 10); var tMax = Math.Max(30, Math.Ceiling((t.Max() + 3) / 10) * 10);
        dc.DrawLine(rule, new Point(left, bottom), new Point(right, bottom));
        if (tMin < 0 && tMax > 0) { var y0 = bottom - (0 - tMin) / (tMax - tMin) * h; dc.DrawLine(new Pen(rule.Brush, .8) { DashStyle = DashStyles.Dash }, new Point(left, y0), new Point(right, y0)); }
        var slot = w / 4; var pts = new Point[4];
        for (var i = 0; i < 4; i++)
        {
            var x0 = left + i * slot; var bh = monthly[i] / rainMax * h;
            dc.DrawRoundedRectangle(rain, null, new Rect(x0 + slot * .22, bottom - bh, slot * .56, bh), 2, 2);
            pts[i] = new Point(x0 + slot / 2, bottom - (t[i] - tMin) / (tMax - tMin) * h);
            var label = Text(Seasons[i]); dc.DrawText(label, new Point(x0 + slot / 2 - label.Width / 2, bottom + 4));
        }
        var geo = new StreamGeometry();
        using (var g = geo.Open()) { g.BeginFigure(pts[0], false, false); g.PolyLineTo(pts.Skip(1).ToArray(), true, true); }
        dc.DrawGeometry(null, heat, geo);
        foreach (var q in pts) dc.DrawEllipse(dot, heat, q, 3, 3);
        dc.DrawText(Text($"{tMax:F0}°"), new Point(4, top - 2)); dc.DrawText(Text($"{tMin:F0}°"), new Point(4, bottom - 12));
        var mm = Text($"{rainMax:F0}mm"); dc.DrawText(mm, new Point(ActualWidth - mm.Width - 2, top - 2));
        var zero = Text("0"); dc.DrawText(zero, new Point(ActualWidth - zero.Width - 2, bottom - 12));
    }
}
