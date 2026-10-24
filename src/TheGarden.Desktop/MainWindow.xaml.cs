using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TheGarden.Core;
using TheGarden.Core.Climate;

namespace TheGarden.Desktop;

/// <summary>
/// The Garden main window ("atlas" layout): the map fills the window; small cards float on it.
/// Two modes: making terrain (brush palette) and reading the climate (layer dock, inspector).
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>A map theme in the climate dock; seasons are alternative layers of the same theme.</summary>
    private sealed record Theme(string Key, string Title, string[] LayerIds);
    private static readonly Theme[] Themes =
    [
        new("off", "지형만", []),
        new("satellite", "우주에서 본 모습", ["satellite_spring", "satellite_summer", "satellite_autumn", "satellite_winter"]),
        new("temperature", "기온", ["temperature_annual", "temperature_summer", "temperature_winter"]),
        new("precipitation", "강수", ["precipitation_annual", "precipitation_summer", "precipitation_winter"]),
        new("koppen", "기후형", ["koppen"]),
        new("biome", "생물군계", ["biome"]),
        new("wind", "바람·기압", ["wind_summer", "wind_winter"]),
        new("currents", "해류", ["currents"]),
        new("hydro", "강·호수·빙하", ["hydro"]),
        new("snow_ice", "눈·얼음", ["snow_ice"]),
    ];
    private static int? SeasonOf(string id) => id.EndsWith("_spring") ? 0 : id.EndsWith("_summer") ? 1 : id.EndsWith("_autumn") ? 2 : id.EndsWith("_winter") ? 3 : null;
    private static string SeasonLabel(string id) => SeasonOf(id) switch { 0 => "3–5월", 1 => "6–8월", 2 => "9–11월", 3 => "12–2월", _ => "연평균" };

    // terrain editing
    private World? world;
    private readonly EditHistory history = new();
    private TerrainStroke? stroke;
    // The stroke's stamps run on a worker so a fast drag never holds up the mouse and the redraw: the mouse moves queue
    // here in order, the worker stamps along them, and the display picks up what is done every frame.
    private readonly Queue<GeoPoint> strokePoints = new();
    private readonly object strokeGate = new();
    private Task strokeWork = Task.CompletedTask;
    private bool strokeRunning;
    private volatile bool strokePainted;
    private volatile Exception? strokeError;
    private TerrainTool tool = TerrainTool.Navigate;
    private GeoPoint? selected;
    private string? currentPath;
    private bool dirty, textureDirty, statsDirty, initialized, busy, allowClose, closingInProgress;
    private readonly DispatcherTimer refreshTimer;
    private double targetElevation = 1500;
    private bool targetValid = true;
    private TerrainTool pickReturn = TerrainTool.Flatten;   // the height tool the eyedropper hands its value back to
    private BitmapSource? shownTexture;
    private DateTime messageUntil;
    // climate
    private readonly string? atlasRepository = WorldClimateJob.FindRepository();
    private readonly DispatcherTimer climateTimer;
    private WorldClimateJob? atlasJob;
    private WorldClimateAtlas? atlas;
    private int atlasState, seasonIndex;
    private Theme theme = Themes[0];
    private WorldClimateLayer? atlasLayer;
    private string terrainHash = "";
    private bool fillingClimateLists, rerunRequested, legendFolded;
    private Dictionary<string, string> koppenColors = new();
    private double uiScale = 1;
    // "100%" is the comfortable default: the cards and labels are drawn 1.2x their design size, the steps are relative to that
    private const double BaseScale = 1.2;
    private static readonly double[] UiScales = [.8, .9, 1, 1.1, 1.2, 1.3];
    private double UiK => BaseScale * uiScale;
    private bool ClimateRunning => atlasJob is { IsFinished: false };
    private bool AtlasMatches => atlas is not null && atlas.Matches(terrainHash);
    private bool ClimateMode => ModeClimate.IsChecked == true;

    public MainWindow()
    {
        var settings = AppSettings.Load();
        if (Application.Current is not null) ThemeManager.Apply(settings.Dark ?? ThemeManager.SystemPrefersDark());
        InitializeComponent();
        foreach (var view in new TerrainView[] { Globe, Map })
        {
            view.Pointed += PointAt; view.Hovered += HoverAt;
            view.StrokeStarted += BeginStroke; view.StrokeMoved += ContinueStroke; view.StrokeFinished += FinishStroke;
        }
        // Render priority (above input): at Background a brush drag's mouse moves starved the redraw until the mouse paused;
        // every frame, so the paint keeps up with the brush
        refreshTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        refreshTimer.Tick += (_, _) => RefreshTerrain();
        refreshTimer.Start();
        climateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        climateTimer.Tick += (_, _) => { atlasJob?.Refresh(); TakeFinishedAtlas(); UpdateClimatePanel(); HideExpiredNotice(); };
        climateTimer.Start();
        initialized = true;
        SetTextScale(settings.TextScale, save: false);
        UpdateThemeButton();
        BrushChanged(this, new RoutedPropertyChangedEventArgs<double>(0, 0));
        Loaded += async (_, _) =>
        {
            var path = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
            if (path is not null) await WorkAsync(() => WorldFile.Load(path), path, "프로젝트를 불러왔습니다.");
            else await WorkAsync(() => TerrainExamples.Create(), null, "예제 지형입니다. 왼쪽 붓으로 지형을 빚고, 위에서 ‘기후 보기’로 넘어가 보세요.");
        };
    }

    // notices go to their own chip under the mode switch for 4 s; the bottom chip is only the place readout
    private void Notify(string message)
    {
        ToastText.Text = message; ToastChip.Visibility = Visibility.Visible; messageUntil = DateTime.Now.AddSeconds(4);
    }
    private void HideExpiredNotice() { if (ToastChip.Visibility == Visibility.Visible && DateTime.Now >= messageUntil) ToastChip.Visibility = Visibility.Collapsed; }

    // ======================================================================== world and terrain
    private void SetWorld(World value, string? path)
    {
        strokeWork.Wait(); strokeError = null; strokePainted = false;
        world = value; currentPath = path; dirty = false; selected = null; rerunRequested = false;
        stroke = null; history.Clear(); atlasJob = null;
        terrainHash = world.Fingerprint();
        Globe.Marker = Map.Marker = null;
        Globe.PlanetRadiusMeters = Map.PlanetRadiusMeters = world.Planet.RadiusMeters;
        Globe.ResetView(); Map.ResetView();
        Inspector.Visibility = Visibility.Collapsed;
        textureDirty = statsDirty = true;
        RefreshTerrain();
        SetAtlas(atlasRepository is null ? null
            : WorldClimateAtlas.ReadArchive(WorldClimateJob.ArchiveRoot(atlasRepository)).FirstOrDefault(a => a.Matches(terrainHash)));
        UpdateDocumentState();
    }
    // a place is picked only in the climate view (its climate card); the terrain view has no use for a selection
    private void PointAt(GeoPoint point)
    {
        if (world is null || busy || !ClimateMode) return;
        selected = point; Globe.Marker = Map.Marker = point;
        Globe.RefreshOverlay(); Map.RefreshOverlay(); UpdatePoint();
    }
    /// <summary>Back to no picked place: the card closes, the dot goes, and nothing opens the card again until the next click.</summary>
    private void ClearSelection()
    {
        selected = null; Globe.Marker = Map.Marker = null;
        Globe.RefreshOverlay(); Map.RefreshOverlay();
        Inspector.Visibility = Visibility.Collapsed; UpdateMapFrame();
    }
    private void HoverAt(GeoPoint p)
    {
        if (world is null || busy) return;        // the readout stays on while painting and while a notice shows
        var h = world.Sample(p);
        var text = $"{Math.Abs(p.Latitude):F1}° {(p.Latitude >= 0 ? "N" : "S")}  {Math.Abs(p.Longitude):F1}° {(p.Longitude >= 0 ? "E" : "W")}   ·   {(h >= world.SeaLevelMeters ? $"해발 {h:N0} m" : $"수심 {-h:N0} m")}";
        if (ClimateMode && atlas is not null && AtlasMatches && atlasLayer is not null && LayerValue(atlas.Nearest(atlasState, p)) is { } v) text += "   ·   " + v;
        StatusText.Text = text;
    }
    private string? LayerValue(WorldClimateCell c)
    {
        var id = atlasLayer!.Id; var season = SeasonOf(id);
        return theme.Key switch
        {
            "temperature" => $"{(season is { } s ? c.TemperatureC[s] : c.TemperatureC.Average()):F1} °C",
            "precipitation" => season is { } s ? $"{c.PrecipitationMmDay[s] * 30.44:F0} mm/월" : $"{c.PrecipitationMmDay.Average() * 365.24:F0} mm/년",
            "koppen" or "biome" => c.LandFraction > .5 ? $"{c.Koppen} {c.KoppenName} · {c.Biome}" : "바다",
            "satellite" => (c.LandFraction > .5 ? $"{c.Biome}" : c.SeaIce > .15 ? "바다 · 해빙이 어는 바다" : "바다") + (season is { } s2 ? $" · {c.TemperatureC[s2]:F0} °C" : ""),
            "snow_ice" => c.LandFraction > .5 ? (c.Glacier ? "빙하" : $"눈 덮이는 계절 {c.SnowSeasons}개") : c.SeaIce > .15 ? $"해빙 최대 {c.SeaIce:P0}" : "얼지 않는 바다",
            _ => null,
        };
    }
    private void UpdatePoint()
    {
        if (world is null || selected is not { } p) return;
        CoordinateText.Text = $"{p.Latitude:F2}, {p.Longitude:F2}";
        var height = world.Sample(p);
        ElevationText.Text = $"{height:N0} m";
        UpdatePointClimate();
    }
    private void BeginStroke(GeoPoint point)
    {
        if (world is null || busy) return;
        if (tool == TerrainTool.Pick)
        {
            var height = Math.Round(world.Sample(point));
            TargetBox.Text = height.ToString("0", CultureInfo.CurrentCulture);
            (pickReturn == TerrainTool.Ridge ? RidgeTool : FlattenTool).IsChecked = true;
            Notify($"고도 {height:N0} m를 가져왔습니다.");
            return;
        }
        if (tool is TerrainTool.Flatten or TerrainTool.Ridge && !targetValid) { Notify("높이 칸에 −12,000 ~ 12,000 사이의 숫자를 넣으세요."); return; }
        FinishStroke();
        stroke = new TerrainStroke(world, new BrushSettings(tool, RadiusSlider.Value, targetElevation, SoftnessSlider.Value / 100, RoughnessSlider.Value / 100));
        ApplyStroke(point);
    }
    private void ContinueStroke(GeoPoint point)
    {
        if (stroke is null || busy) return;
        ApplyStroke(point);
    }
    private void ApplyStroke(GeoPoint point)
    {
        if (strokeError is not null) return;
        lock (strokeGate)
        {
            strokePoints.Enqueue(point);
            if (strokeRunning) return;
            strokeRunning = true;
            var current = stroke!;
            strokeWork = Task.Run(() => PaintQueued(current));
        }
    }
    private void PaintQueued(TerrainStroke current)
    {
        while (true)
        {
            GeoPoint point;
            lock (strokeGate)
            {
                if (strokePoints.Count == 0) { strokeRunning = false; return; }
                point = strokePoints.Dequeue();
            }
            try { current.MoveTo(point); strokePainted = true; }
            catch (Exception ex)
            {
                lock (strokeGate) { strokePoints.Clear(); strokeRunning = false; }
                strokeError = ex; return;
            }
        }
    }
    /// <summary>Waits for the queued stamps; a failed stroke is undone. True when the stroke is still there.</summary>
    private bool SettleStroke()
    {
        strokeWork.Wait();
        if (strokeError is not { } ex) return stroke is not null;
        strokeError = null;
        stroke?.Cancel(); stroke = null; textureDirty = statsDirty = true;
        Notify((ex is InvalidOperationException ? ex.Message : "붓질을 반영하지 못했습니다.") + " 이번 붓질은 취소했습니다.");
        return false;
    }
    private void FinishStroke()
    {
        if (!SettleStroke()) return;
        var command = stroke!.Complete(); stroke = null;
        if (command.ChangedCells == 0) return;
        history.Push(command); dirty = true; textureDirty = statsDirty = true;
        UpdateDocumentState();
    }
    private BitmapSource ComposeLayer(BitmapSource terrain, WorldClimateLayer layer) =>
        ClimateLayerTexture.Compose(terrain, world!, atlas!.LayerPath(atlasState, layer), ShadeToggle.IsChecked == true);
    private void RefreshTerrain()
    {
        if (world is null) return;
        if (strokeError is not null) SettleStroke();
        if (strokePainted) { strokePainted = false; textureDirty = true; }
        if (textureDirty)
        {
            var texture = TerrainTexture.Build(world, ShadeToggle.IsChecked == true);
            if (atlas is not null && atlasLayer is not null && AtlasMatches && ClimateMode)
            {
                try
                {
                    texture = ComposeLayer(texture, atlasLayer);
                    if (CloudLayer() is { } clouds && CloudToggle.IsChecked == true)
                        texture = ClimateLayerTexture.Compose(texture, world, atlas.LayerPath(atlasState, clouds), false);
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
                { Notify("기후 층 그림을 읽지 못했습니다: " + ex.Message); atlasLayer = null; }
            }
            Globe.Space = ClimateMode && theme.Key == "satellite" && atlasLayer is not null && AtlasMatches;
            shownTexture = texture; Map.SetTexture(texture); Globe.SetTexture(texture); textureDirty = false;
            UpdatePoint(); UpdateLegend();
        }
        if (statsDirty && !statsRunning)
        {
            // the land share reads the whole planet (tens of ms): off the UI thread, so letting go of the brush never stalls
            statsDirty = false; statsRunning = true;
            ResolutionText.Text = $"격자 지형 {world.Terrain.CellCount:N0}칸 · 간격 약 {world.SpacingKm:F1} km · 지구 크기 행성";
            var measured = world;
            Task.Run(measured.Statistics).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                statsRunning = false;
                if (!t.IsCompletedSuccessfully || !ReferenceEquals(measured, world)) { statsDirty = true; return; }
                var stats = t.Result;
                LandText.Text = $"육지 {stats.LandFraction:P1} · 최저 {stats.MinimumMeters:N0} m · 최고 {stats.MaximumMeters:N0} m";
            }));
        }
    }
    private bool statsRunning;
    private void UpdateDocumentState()
    {
        if (world is null) return;
        terrainHash = world.Fingerprint();
        WorldTitle.Text = world.Name;
        Title = $"{world.Name}{(dirty ? " *" : "")} — 세계 지도";
        SaveState.Text = currentPath is null ? "아직 저장하지 않음" : (dirty ? "저장하지 않은 변경 있음" : "저장됨") + " · " + Path.GetFileName(currentPath);
        SaveState.ToolTip = currentPath;
        UndoButton.IsEnabled = history.UndoCount > 0;
        RedoButton.IsEnabled = history.RedoCount > 0;
        UpdateClimatePanel(); UpdateLegend(); UpdatePointClimate();
    }

    // ======================================================================== chrome: mode, view, tools
    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        Globe.EndInteraction(); Map.EndInteraction();
        ClearSelection();                                     // each view starts without a picked place
        TerrainLayer.Visibility = ClimateMode ? Visibility.Collapsed : Visibility.Visible;
        ClimateLayer.Visibility = ClimateMode ? Visibility.Visible : Visibility.Collapsed;
        if (ClimateMode) NavigateTool.IsChecked = true;      // clicks pick places in the climate view
        textureDirty = true; UpdateClimatePanel(); UpdatePointClimate(); UpdateLegend(); UpdateMapFrame();
    }
    // ---- text size: the overlay (all cards) is scaled as one; the map makes room for it
    private void SetTextScale(double value, bool save = true)
    {
        uiScale = UiScales.MinBy(s => Math.Abs(s - value));
        OverlayScale.ScaleX = OverlayScale.ScaleY = UiK;
        WorldMenuSheet.LayoutTransform = TextSizeSheet.LayoutTransform = new ScaleTransform(UiK, UiK);
        Map.TextScale = Globe.TextScale = UiK; Map.InvalidateVisual();
        TextSizeLabel.Text = $"{uiScale:P0}";
        TextSmallerButton.IsEnabled = uiScale > UiScales[0]; TextLargerButton.IsEnabled = uiScale < UiScales[^1];
        UpdateMapFrame();
        if (save) { var st = AppSettings.Load(); st.TextScale = uiScale; AppSettings.Save(st); Notify($"글자 크기 {uiScale:P0}"); }
    }
    private void StepTextScale(int step) => SetTextScale(UiScales[Math.Clamp(Array.IndexOf(UiScales, uiScale) + step, 0, UiScales.Length - 1)]);
    private void TextSize_Click(object sender, RoutedEventArgs e) => TextSizePopup.IsOpen = true;
    // ---- theme
    private void Theme_Click(object sender, RoutedEventArgs e) => SetTheme(!ThemeManager.Dark);
    private void SetTheme(bool dark, bool save = true)
    {
        ThemeManager.Apply(dark); UpdateThemeButton();
        Map.InvalidateVisual(); Globe.RefreshOverlay(); ClimateGraph.InvalidateVisual();
        BuildLayerCards(); UpdateLegend(); UpdatePointClimate(); UpdateClimatePanel();
        if (save) { var st = AppSettings.Load(); st.Dark = dark; AppSettings.Save(st); Notify(dark ? "어두운 화면" : "밝은 화면"); }
    }
    private void UpdateThemeButton()
    {
        ThemeButton.Content = ThemeManager.Dark ? "☀" : "☾";
        ThemeButton.ToolTip = ThemeManager.Dark ? "밝은 화면으로" : "어두운 화면으로";
    }
    private void TextSmaller_Click(object sender, RoutedEventArgs e) => StepTextScale(-1);
    private void TextLarger_Click(object sender, RoutedEventArgs e) => StepTextScale(+1);
    private void TextReset_Click(object sender, RoutedEventArgs e) => SetTextScale(1);
    private void UpdateMapFrame()
    {
        var k = UiK;
        var bottom = (ClimateMode && ClimateResultPanel.Visibility == Visibility.Visible ? 212 : 30) * k;
        var right = (ClimateMode && Inspector.Visibility == Visibility.Visible ? 380 : 28) * k;
        var left = (ClimateMode ? 28 : 96) * k; var top = 84 * k;
        var split = ViewSplitButton.IsChecked == true;
        void Frame(double frameTop)
        {
            if (split && Page.ActualWidth > 0)
            {
                // side by side: the globe takes a square on the right, the map the rest (2:1 fits its own frame)
                var width = Page.ActualWidth - left - right; var height = Page.ActualHeight - frameTop - bottom;
                var globe = Math.Max(160, Math.Min(height, width * .42)); const double gap = 18;
                Map.Margin = new Thickness(left, frameTop, right + globe + gap, bottom);
                Globe.Margin = new Thickness(Page.ActualWidth - right - globe, frameTop, right, bottom);
            }
            else Map.Margin = Globe.Margin = new Thickness(left, frameTop, right, bottom);
        }
        Frame(top);
        // terrain mode: the brush bar sits in the band above the map, which the 2:1 map leaves free in the
        // side-by-side view; where the map fills the height (flat or globe view) that band is reserved,
        // so the bar never covers the map
        if (!ClimateMode && Page.ActualWidth > 0)
        {
            var bar = (BrushCard.ActualHeight > 0 ? BrushCard.ActualHeight : 62) * k;
            var label = 30 * k;                                  // the map's "90° N" label just above it
            if (ViewGlobeButton.IsChecked == true)
            {
                Frame(top + bar + 12 * k);
                BrushCard.Margin = new Thickness(left / k, top / k, 0, 0);
            }
            else
            {
                var (_, mapTop) = MapPlacement();
                var need = bar + label + 8 * k + 16 * k;           // + room for a notice under the mode switch
                if (mapTop - top < need) { Frame(top + need - (mapTop - top)); (_, mapTop) = MapPlacement(); }
                // left edge with the map's frame: the map repeats sideways (longitude wraps), so that is where it is seen to start
                BrushCard.Margin = new Thickness(Map.Margin.Left / k, (mapTop - label - bar) / k, 0, 0);
            }
        }
        StatusChip.Margin = new Thickness(0, 0, 0, ClimateMode && ClimateResultPanel.Visibility == Visibility.Visible ? 222 : 26);
    }
    /// <summary>Top-left of the drawn map at zoom 1 in page coordinates (MapView.MapRect: 2:1, centred, 24 / 60 px slack).</summary>
    private (double Left, double Top) MapPlacement()
    {
        var m = Map.Margin; var w = Page.ActualWidth - m.Left - m.Right; var h = Page.ActualHeight - m.Top - m.Bottom;
        var width = Math.Max(1, Math.Min(w - 24, (h - 60) * 2));
        return (m.Left + (w - width) / 2, m.Top + (h - width / 2) / 2);
    }
    private void BrushCard_SizeChanged(object sender, SizeChangedEventArgs e) { if (initialized) UpdateMapFrame(); }
    private void View_Checked(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        Globe.EndInteraction(); Map.EndInteraction();
        var split = ViewSplitButton.IsChecked == true; var globe = ViewGlobeButton.IsChecked == true;
        Globe.Visibility = globe || split ? Visibility.Visible : Visibility.Collapsed;
        Map.Visibility = globe && !split ? Visibility.Collapsed : Visibility.Visible;
        UpdateMapFrame();
    }
    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) { if (initialized) UpdateMapFrame(); }
    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (!initialized || sender is not RadioButton button || button.Tag is not string tag) return;
        SetTool(Enum.Parse<TerrainTool>(tag));
    }
    private void SetTool(TerrainTool next)
    {
        Globe.EndInteraction(); Map.EndInteraction();
        if (next == TerrainTool.Pick) pickReturn = tool == TerrainTool.Ridge ? TerrainTool.Ridge : TerrainTool.Flatten;
        tool = next;
        Globe.Tool = Map.Tool = tool;
        Globe.Cursor = Map.Cursor = tool == TerrainTool.Navigate ? Cursors.Hand : Cursors.Cross;
        Globe.RefreshOverlay(); Map.RefreshOverlay();   // aim: selected place <-> under the mouse
        BrushCard.Visibility = tool == TerrainTool.Navigate ? Visibility.Collapsed : Visibility.Visible;
        RadiusPanel.Visibility = tool == TerrainTool.Pick ? Visibility.Collapsed : Visibility.Visible;
        SoftnessPanel.Visibility = tool == TerrainTool.Ridge ? Visibility.Collapsed : Visibility.Visible;   // the ridge has a fixed cross-section
        TargetPanel.Visibility = tool is TerrainTool.Flatten or TerrainTool.Ridge ? Visibility.Visible : Visibility.Collapsed;
        TargetLabel.Text = tool == TerrainTool.Ridge ? "능선 높이 (m)" : "목표 고도 (m)";
        RoughnessPanel.Visibility = tool is TerrainTool.Flatten or TerrainTool.Ridge ? Visibility.Visible : Visibility.Collapsed;
        // one short line in the bar; the full explanation on hover
        (BrushTitle.Text, BrushHint.Text, BrushHint.ToolTip) = tool switch
        {
            TerrainTool.Flatten => ("목표 고도", "정한 높이로 맞춥니다", "칠한 곳을 정한 높이로 맞춥니다. 대륙을 한 번에 깔거나 고원을 만들 때. 바다는 0 m 아래, 육지는 위."),
            TerrainTool.Smooth => ("부드럽게", "울퉁불퉁한 곳을 다듬습니다", "울퉁불퉁한 곳을 매끄럽게 합니다."),
            TerrainTool.Ridge => ("산맥", "선을 따라 능선이 솟습니다", "끄는 선을 따라 능선이 솟습니다. 여러 번 그어도 높이가 쌓이지 않습니다. 거칠기를 올리면 능선이 여러 갈래로 갈라지고 사이에 골짜기가 생깁니다."),
            TerrainTool.Pick => ("고도 찍기", "누른 곳의 높이를 가져옵니다", "지도를 누르면 그 지점의 높이를 목표 고도 칸으로 가져옵니다."),
            _ => ("", "", ""),
        };
    }
    private void BrushChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!initialized) return;
        RadiusLabel.Text = $"{RadiusSlider.Value:N0} km"; SoftnessLabel.Text = $"{SoftnessSlider.Value:N0}%"; RoughnessLabel.Text = $"{RoughnessSlider.Value:N0}%";
        Globe.BrushRadiusKm = Map.BrushRadiusKm = RadiusSlider.Value;
        Globe.RefreshOverlay(); Map.RefreshOverlay();
    }
    private void TargetChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized) return;
        targetValid = double.TryParse(TargetBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value) && value >= World.MinElevation && value <= World.MaxElevation;
        if (targetValid) targetElevation = value;
        TargetError.Text = targetValid ? "" : "−12,000 ~ 12,000 m의 숫자를 넣으세요.";
    }
    private void DisplayChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        Globe.ShowGrid = Map.ShowGrid = GridToggle.IsChecked == true;
        Globe.RefreshOverlay(); Map.RefreshOverlay(); textureDirty = true;
        if (atlas is not null) BuildLayerCards();
    }
    private void ResetView_Click(object sender, RoutedEventArgs e) { Globe.ResetView(); Map.ResetView(); }
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        Globe.EndInteraction(); Map.EndInteraction();
        if (world is null || !history.Undo(world)) return;
        dirty = textureDirty = statsDirty = true; UpdateDocumentState(); Notify("마지막 붓질을 되돌렸습니다.");
    }
    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (world is null || !history.Redo(world)) return;
        dirty = textureDirty = statsDirty = true; UpdateDocumentState(); Notify("붓질을 다시 적용했습니다.");
    }

    // ======================================================================== files and menu
    private void Menu_Click(object sender, RoutedEventArgs e) => WorldMenu.IsOpen = true;
    private async void NewWorld_Click(object sender, RoutedEventArgs e)
    {
        WorldMenu.IsOpen = false;
        if (!await MayReplaceAsync()) return;
        await WorkAsync(() => new World("새로운 지도"), null, "바다뿐인 새 지도입니다. ‘목표 고도’ 붓으로 첫 대륙을 깔아 보세요.");
    }
    private async void Example_Click(object sender, RoutedEventArgs e)
    {
        WorldMenu.IsOpen = false;
        if (!await MayReplaceAsync()) return;
        await WorkAsync(() => TerrainExamples.Create(), null, "예제 지형을 불러왔습니다.");
    }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        WorldMenu.IsOpen = false;
        if (busy || !await MayReplaceAsync()) return;
        var dialog = new OpenFileDialog { Filter = "Map 지도 (*.map)|*.map;*.garden", Title = "지도 열기", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await WorkAsync(() => WorldFile.Load(dialog.FileName), dialog.FileName, "지도를 열었습니다.");
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(false);
    private async void SaveAs_Click(object sender, RoutedEventArgs e) { WorldMenu.IsOpen = false; await SaveAsync(true); }
    private async Task<bool> SaveAsync(bool saveAs)
    {
        if (world is null || busy) return false;
        Globe.EndInteraction(); Map.EndInteraction();
        var path = currentPath;
        if (path is null || saveAs)
        {
            var dialog = new SaveFileDialog { Filter = "Map 지도 (*.map)|*.map", DefaultExt = ".map", AddExtension = true,
                FileName = path is null ? "My-world.map" : Path.ChangeExtension(Path.GetFileName(path), ".map"), Title = "지도 저장", OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }
        SetBusy(true, "저장하는 중…");
        try
        {
            // the climate of this terrain goes into the map file with it
            await WorldFile.SaveAsync(world, path, atlas is not null && AtlasMatches ? atlas.DirectoryPath : null);
            currentPath = path; dirty = false; UpdateDocumentState();
            Notify("저장했습니다."); return true;
        }
        catch (Exception ex) { ShowFileError(ex); return false; }
        finally { SetBusy(false); }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        WorldMenu.IsOpen = false;
        if (world is null || shownTexture is null) return;
        var layerName = ClimateMode && atlasLayer is not null && AtlasMatches ? "-" + atlasLayer.Id : "-terrain";
        var dialog = new SaveFileDialog { Filter = "PNG 그림 (*.png)|*.png", DefaultExt = ".png", AddExtension = true, FileName = world.Name + layerName + ".png", Title = "지도 그림 저장" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(shownTexture));
            using var file = File.Create(dialog.FileName); png.Save(file);
            Notify($"지도 그림을 저장했습니다 ({shownTexture.PixelWidth}×{shownTexture.PixelHeight}, 등장방형).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowFileError(ex); }
    }
    private async Task<bool> MayReplaceAsync()
    {
        if (busy) return false;
        if (ClimateRunning) { Notify("기후 계산을 취소한 뒤 다른 지도를 열 수 있습니다."); return false; }
        Globe.EndInteraction(); Map.EndInteraction();
        if (!dirty) return true;
        var choice = MessageBox.Show(this, "이 지도의 변경사항을 저장할까요?\n\n예: 저장하고 계속\n아니요: 버리고 계속\n취소: 돌아가기", "저장하지 않은 변경", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return choice == MessageBoxResult.No || (choice == MessageBoxResult.Yes && await SaveAsync(false));
    }
    private async Task WorkAsync(Func<World> work, string? path, string message)
    {
        if (busy) return;
        SetBusy(true, "지형을 준비하는 중…");
        try
        {
            var (next, kept) = await Task.Run(() => (work(), path is null ? null : KeptClimate(path)));
            SetWorld(next, path);
            if (kept is not null && kept.Matches(terrainHash)) SetAtlas(kept);
            Notify(message);
        }
        catch (Exception ex) { ShowFileError(ex); }
        finally { SetBusy(false); }
    }
    /// <summary>The climate saved in a map file, unpacked into this run's scratch folder (null: none or unreadable).</summary>
    private static WorldClimateAtlas? KeptClimate(string path)
    {
        var dir = Path.Combine(WorldClimateJob.SessionRoot, "opened", Guid.NewGuid().ToString("N"));
        try { return WorldFile.ExtractClimate(path, dir) ? WorldClimateAtlas.Read(dir) : null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return null; }
    }
    private void SetBusy(bool value, string? message = null)
    {
        busy = value; ((UIElement)Content).IsEnabled = !value;
        if (message is not null) Notify(message);
        UpdateClimatePanel();
    }
    private void ShowFileError(Exception ex)
    {
        Notify("작업을 마치지 못했습니다. 지금 지도는 그대로입니다.");
        MessageBox.Show(this, ex.Message, "처리하지 못했습니다", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) { refreshTimer.Stop(); climateTimer.Stop(); return; }
        e.Cancel = true;
        if (closingInProgress) return;
        closingInProgress = true;
        try
        {
            if (ClimateRunning) { atlasJob!.Cancel(); UpdateClimatePanel(); if (atlasJob.Completion is { } job) await job; }
            if (await MayReplaceAsync()) { allowClose = true; _ = Dispatcher.BeginInvoke(Close); }
        }
        finally { closingInProgress = false; }
    }

    // ======================================================================== climate: run
    private void ClimateRun_Click(object sender, RoutedEventArgs e)
    {
        if (world is null || busy || ClimateRunning) return;
        if (atlasRepository is null) { Notify("기후 모형(tools/climate/worldclimate)을 찾지 못했습니다."); return; }
        Globe.EndInteraction(); Map.EndInteraction();
        // no deep-water sinking (the automatic choice is off; no picker in the app)
        atlasJob = new WorldClimateJob(world, atlasRepository, Array.Empty<GeoPoint>()); atlasJob.Start(); rerunRequested = false;
        Notify("기후 계산을 시작했습니다. 그동안 지형을 계속 고칠 수 있지만, 결과는 시작할 때의 지형 것입니다.");
        UpdateClimatePanel();
    }
    private void ClimateCancel_Click(object sender, RoutedEventArgs e) { atlasJob?.Cancel(); UpdateClimatePanel(); }
    private void Rerun_Click(object sender, RoutedEventArgs e) { rerunRequested = true; UpdateClimatePanel(); }
    private void RunClose_Click(object sender, RoutedEventArgs e) { rerunRequested = false; if (atlasJob is { IsFinished: true }) atlasJob = null; UpdateClimatePanel(); }
    private void TakeFinishedAtlas()
    {
        if (atlasJob is not { IsFinished: true, Result: { } result } || ReferenceEquals(result, atlas)) return;
        SetAtlas(result);
        if (AtlasMatches) { dirty = true; UpdateDocumentState(); }   // a new climate is kept only by saving the map
        if (AtlasMatches && theme.Key == "off") SelectTheme("koppen");
        Notify(AtlasMatches ? "기후를 계산했습니다. 아래에서 지도에 그릴 주제를 고르고, 지도를 눌러 그곳의 기후를 보세요." : "계산은 끝났지만 그사이 지형이 바뀌었습니다.");
    }
    private void UpdateClimatePanel()
    {
        if (!initialized || world is null) return;
        var running = ClimateRunning; var matches = AtlasMatches;
        NewWorldButton.IsEnabled = ExampleButton.IsEnabled = OpenButton.IsEnabled = !running;
        ClimateResultButton.IsEnabled = atlas is not null;
        var failed = atlasJob is { Phase: "failed" or "cancelled" };
        var showRun = running || failed || !matches || rerunRequested;
        RunCard.Visibility = ClimateMode && showRun ? Visibility.Visible : Visibility.Collapsed;
        ClimateResultPanel.Visibility = ClimateMode && matches ? Visibility.Visible : Visibility.Collapsed;
        RunProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ClimateRunButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        ClimateCancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ClimateCancelButton.IsEnabled = atlasJob?.CancelRequested != true;
        RunCloseButton.Visibility = !running && matches && (rerunRequested || failed) ? Visibility.Visible : Visibility.Collapsed;
        ClimateRunButton.IsEnabled = atlasRepository is not null && !busy;
        if (running)
        {
            var job = atlasJob!;
            RunTitle.Text = job.CancelRequested ? "취소하는 중" : "기후를 계산하는 중";
            RunText.Text = "바다와 바람, 해류, 비와 강, 눈과 빙하, 기후형을 차례로 구합니다. 따뜻한 기후와 얼음 행성처럼 서로 다른 안정한 기후가 있으면 함께 찾습니다.";
            ClimateProgressBar.IsIndeterminate = job.Progress <= 0; ClimateProgressBar.Value = job.Progress * 100;
            ClimateStateText.Text = $"{job.Progress:P0} · {job.Message}";
            ClimateDetailText.Text = $"경과 {TimeSpan.FromSeconds(job.ElapsedSeconds):m\\:ss} · 보통 3–5분" + (job.SourceHash != terrainHash ? " · 계산을 시작한 뒤 지형이 바뀌었습니다" : "");
        }
        else if (failed)
        {
            RunTitle.Text = atlasJob!.Phase == "cancelled" ? "계산을 취소했습니다" : "계산하지 못했습니다";
            RunText.Text = (atlasJob.Error ?? "완료된 결과가 없습니다.") + (matches ? "\n이전 결과는 그대로 있습니다." : "");
            ClimateRunButton.Content = "다시 시도";
        }
        else if (atlas is not null && !matches)
        {
            RunTitle.Text = "지형이 바뀌었습니다";
            RunText.Text = "마지막으로 기후를 계산한 뒤 지형을 고쳤습니다. 지금 지형으로 다시 계산하면 새 기후를 볼 수 있습니다. (되돌리기로 그때 지형으로 돌아가면 이전 결과가 다시 보입니다.)";
            ClimateRunButton.Content = "다시 계산";
        }
        else if (rerunRequested)
        {
            RunTitle.Text = "다시 계산";
            RunText.Text = "같은 지형으로 다시 계산합니다.";
            ClimateRunButton.Content = "다시 계산";
        }
        else
        {
            RunTitle.Text = "이 지도의 기후";
            RunText.Text = "그린 지형으로 바람과 해류, 비와 강, 눈과 빙하, 기후형과 생물군계를 계산합니다. 3–5분쯤 걸리고, 그동안 지형을 계속 다듬을 수 있습니다.";
            ClimateRunButton.Content = "기후 계산 시작";
        }
        DockInfo.Text = atlas is not null && matches ? $"{atlas.CreatedUtc.LocalDateTime:M월 d일 HH:mm} 계산" : "";
        foreach (var item in StateList.Children.OfType<RadioButton>().Concat(LayerList.Children.OfType<RadioButton>()).Concat(SeasonList.Children.OfType<RadioButton>())) item.IsEnabled = !running;
        RerunButton.IsEnabled = !running;
        if (!matches) Inspector.Visibility = Visibility.Collapsed;
        UpdateMapFrame();
    }

    // ======================================================================== climate: dock, layers, legend
    private void SetAtlas(WorldClimateAtlas? value)
    {
        atlas = value; atlasState = 0; seasonIndex = 0; atlasLayer = null; theme = Themes[0];
        koppenColors = atlas?.States[0].Layers.FirstOrDefault(l => l.Id == "koppen")?.LegendItems
            .ToDictionary(i => i.Label.Split(' ')[0], i => i.Color) ?? new();
        fillingClimateLists = true;
        try
        {
            StateList.Children.Clear();
            if (atlas is not null)
                for (var i = 0; i < atlas.States.Count; i++)
                {
                    var st = atlas.States[i];
                    var item = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "ClimateState", Tag = i, IsChecked = i == 0, Padding = new Thickness(14, 5, 14, 5),
                        Content = $"{st.Label} {st.GlobalMeanC:F0}°", ToolTip = $"전 지구 평균 {st.GlobalMeanC:F1} °C · {(st.Stable == true ? "안정한 기후" : st.Stable == false ? "불안정" : "안정성 미판정")}" };
                    item.Checked += ClimateState_Checked; StateList.Children.Add(item);
                }
        }
        finally { fillingClimateLists = false; }
        BuildLayerCards(); BuildSeasons();
        textureDirty = true; UpdateClimatePanel(); UpdatePointClimate(); UpdateLegend();
    }
    private IEnumerable<Theme> AvailableThemes() =>
        atlas is null ? [] : Themes.Where(t => t.Key == "off" || t.LayerIds.Any(id => atlas.States[atlasState].Layers.Any(l => l.Id == id)));
    private void BuildLayerCards()
    {
        LayerList.Children.Clear();
        if (atlas is null || world is null || !AtlasMatches) return;
        var wasFilling = fillingClimateLists; fillingClimateLists = true;
        try { AddLayerCards(); } finally { fillingClimateLists = wasFilling; }
    }
    private void AddLayerCards()
    {
        BitmapSource? terrain = null;
        foreach (var t in AvailableThemes())
        {
            ImageSource? thumb = null;
            try
            {
                terrain ??= TerrainTexture.Build(world!, ShadeToggle.IsChecked == true);
                var layer = t.LayerIds.Select(id => atlas!.States[atlasState].Layers.FirstOrDefault(l => l.Id == id)).FirstOrDefault(l => l is not null);
                var full = layer is null ? terrain : ComposeLayer(terrain, layer);
                var small = new TransformedBitmap(full, new ScaleTransform(124.0 / full.PixelWidth, 62.0 / full.PixelHeight)); small.Freeze(); thumb = small;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException) { }
            var content = new StackPanel { Width = 124 };
            content.Children.Add(new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Height = 62, Background = (Brush)FindResource("Rule"),
                Child = new Image { Source = thumb, Stretch = Stretch.Fill } });
            content.Children.Add(new TextBlock { Text = t.Title, FontSize = 12, Margin = new Thickness(2, 5, 0, 1) });
            var card = new RadioButton { Style = (Style)FindResource("LayerCard"), GroupName = "Theme", Tag = t.Key, Content = content, IsChecked = t.Key == theme.Key };
            card.Checked += Theme_Checked; LayerList.Children.Add(card);
        }
    }
    private void BuildSeasons()
    {
        fillingClimateLists = true;
        try
        {
            SeasonList.Children.Clear();
            string[] ids = atlas is null ? [] : theme.LayerIds.Where(id => atlas.States[atlasState].Layers.Any(l => l.Id == id)).ToArray();
            SeasonBar.Visibility = ids.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            seasonIndex = Math.Clamp(seasonIndex, 0, Math.Max(0, ids.Length - 1));
            for (var i = 0; i < ids.Length && ids.Length > 1; i++)
            {
                var item = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "Season", Tag = i, Content = SeasonLabel(ids[i]), IsChecked = i == seasonIndex, Padding = new Thickness(13, 5, 13, 5) };
                item.Checked += Season_Checked; SeasonList.Children.Add(item);
            }
            atlasLayer = ids.Length == 0 ? null : atlas!.States[atlasState].Layers.First(l => l.Id == ids[seasonIndex]);
            CloudToggle.Visibility = CloudLayer() is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { fillingClimateLists = false; }
        textureDirty = true;
    }
    /// <summary>The cloud deck of the season shown, when the planet is seen from space.</summary>
    private WorldClimateLayer? CloudLayer() =>
        theme.Key != "satellite" || atlas is null || atlasLayer is null ? null
            : atlas.States[atlasState].Layers.FirstOrDefault(l => l.Id == "clouds_" + atlasLayer.Id["satellite_".Length..]);
    private void Cloud_Changed(object sender, RoutedEventArgs e) { if (!initialized) return; textureDirty = true; RefreshTerrain(); }
    private void SelectTheme(string key)
    {
        foreach (var card in LayerList.Children.OfType<RadioButton>())
            if (Equals(card.Tag, key)) { card.IsChecked = true; return; }
    }
    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (fillingClimateLists || sender is not RadioButton { Tag: string key }) return;
        theme = Themes.First(t => t.Key == key); seasonIndex = 0; BuildSeasons(); UpdateLegend();
        if (key == "satellite" && ViewMapButton.IsChecked == true) { ViewGlobeButton.IsChecked = true; Notify("우주에서 본 모습입니다. 끌어서 돌려 보고, 계절을 바꿔 눈과 초록이 어떻게 바뀌는지 보세요."); }
    }
    private void Season_Checked(object sender, RoutedEventArgs e)
    {
        if (fillingClimateLists || sender is not RadioButton { Tag: int i }) return;
        seasonIndex = i; BuildSeasons(); UpdateLegend();
    }
    private void ClimateState_Checked(object sender, RoutedEventArgs e)
    {
        if (fillingClimateLists || atlas is null || sender is not RadioButton { Tag: int index }) return;
        atlasState = index; BuildLayerCards(); BuildSeasons(); UpdatePointClimate(); UpdateLegend();
    }
    private void UpdateLegend()
    {
        if (!initialized) return;
        var show = ClimateMode && atlas is not null && atlasLayer is not null && AtlasMatches;
        LegendCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var items = atlasLayer!.LegendItems;
        LegendTitle.Text = (legendFolded ? "▸ " : "▾ ") + (atlas!.States.Count > 1 ? $"{atlasLayer.Title} · {atlas.States[atlasState].Label}" : atlasLayer.Title);
        ClimateLegend.Visibility = items.Count == 0 && !legendFolded ? Visibility.Visible : Visibility.Collapsed;
        ClimateLegendScroll.Visibility = items.Count > 0 && !legendFolded ? Visibility.Visible : Visibility.Collapsed;
        ClimateLegendItems.Children.Clear();
        foreach (var (label, color) in items)
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 14, 2) };
            entry.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!, BorderBrush = (Brush)FindResource("Rule"), BorderThickness = new Thickness(1) });
            entry.Children.Add(new TextBlock { Text = label, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            ClimateLegendItems.Children.Add(entry);
        }
        if (items.Count == 0)
        {
            try { ClimateLegend.Source = DarkLegendOnPaper(ClimateLayerTexture.LoadImage(atlas.LegendPath(atlasState, atlasLayer))); }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException) { ClimateLegend.Source = null; }
        }
    }
    private void LegendTitle_Click(object sender, MouseButtonEventArgs e) { legendFolded = !legendFolded; UpdateLegend(); }
    /// <summary>The colour-scale legends are drawn with light text for dark pages; re-ink them for the paper cards.</summary>
    private static BitmapSource DarkLegendOnPaper(BitmapSource legend)
    {
        if (ThemeManager.Dark) return legend;          // drawn for dark pages already
        var src = new FormatConvertedBitmap(legend, PixelFormats.Bgra32, null, 0); var w = src.PixelWidth; var h = src.PixelHeight;
        var px = new byte[w * h * 4]; src.CopyPixels(px, w * 4, 0);
        for (var i = 0; i < px.Length; i += 4)
        {
            int b = px[i], g = px[i + 1], r = px[i + 2];
            var grey = Math.Abs(r - g) < 18 && Math.Abs(g - b) < 22 && r > 150;      // the light grey-blue text and frame
            if (grey) { px[i] = 0x24; px[i + 1] = 0x28; px[i + 2] = 0x2A; }
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4); result.Freeze(); return result;
    }

    // ======================================================================== climate: inspector
    private void InspectorClose_Click(object sender, RoutedEventArgs e) => ClearSelection();
    private void UpdatePointClimate()
    {
        if (!initialized) return;
        if (atlas is null || !AtlasMatches || selected is not { } p || !ClimateMode) { if (!ClimateMode || atlas is null || !AtlasMatches) Inspector.Visibility = Visibility.Collapsed; UpdateMapFrame(); return; }
        var c = atlas.Nearest(atlasState, p);
        // the map pixel's own climate (what the layers show) when the result has it; older results only have the cell mean
        var px = atlas.Pixel(atlasState, p);
        var land = px is not null ? px.Koppen is not null : c.LandFraction > .5;
        var koppen = px is not null ? px.Koppen : c.Koppen; var koppenName = px is not null ? px.KoppenName : c.KoppenName; var biome = px is not null ? px.Biome : c.Biome;
        var temperature = px?.TemperatureC ?? c.TemperatureC; var rain = px?.PrecipitationMmDay ?? c.PrecipitationMmDay;
        KoppenCode.Text = land ? koppen ?? "" : "바다";
        KoppenCode.FontSize = land ? 14 : 12;
        KoppenBadge.Background = land && koppen is { } k && koppenColors.TryGetValue(k, out var col)
            ? (SolidColorBrush)new BrushConverter().ConvertFromString(col)! : new SolidColorBrush(Color.FromRgb(0x9C, 0xB9, 0xC9));
        KoppenCode.Foreground = land && koppen is ("Af" or "Am" or "Dwd" or "Dfd" or "Dwc" or "Dsd" or "Cwc" or "EF") ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x2A, 0x28, 0x24));
        InspectorTitle.Text = land ? koppenName ?? "" : (c.SeaIce > .5 ? "얼어붙는 바다" : "바다");
        InspectorSubtitle.Text = land ? (px is not null ? $"{biome} · 해발 약 {px.HeightMeters:N0} m" : $"{biome} · 해발 약 {c.HeightMeters:N0} m (칸 평균)") : $"해빙 최대 {c.SeaIce:P0}";
        ClimateGraph.Show(temperature, rain);
        var facts = new StringBuilder();
        facts.Append(land ? $"{koppen} {koppenName} · {biome}\n" : "바다\n");
        facts.Append($"연평균 기온 {temperature.Average():F1} °C  (가장 따뜻한 계절 {temperature.Max():F1} · 가장 추운 계절 {temperature.Min():F1})\n");
        facts.Append($"연 강수 {rain.Average() * 365.24:N0} mm");
        if (land && c.LandFraction > .5) facts.Append(c.Glacier ? "\n빙하·빙상으로 덮인 땅" : c.SnowSeasons > 0 ? $"\n눈이 덮이는 계절 {c.SnowSeasons}개" : "");
        ClimatePointText.Text = facts.ToString();
        var why = c.Explanation is { } text ? "· " + text[(text.IndexOf(':') + 1)..].Trim().Replace("; ", "\n· ") : "";
        if (px is not null && land && c.Koppen is { } ck && ck != koppen)
            why = $"· 둘레 칸 평균은 {ck} {c.KoppenName}이고, 이 지점은 제 높이({px.HeightMeters:N0} m, 칸 평균 {c.HeightMeters:N0} m), 바람에 대한 비탈 방향, 이웃 칸과의 사이 값으로 {koppen}이 된다" + (why.Length > 0 ? "\n" + why : "");
        InspectorWhy.Text = why.Length > 0 ? why : land ? "" : "바다 칸은 설명을 따로 만들지 않습니다. 가까운 해안을 눌러 보세요.";
        InspectorFoot.Text = px is not null
            ? $"이 지점 {p.Latitude:F1}°, {p.Longitude:F1}°의 값은 지도 색과 같습니다(지형 높이·비탈로 나눈 값). 까닭 설명은 둘레 약 480 km 기후 칸 기준입니다. 창작용 단순 계절 기후 모형의 근사입니다."
            : $"기후 칸 중심 {c.Latitude:F1}°, {c.Longitude:F1}° · 칸 하나는 약 480 km를 대표합니다. 창작용 단순 계절 기후 모형의 근사입니다.";
        Inspector.Visibility = Visibility.Visible; UpdateMapFrame();
    }
    private void ClimateResult_Click(object sender, RoutedEventArgs e)
    {
        WorldMenu.IsOpen = false;
        if (atlas is null) return;
        var a = atlas;
        var window = new Window { Owner = this, Title = "기후 요약", Width = 620 * UiK, Height = 560 * UiK, MinWidth = 480, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(28), LayoutTransform = new ScaleTransform(UiK, UiK) };
        panel.Children.Add(new TextBlock { Text = AtlasMatches ? "이 지도의 기후" : "이전 지형의 기후", Style = (Style)FindResource("Headline"), Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = a.Model + "\n창작용 근사입니다. 지구로 시험하면 사막·우림·계절풍·해류의 큰 그림은 맞고, 대륙 동쪽 아열대가 실제보다 건조하게 나오는 등 알려진 한계가 있습니다.",
            TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 16) });
        var text = new StringBuilder();
        text.AppendLine($"{a.CreatedUtc.LocalDateTime:yyyy-MM-dd HH:mm} 계산 · {a.Seconds:F0}초");
        foreach (var st in a.States)
            text.AppendLine($"\n{st.Label} — 전 지구 평균 {st.GlobalMeanC:F1} °C, 강수 {st.GlobalPrecipitationMmDay:F2} mm/일, {(st.Stable == true ? "안정" : st.Stable == false ? "불안정" : "안정성 미판정")}\n육지의 기후대  " +
                string.Join("   ", st.KoppenGroupShare.Select(k => $"{k.Key} {k.Value:P0}")));
        text.AppendLine("\n" + (a.DeepwaterSites.Count > 0 ? "바닷물이 가라앉는 바다(모형이 찾음)  " + string.Join(", ", a.DeepwaterSites.Select(p => $"{Math.Abs(p.Latitude):F0}° {(p.Latitude >= 0 ? "N" : "S")}, {Math.Abs(p.Longitude):F0}° {(p.Longitude >= 0 ? "E" : "W")}")) : "바닷물이 가라앉는 바다  없음"));
        panel.Children.Add(new TextBlock { Text = text.ToString(), TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
        var open = new Button { Content = "결과 폴더 열기", Style = (Style)FindResource("Quiet"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(a.DirectoryPath) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(window, ex.Message, "폴더를 열지 못했습니다"); } };
        panel.Children.Add(open);
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.ShowDialog();
    }

    // ======================================================================== keyboard
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (busy || world is null) return;
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (control && e.Key == Key.S) { e.Handled = true; await SaveAsync(shift); return; }
        if (control && e.Key == Key.O) { e.Handled = true; Open_Click(this, new RoutedEventArgs()); return; }
        if (control && e.Key is Key.OemPlus or Key.Add) { e.Handled = true; StepTextScale(+1); return; }
        if (control && e.Key is Key.OemMinus or Key.Subtract) { e.Handled = true; StepTextScale(-1); return; }
        if (control && e.Key is Key.D0 or Key.NumPad0) { e.Handled = true; SetTextScale(1); return; }
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Escape && selected is not null) { e.Handled = true; ClearSelection(); return; }
        if (control && e.Key == Key.Z) { e.Handled = true; if (shift) Redo_Click(this, new RoutedEventArgs()); else Undo_Click(this, new RoutedEventArgs()); return; }
        if (control && e.Key == Key.Y) { e.Handled = true; Redo_Click(this, new RoutedEventArgs()); return; }
        if (control) return;
        if (e.Key == Key.Tab) { e.Handled = true; (ClimateMode ? ModeTerrain : ModeClimate).IsChecked = true; return; }
        RadioButton? button = e.Key switch { Key.D1 => NavigateTool, Key.D2 => FlattenTool, Key.D3 => SmoothTool,
            Key.D4 => RidgeTool, Key.D5 => PickTool, _ => null };
        if (button is not null) { if (ClimateMode && button != NavigateTool) ModeTerrain.IsChecked = true; button.IsChecked = true; e.Handled = true; }
    }
}
