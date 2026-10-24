using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TheGarden.Core;
using TheGarden.Core.Climate;
using TheGarden.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var measurements=new Dictionary<string,double>();
        var app=new App(); app.InitializeComponent(); app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        MainWindow? window=null;
        try
        {
            var watch=Stopwatch.StartNew(); var world=TerrainExamples.Create(); measurements["example_ms"]=watch.Elapsed.TotalMilliseconds;
            window=new MainWindow();
            Call(window,"SetWorld",world,null);
            var content=(Grid)window.Content; content.SetResourceReference(Panel.BackgroundProperty,"Paper"); content.Width=1440; content.Height=890;
            content.Measure(new Size(1440,890)); content.Arrange(new Rect(0,0,1440,890)); content.UpdateLayout();
            var map=(MapView)window.FindName("Map"); var globe=(GlobeView)window.FindName("Globe");
            var texture=typeof(TerrainView).GetField("Texture",BindingFlags.Instance|BindingFlags.NonPublic)!;
            Assert(ReferenceEquals(texture.GetValue(map),texture.GetValue(globe)),"same display texture");
            var initial=world.Fingerprint();
            ((RadioButton)window.FindName("FlattenTool")).IsChecked=true;
            var mapPoint=map.Pick(new Point(map.ActualWidth*.5,map.ActualHeight*.5))!.Value;
            watch.Restart(); Stroke(window,map,"StrokeStarted",mapPoint); Stroke(window,map,"StrokeFinished");
            Call(window,"RefreshTerrain"); measurements["map_stamp_and_render_ms"]=watch.Elapsed.TotalMilliseconds;
            var edited=world.Fingerprint(); Assert(initial!=edited,"map edits native surface");
            Call(window,"Undo_Click",window,new RoutedEventArgs()); Assert(world.Fingerprint()==initial,"UI undo");
            Call(window,"Redo_Click",window,new RoutedEventArgs()); Assert(world.Fingerprint()==edited,"UI redo");
            ((TextBox)window.FindName("TargetBox")).Text="-1000";   // the globe stroke lowers what the map stroke built
            var globePoint=globe.Pick(new Point(globe.ActualWidth*.5,globe.ActualHeight*.5))!.Value;
            Stroke(window,globe,"StrokeStarted",globePoint); Stroke(window,globe,"StrokeFinished");
            Assert(world.Fingerprint()!=edited,"globe edits same surface");
            ((RadioButton)window.FindName("SmoothTool")).IsChecked=true;
            Stroke(window,globe,"StrokeStarted",globePoint); Stroke(window,globe,"StrokeFinished");
            ((RadioButton)window.FindName("FlattenTool")).IsChecked=true;
            ((TextBox)window.FindName("TargetBox")).Text="2400";
            ((Slider)window.FindName("RadiusSlider")).Value=100;
            watch.Restart(); Stroke(window,map,"StrokeStarted",new GeoPoint(0,179)); Stroke(window,map,"StrokeFinished");
            Call(window,"RefreshTerrain"); measurements["small_stamp_and_render_ms"]=watch.Elapsed.TotalMilliseconds;
            Assert(world.Sample(new(0,179))>2000,"small target-height brush");
            var dir=Path.Combine(Environment.CurrentDirectory,"artifacts","vector-editor-20261017","ui"); Directory.CreateDirectory(dir);
            var save=Path.Combine(dir,"ui-edited.garden"); WorldFile.Save(world,save);
            var loaded=WorldFile.Load(save); Assert(loaded.Fingerprint()==world.Fingerprint(),"UI-edited saved source");
            Call(window,"SetWorld",loaded,save); Call(window,"PointAt",new GeoPoint(0,179));
            Call(window,"RefreshTerrain"); content.UpdateLayout();
            Assert(ReferenceEquals(texture.GetValue(map),texture.GetValue(globe)),"reload displays same texture");
            Assert(((TextBlock)window.FindName("ResolutionText")).Text.Contains("격자"),"grid terrain status");
            Assert(((Button)window.FindName("ClimateRunButton")).IsEnabled==(WorldClimateJob.FindRepository() is not null),"climate run available when the model is present");
            // climate layer display with a synthetic atlas for the loaded terrain (no model run here)
            var atlasDir=Path.Combine(Path.GetTempPath(),"garden-atlas-ui-"+Guid.NewGuid().ToString("N"));
            AtlasFixture.Write(atlasDir,loaded.Fingerprint(),loaded.Width,loaded.Height,path =>
            {
                var px=new byte[loaded.Width*loaded.Height*4];
                for(var y=0;y<loaded.Height;y++) for(var x=0;x<loaded.Width/2;x++){ var o=(y*loaded.Width+x)*4; px[o+2]=255; px[o+3]=255; }
                var bmp=BitmapSource.Create(loaded.Width,loaded.Height,96,96,System.Windows.Media.PixelFormats.Bgra32,null,px,loaded.Width*4);
                var enc=new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var f=File.Create(path); enc.Save(f);
            });
            Call(window,"SetAtlas",WorldClimateAtlas.Read(atlasDir));
            ((RadioButton)window.FindName("ModeClimate")).IsChecked=true;
            var layerList=(StackPanel)window.FindName("LayerList"); var resultPanel=(FrameworkElement)window.FindName("ClimateResultPanel");
            var layerItems=layerList.Children.OfType<RadioButton>().ToArray();
            Assert(resultPanel.Visibility==Visibility.Visible && layerItems.Length==2 && Equals(layerItems[1].Tag,"temperature"),"theme cards for matching terrain");
            layerItems[1].IsChecked=true; Call(window,"RefreshTerrain");
            var shown=(BitmapSource)texture.GetValue(map)!; var pix=new byte[4];
            shown.CopyPixels(new Int32Rect(10,loaded.Height/2,1,1),pix,4,0);
            Assert(pix[2]>pix[1]+60,"layer drawn over the terrain where it is opaque");
            Assert(ReferenceEquals(texture.GetValue(map),texture.GetValue(globe)),"globe shows the same climate texture");
            Assert(((FrameworkElement)window.FindName("LegendCard")).Visibility==Visibility.Visible && ((Image)window.FindName("ClimateLegend")).Visibility==Visibility.Visible,"legend shown with a climate layer");
            Call(window,"PointAt",new GeoPoint(1,1));
            Assert(((TextBlock)window.FindName("ClimatePointText")).Text.Contains("Af") && ((FrameworkElement)window.FindName("Inspector")).Visibility==Visibility.Visible,"clicked place shows its climate");
            var inspector=(FrameworkElement)window.FindName("Inspector");
            Call(window,"InspectorClose_Click",window,new RoutedEventArgs());
            Assert(inspector.Visibility!=Visibility.Visible && map.Marker is null && globe.Marker is null,"closing the card drops the picked place");
            layerItems[0].IsChecked=true; layerItems[1].IsChecked=true; Call(window,"RefreshTerrain");
            Assert(inspector.Visibility!=Visibility.Visible,"a closed card stays closed when the layer changes");
            Call(window,"PointAt",new GeoPoint(1,1));
            ((RadioButton)window.FindName("ModeTerrain")).IsChecked=true; ((RadioButton)window.FindName("ModeClimate")).IsChecked=true;
            Assert(inspector.Visibility!=Visibility.Visible && map.Marker is null,"switching views drops the picked place");
            Call(window,"PointAt",new GeoPoint(1,1));
            // the planet from space: natural colour theme, globe in space mode, cloud deck on demand
            var satDir=Path.Combine(Path.GetTempPath(),"garden-atlas-sat-"+Guid.NewGuid().ToString("N"));
            AtlasFixture.Write(satDir,loaded.Fingerprint(),loaded.Width,loaded.Height,path =>
            {
                var cloud=path.Contains("clouds"); var px=new byte[loaded.Width*loaded.Height*4];
                for(var o=0;o<px.Length;o+=4){ if(cloud){ px[o]=px[o+1]=px[o+2]=250; px[o+3]=128; } else { px[o]=40; px[o+1]=120; px[o+2]=30; px[o+3]=255; } }
                var bmp=BitmapSource.Create(loaded.Width,loaded.Height,96,96,System.Windows.Media.PixelFormats.Bgra32,null,px,loaded.Width*4);
                var enc=new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var f=File.Create(path); enc.Save(f);
            },satellite:true);
            Call(window,"SetAtlas",WorldClimateAtlas.Read(satDir));
            var satCard=((StackPanel)window.FindName("LayerList")).Children.OfType<RadioButton>().FirstOrDefault(c=>Equals(c.Tag,"satellite"));
            Assert(satCard is not null,"space view theme offered when the atlas has it");
            ((RadioButton)window.FindName("ViewMapButton")).IsChecked=true; satCard!.IsChecked=true; Call(window,"RefreshTerrain");
            Assert(((RadioButton)window.FindName("ViewGlobeButton")).IsChecked==true && globe.Space,"space view turns to the globe in space mode");
            var seasons=((StackPanel)window.FindName("SeasonList")).Children.OfType<RadioButton>().ToArray();
            Assert(seasons.Length==2 && Equals(seasons[0].Content,"6–8월") && Equals(seasons[1].Content,"12–2월"),"space view seasons");
            var cloudToggle=(System.Windows.Controls.Primitives.ToggleButton)window.FindName("CloudToggle");
            Assert(cloudToggle.Visibility==Visibility.Visible && cloudToggle.IsChecked!=true,"cloud switch offered, off at first");
            ((BitmapSource)texture.GetValue(map)!).CopyPixels(new Int32Rect(10,loaded.Height/2,1,1),pix,4,0); var before=(int[])[pix[0],pix[1],pix[2]];
            Assert(before[1]>before[2]+40 && before[1]>before[0]+40,"natural colour layer drawn over the terrain");
            cloudToggle.IsChecked=true; ((BitmapSource)texture.GetValue(map)!).CopyPixels(new Int32Rect(10,loaded.Height/2,1,1),pix,4,0);
            Assert(pix[0]>before[0]+60 && pix[2]>before[2]+60,"clouds laid over the surface");
            seasons[1].IsChecked=true; Call(window,"RefreshTerrain");
            Assert(cloudToggle.Visibility==Visibility.Collapsed,"no cloud switch for a season without a cloud deck");
            var tempCard=((StackPanel)window.FindName("LayerList")).Children.OfType<RadioButton>().First(c=>Equals(c.Tag,"temperature")); tempCard.IsChecked=true; Call(window,"RefreshTerrain");
            Assert(!globe.Space,"data themes leave space mode");
            cloudToggle.IsChecked=false; ((RadioButton)window.FindName("ViewMapButton")).IsChecked=true;
            Call(window,"SetAtlas",WorldClimateAtlas.Read(atlasDir)); ((StackPanel)window.FindName("LayerList")).Children.OfType<RadioButton>().ToArray()[1].IsChecked=true; Call(window,"RefreshTerrain");
            Call(window,"SetTheme",true,false); content.UpdateLayout();
            var paper=((System.Windows.Media.SolidColorBrush)Application.Current.Resources["Paper"]).Color;
            Assert(paper==System.Windows.Media.Color.FromRgb(0x15,0x17,0x1B) && ThemeManager.Dark,"dark theme swaps the palette");
            Assert(((System.Windows.Media.SolidColorBrush)window.Background).Color==paper,"window follows the theme at once");
            Assert(Equals(((Button)window.FindName("ThemeButton")).Content,"☀"),"theme button offers the way back");
            Call(window,"SetTheme",false,false);
            Assert(((System.Windows.Media.SolidColorBrush)window.Background).Color==System.Windows.Media.Color.FromRgb(0xEF,0xE9,0xDD),"back to the light theme");
            Call(window,"SetTextScale",new AppSettings().TextScale,false);
            Assert(Math.Abs(((System.Windows.Media.ScaleTransform)window.FindName("OverlayScale")).ScaleX-1.2)<1e-9 && ((TextBlock)window.FindName("TextSizeLabel")).Text.Contains("100"),"default text size is the enlarged comfortable one");
            Call(window,"SetTextScale",1.2,false); content.UpdateLayout();
            var scale=(System.Windows.Media.ScaleTransform)window.FindName("OverlayScale");
            Assert(Math.Abs(scale.ScaleX-1.2*1.2)<1e-9 && Math.Abs(map.Margin.Top-84*1.2*1.2)<1e-6,"text size scales the cards and the map makes room");
            Assert(((TextBlock)window.FindName("TextSizeLabel")).Text.Contains("120"),"text size shown");
            Call(window,"SetTextScale",3.0,false); Assert(Math.Abs(scale.ScaleX-1.3*1.2)<1e-9,"text size limited to 130%");
            Call(window,"SetTextScale",1.0,false);
            ((RadioButton)window.FindName("ViewSplitButton")).IsChecked=true; content.UpdateLayout();
            Assert(map.Visibility==Visibility.Visible && globe.Visibility==Visibility.Visible,"split view shows map and globe together");
            Assert(map.Margin.Right>=content.ActualWidth-globe.Margin.Left,"map and globe do not overlap");
            ((RadioButton)window.FindName("ViewMapButton")).IsChecked=true;
            Assert(globe.Visibility!=Visibility.Visible,"back to the map alone");
            ((RadioButton)window.FindName("ModeTerrain")).IsChecked=true;
            Assert(((FrameworkElement)window.FindName("LegendCard")).Visibility!=Visibility.Visible,"terrain mode shows terrain, not climate");
            ((RadioButton)window.FindName("FlattenTool")).IsChecked=true;
            Stroke(window,map,"StrokeStarted",new GeoPoint(10,10)); Stroke(window,map,"StrokeFinished"); Call(window,"RefreshTerrain");
            ((RadioButton)window.FindName("ModeClimate")).IsChecked=true; Call(window,"RefreshTerrain");
            Assert(resultPanel.Visibility!=Visibility.Visible && ((FrameworkElement)window.FindName("LegendCard")).Visibility!=Visibility.Visible
                   && ((FrameworkElement)window.FindName("RunCard")).Visibility==Visibility.Visible,"edited terrain hides the old climate and asks to recompute");
            Call(window,"Undo_Click",window,new RoutedEventArgs()); Call(window,"RefreshTerrain");
            Assert(resultPanel.Visibility==Visibility.Visible,"undo back to the computed terrain shows its climate again");
            ((RadioButton)window.FindName("ModeTerrain")).IsChecked=true;
            Directory.Delete(atlasDir,true);
            Console.WriteLine("PASS WPF climate layers: synthetic atlas, layer over terrain on map and globe, legend, point climate, stale after edit, undo");
            var bitmap=new RenderTargetBitmap(1440,890,96,96,System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(content);
            var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using(var output=File.Create(Path.Combine(dir,"editor.png"))) png.Save(output);
            File.WriteAllText(Path.Combine(dir,"report.json"),JsonSerializer.Serialize(new { status="passed", measurements, cells=loaded.Terrain.CellCount, spacingKm=loaded.SpacingKm, hash=loaded.Fingerprint() },new JsonSerializerOptions {WriteIndented=true}));
            Console.WriteLine("PASS WPF map/globe editing, four tools, undo/redo, save/reload, shared texture and offscreen render");
            var solverDir=Path.Combine(Environment.CurrentDirectory,"artifacts","climate-solver-v2-20261018");
            Directory.CreateDirectory(solverDir);
            var catalog=ClimateSolutionCatalog.Read(Path.Combine(AppContext.BaseDirectory,"Fixtures","ClimateAlternatives","solutions.json"));
            var comparison=new ClimateSolutionsWindow(catalog,new string('a',64));
            Assert(comparison.DisplayedAlternatives==2 && comparison.MatchesCurrentTerrain,"multiple solutions with matching terrain");
            comparison.Select(catalog.Alternatives[1].Id);
            Assert(comparison.SelectedCandidateId==catalog.Alternatives[1].Id,"select second alternative, not an average");
            Assert(comparison.SelectedSeasonIndex is null,"initially show all seasons");
            LogicalNodes(comparison).OfType<RadioButton>().Single(x=>x.Name=="ClimateSeason3").IsChecked=true;
            Assert(comparison.SelectedSeasonIndex==3,"winter selector event changes displayed phase");
            var selection=LogicalNodes(comparison).OfType<StackPanel>().Single(x=>x.Name=="ClimateSelectionDetails");
            var compare=LogicalNodes(comparison).OfType<StackPanel>().Single(x=>x.Name=="ClimateSeasonComparison");
            Assert(LogicalNodes(selection).OfType<TextBlock>().Any(t=>t.Text==catalog.Alternatives[1].TemperatureC[3].ToString("F2")),"selected alternative winter temperature");
            Assert(LogicalNodes(selection).OfType<Grid>().Single().RowDefinitions.Count==2,"one selected season, not four rows");
            foreach(var alternative in catalog.Alternatives)
                Assert(LogicalNodes(compare).OfType<TextBlock>().Any(t=>t.Text==alternative.TemperatureC[3].ToString("F2")),"same winter in comparison table");
            LogicalNodes(comparison).OfType<RadioButton>().Single(x=>Equals(x.Content,catalog.Alternatives[0].Label)).IsChecked=true;
            Assert(comparison.SelectedCandidateId==catalog.Alternatives[0].Id && comparison.SelectedSeasonIndex==3,"candidate switch preserves selected winter");
            Assert(LogicalNodes(selection).OfType<TextBlock>().Any(t=>t.Text==catalog.Alternatives[0].TemperatureC[3].ToString("F2")),"new candidate's own winter values");
            LogicalNodes(comparison).OfType<RadioButton>().Single(x=>x.Name=="ClimateSeason0").IsChecked=true;
            Assert(comparison.SelectedSeasonIndex==0,"spring can be selected independently");
            Assert(LogicalNodes(selection).OfType<TextBlock>().Any(t=>t.Text==catalog.Alternatives[0].RainMmDay[0].ToString("F3")),"selected spring rain");
            LogicalNodes(comparison).OfType<RadioButton>().Single(x=>x.Name=="ClimateAllSeasons").IsChecked=true;
            Assert(comparison.SelectedSeasonIndex is null && LogicalNodes(selection).OfType<Grid>().Single().RowDefinitions.Count==5,"return to all four seasons");
            comparison.Select(catalog.Alternatives[1].Id); comparison.SelectSeason(3);
            var invalidRejected=false;
            try { comparison.SelectSeason(4); } catch(ArgumentOutOfRangeException) { invalidRejected=true; }
            Assert(invalidRejected && comparison.SelectedSeasonIndex==3,"invalid season cannot alter selection");
            var wrong=new ClimateSolutionsWindow(catalog,new string('b',64));
            Assert(!wrong.MatchesCurrentTerrain,"other terrain result is marked");
            Assert(LogicalNodes(comparison).OfType<RadioButton>().Single(x=>x.Name=="ClimateSeason3").IsChecked==true,"another window cannot reset selected season");
            var comparisonContent=(ScrollViewer)comparison.Content;
            comparisonContent.Background=comparison.Background;
            comparisonContent.Measure(new Size(1000,700)); comparisonContent.Arrange(new Rect(0,0,1000,700));comparisonContent.UpdateLayout();
            var compared=new RenderTargetBitmap(1000,700,96,96,System.Windows.Media.PixelFormats.Pbgra32);compared.Render(comparisonContent);
            var resultPng=new PngBitmapEncoder();resultPng.Frames.Add(BitmapFrame.Create(compared));
            var seasonDir=Path.Combine(Environment.CurrentDirectory,"artifacts","climate-season-selector-20261018");Directory.CreateDirectory(seasonDir);
            using(var output=File.Create(Path.Combine(seasonDir,"winter-comparison-fixture.png")))resultPng.Save(output);
            var empty=new ClimateSolutionsWindow(catalog with { Alternatives=Array.Empty<ClimateAlternative>() },new string('a',64));
            Assert(empty.DisplayedAlternatives==0 && empty.SelectedCandidateId is null,"no approved result never fabricated");
            Assert(!LogicalNodes(empty).OfType<RadioButton>().Any(),"no season choices without a result");
            var twoPhases=catalog with { Alternatives=catalog.Alternatives.Select(c=>c with { TemperatureC=c.TemperatureC[..2],RainMmDay=c.RainMmDay[..2],SnowFraction=c.SnowFraction[..2] }).ToArray() };
            var twoPhaseWindow=new ClimateSolutionsWindow(twoPhases,new string('a',64));
            LogicalNodes(twoPhaseWindow).OfType<RadioButton>().Single(x=>x.Name=="ClimateSeason1").IsChecked=true;
            Assert(twoPhaseWindow.SelectedSeasonIndex==1 && LogicalNodes(twoPhaseWindow).OfType<RadioButton>().Any(x=>Equals(x.Content,"계절 구간 2/2")),"actual phase count used, not forced to four");
            Console.WriteLine("PASS WPF climate alternatives, separate selection, wrong terrain, empty results, offscreen render (synthetic fixture)");
            Console.WriteLine("PASS WPF season controls: independent candidate/season selection, preserved season, same-season comparison, all seasons, multiple windows, empty results and non-four-phase data");
            if(Environment.GetEnvironmentVariable("GARDEN_CLIMATE_E2E")=="1") ClimateEndToEnd(window,content,measurements);
            Console.WriteLine(JsonSerializer.Serialize(measurements)); return 0;
        }
        catch(Exception e) { Console.WriteLine(e); return 1; }
        finally
        {
            if(window is not null) foreach(var name in new[]{"refreshTimer","climateTimer"}) ((DispatcherTimer)window.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).Stop();
            app.Shutdown();
        }
    }
    /// <summary>Real run: the app saves the example world, starts the WSL model, reads the result and shows layers.</summary>
    private static void ClimateEndToEnd(MainWindow window,Grid content,Dictionary<string,double> measurements)
    {
        var world=TerrainExamples.Create(); Call(window,"SetWorld",world,null);
        var watch=Stopwatch.StartNew();
        var reuse=Environment.GetEnvironmentVariable("GARDEN_CLIMATE_REUSE")=="1" && window.GetType().GetField("atlas",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window) is not null;
        WorldClimateJob? job=null;
        if(!reuse)
        {
            Call(window,"ClimateRun_Click",window,new RoutedEventArgs());
            job=(WorldClimateJob)window.GetType().GetField("atlasJob",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            var frame=new DispatcherFrame(); var poll=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
            poll.Tick+=(_,_)=>{ job.Refresh(); if(job.IsFinished||watch.Elapsed>TimeSpan.FromMinutes(40)) frame.Continue=false; };
            poll.Start(); Dispatcher.PushFrame(frame); poll.Stop();
            Call(window,"TakeFinishedAtlas"); Call(window,"UpdateClimatePanel");
            measurements["climate_e2e_s"]=watch.Elapsed.TotalSeconds;
            Assert(job.Phase=="completed" && job.Result is not null,"end-to-end climate run: "+job.Phase+" "+job.Error);
        }
        var shown=(WorldClimateAtlas)window.GetType().GetField("atlas",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
        var dir=Path.Combine(Environment.CurrentDirectory,"artifacts","worldclimate-app-20261021","e2e"); Directory.CreateDirectory(dir);
        ((RadioButton)window.FindName("ModeClimate")).IsChecked=true; Call(window,"RefreshTerrain");
        var layers=((StackPanel)window.FindName("LayerList")).Children.OfType<RadioButton>().ToArray();
        Assert(((FrameworkElement)window.FindName("ClimateResultPanel")).Visibility==Visibility.Visible && layers.Length>=8,"theme cards after a real run");
        void Shot(string name)
        {
            content.Width=1600; content.Height=960; content.Measure(new Size(1600,960)); content.Arrange(new Rect(0,0,1600,960)); content.UpdateLayout();
            var bitmap=new RenderTargetBitmap(1600,960,96,96,System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(content);
            var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var output=File.Create(Path.Combine(dir,name)); png.Save(output);
        }
        for(var i=0;i<layers.Length;i++) { layers[i].IsChecked=true; Call(window,"RefreshTerrain"); Shot($"app-{i:00}-{layers[i].Tag}.png"); ((RadioButton)window.FindName("ViewMapButton")).IsChecked=true; }
        if(layers.FirstOrDefault(l=>Equals(l.Tag,"satellite")) is { } sat)
        {
            sat.IsChecked=true; Call(window,"RefreshTerrain"); Assert(((GlobeView)window.FindName("Globe")).Space,"space view after a real run");
            foreach(var season in ((StackPanel)window.FindName("SeasonList")).Children.OfType<RadioButton>().ToArray()) { season.IsChecked=true; Call(window,"RefreshTerrain"); Shot($"app-space-season-{season.Tag}.png"); }
            var clouds=(System.Windows.Controls.Primitives.ToggleButton)window.FindName("CloudToggle"); clouds.IsChecked=true; Shot("app-space-clouds.png");
            ((RadioButton)window.FindName("ViewMapButton")).IsChecked=true; Call(window,"RefreshTerrain"); Shot("app-space-map-clouds.png");
            clouds.IsChecked=false; Shot("app-space-map.png");
            ((RadioButton)window.FindName("ViewSplitButton")).IsChecked=true; Shot("app-space-split.png");
            ((RadioButton)window.FindName("ViewMapButton")).IsChecked=true;
        }
        layers.First(l=>Equals(l.Tag,"temperature")).IsChecked=true;
        foreach(var season in ((StackPanel)window.FindName("SeasonList")).Children.OfType<RadioButton>().ToArray()) { season.IsChecked=true; Call(window,"RefreshTerrain"); Shot($"app-temperature-season-{season.Tag}.png"); }
        layers.First(l=>Equals(l.Tag,"koppen")).IsChecked=true; Call(window,"PointAt",new GeoPoint(18,-40)); Call(window,"RefreshTerrain"); Shot("app-inspector-koppen.png");
        Call(window,"SetTheme",true,false); Call(window,"RefreshTerrain"); Shot("app-dark-koppen.png");
        layers.First(l=>Equals(l.Tag,"temperature")).IsChecked=true; Call(window,"RefreshTerrain"); Shot("app-dark-temperature.png");
        ((RadioButton)window.FindName("ModeTerrain")).IsChecked=true; ((RadioButton)window.FindName("FlattenTool")).IsChecked=true; Call(window,"RefreshTerrain"); Shot("app-dark-terrain.png");
        ((RadioButton)window.FindName("NavigateTool")).IsChecked=true; ((RadioButton)window.FindName("ModeClimate")).IsChecked=true;
        layers.First(l=>Equals(l.Tag,"koppen")).IsChecked=true; Call(window,"SetTheme",false,false); Call(window,"RefreshTerrain");
        Call(window,"SetTextScale",.8,false); Call(window,"RefreshTerrain"); Shot("app-text-80.png");
        Call(window,"SetTextScale",1.3,false); Call(window,"RefreshTerrain"); Shot("app-text-130.png");
        Call(window,"SetTextScale",1.0,false);
        ((RadioButton)window.FindName("ViewGlobeButton")).IsChecked=true; Shot("app-globe-koppen.png");
        ((RadioButton)window.FindName("ViewSplitButton")).IsChecked=true; Shot("app-split-koppen.png");
        ((RadioButton)window.FindName("ModeTerrain")).IsChecked=true; ((RadioButton)window.FindName("FlattenTool")).IsChecked=true; Call(window,"RefreshTerrain"); Shot("app-split-terrain.png");
        ((RadioButton)window.FindName("ModeClimate")).IsChecked=true;
        ((RadioButton)window.FindName("ViewMapButton")).IsChecked=true;
        ((RadioButton)window.FindName("ModeTerrain")).IsChecked=true; ((RadioButton)window.FindName("FlattenTool")).IsChecked=true; Call(window,"RefreshTerrain"); Shot("app-terrain-mode.png");
        ((RadioButton)window.FindName("NavigateTool")).IsChecked=true;
        Call(window,"SetWorld",new World("새로운 세계"),null); ((RadioButton)window.FindName("ModeClimate")).IsChecked=true; Call(window,"RefreshTerrain"); Shot("app-climate-not-computed.png");
        File.WriteAllText(Path.Combine(dir,"e2e.json"),JsonSerializer.Serialize(new { reused=reuse, job?.JobId, seconds=watch.Elapsed.TotalSeconds, shown.DirectoryPath, states=shown.States.Select(st=>new{st.Label,st.GlobalMeanC,st.Stable}),
            point=((TextBlock)window.FindName("ClimatePointText")).Text },new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
        Console.WriteLine(reuse ? $"PASS climate layers from the saved result, {layers.Length-1} layers rendered in the app"
                                : $"PASS climate end-to-end: WSL model run {watch.Elapsed.TotalSeconds:F0} s, {layers.Length-1} layers rendered in the app");
    }
    private static void Assert(bool value,string message) { if(!value) throw new Exception(message); }
    private static IEnumerable<DependencyObject> LogicalNodes(DependencyObject root)
    {
        yield return root;
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach(var node in LogicalNodes(child))yield return node;
    }
    private static object? Call(MainWindow w,string method,params object?[] args) => w.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,args);
    private static void Stroke(MainWindow w,TerrainView view,string name,params object?[] args)
    {
        var action=(Delegate)typeof(TerrainView).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
        action.DynamicInvoke(args);
    }
}

static class AtlasFixture
{
    public static string Write(string dir, string hash, int width, int height, System.Action<string>? writeLayer = null, bool satellite = false)
    {
        var extra = satellite ? @",{""id"":""satellite_summer"",""title"":""우주에서 본 모습 · 6-8월"",""file"":""layers/satellite_summer.png"",""legend"":""legends/satellite_summer.png"",""kind"":""category"",""legend_items"":[[""깊은 바다"",""#081433""]]},{""id"":""satellite_winter"",""title"":""우주에서 본 모습 · 12-2월"",""file"":""layers/satellite_winter.png"",""legend"":""legends/satellite_winter.png"",""kind"":""category"",""legend_items"":[[""깊은 바다"",""#081433""]]},{""id"":""clouds_summer"",""title"":""구름 · 6-8월"",""file"":""layers/clouds_summer.png"",""legend"":""legends/clouds_summer.png"",""kind"":""overlay""}" : "";
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "state-0", "layers"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "state-0", "legends"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.json"), $$"""
        {"model":"worldclimate test fixture","name":"fixture","terrain_sha256":"{{hash}}","created_utc":"2026-10-21T00:00:00+00:00","seconds":1.5,
         "frame":{"width":{{width}},"height":{{height}}},"land_fraction":0.3,"deepwater_sites":[[60.0,-20.0]],
         "deepwater_suggestions":[{"lat":63.0,"lon":-18.0,"area_mkm2":11.2,"circumpolar":false}],
         "states":[{"folder":"state-0","label":"따뜻한 기후안","start":"warm","stable":true,"floquet":[0.8],"global_mean_c":14.0,"global_precip_mm_day":2.5,
           "koppen_group_share":{"A":0.2,"B":0.3},"koppen_land_share":{},"biome_land_share":{},"closed_basins":0,
           "layers":[{"id":"temperature_annual","title":"연평균 기온","file":"layers/temperature_annual.png","legend":"legends/temperature_annual.png","kind":"field"}{{extra}}]}],
         "not_converged":[]}
        """);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "state-0", "climate.json"), """
        {"summary":{},"cells":[
         {"lat":0.0,"lon":0.0,"land":1.0,"height_m":120,"T_c":[25.0,26.0,25.5,24.5],"P_mm_day":[5.0,6.0,5.5,4.5],"sea_ice":0.0,"koppen":"Af","koppen_name":"열대 우림","biome":"열대 우림","snow_seasons":0,"glacier":false,"soil_wetness_min":0.9,"explanation":"열대 우림: 시험 설명"},
         {"lat":60.0,"lon":-20.0,"land":0.0,"height_m":0,"T_c":[4.0,9.0,6.0,2.0],"P_mm_day":[2.0,2.0,3.0,3.0],"sea_ice":0.0},
         {"lat":-70.0,"lon":120.0,"land":1.0,"height_m":2500,"T_c":[-30.0,-40.0,-30.0,-15.0],"P_mm_day":[0.2,0.1,0.2,0.3],"sea_ice":0.9,"koppen":"EF","koppen_name":"빙설","biome":"빙원","snow_seasons":4,"glacier":true,"soil_wetness_min":1.0,"explanation":"빙설"}]}
        """);
        writeLayer?.Invoke(System.IO.Path.Combine(dir, "state-0", "layers", "temperature_annual.png"));
        writeLayer?.Invoke(System.IO.Path.Combine(dir, "state-0", "legends", "temperature_annual.png"));
        if (satellite)
            foreach (var id in new[] { "satellite_summer", "satellite_winter", "clouds_summer" }) writeLayer?.Invoke(System.IO.Path.Combine(dir, "state-0", "layers", id + ".png"));
        return dir;
    }
}
