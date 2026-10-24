using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TheGarden.Core.Climate;

namespace TheGarden.Desktop;

public sealed class ClimateSolutionsWindow : Window
{
    public string? SelectedCandidateId { get; private set; }
    public int? SelectedSeasonIndex { get; private set; }
    public bool MatchesCurrentTerrain { get; }
    public int DisplayedAlternatives { get; }
    private readonly StackPanel details = new() { Name="ClimateSelectionDetails" };
    private readonly StackPanel comparison = new() { Name="ClimateSeasonComparison" };
    private readonly Dictionary<string,RadioButton> candidateButtons = new();
    private readonly List<(int? Index, RadioButton Button)> seasonButtons = new();
    private bool synchronizingSelection;
    private readonly ClimateSolutionCatalog catalog;

    public ClimateSolutionsWindow(ClimateSolutionCatalog catalog, string currentTerrainId)
    {
        this.catalog = catalog;
        MatchesCurrentTerrain = catalog.Matches(currentTerrainId); DisplayedAlternatives = catalog.Alternatives.Count;
        Title = "Map · 기후안 비교"; Width = 1040; Height = 760; MinWidth = 830; MinHeight = 580;
        Background = new SolidColorBrush(Color.FromRgb(11,19,28)); Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic"); FontSize=13;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Margin = new Thickness(26) };
        body.Children.Add(Text("같은 지형, 가능한 기후들", 26));
        body.Children.Add(Text(MatchesCurrentTerrain ? "현재 지형의 결과" : "다른 지형의 결과 · 현재 지형에 적용되지 않습니다.", 13, "#E4C58A"));
        if (catalog.Alternatives.Any(x => x.SyntheticFixture))
            body.Children.Add(Text("화면 검사용 자료 · 실제 기후 계산 결과가 아닙니다.", 14, "#E4C58A"));
        body.Children.Add(Text($"원식 검증 통과 {DisplayedAlternatives}개 · {catalog.SearchLabel}\n찾은 후보를 표시합니다. 가능한 모든 해를 찾았다는 뜻은 아닙니다.", 12, "#93A7B9"));
        if (DisplayedAlternatives == 0)
            body.Children.Add(Text("표시할 기후안이 없습니다. 이번 탐색에서 승인된 결과를 얻지 못했습니다.", 16));
        else
        {
            var group=Guid.NewGuid().ToString("N");
            body.Children.Add(Text("기후안 선택", 12, "#93A7B9"));
            var choices = new WrapPanel { Margin = new Thickness(0, 14, 0, 8) };
            foreach (var candidate in catalog.Alternatives)
            {
                var button = Choice(candidate.Label,"ClimateAlternative"+group);
                button.Checked += (_, _) => { if(!synchronizingSelection) Select(candidate.Id); };
                choices.Children.Add(button);candidateButtons.Add(candidate.Id,button);
            }
            body.Children.Add(choices);
            var phases = catalog.Alternatives[0].TemperatureC.Length;
            body.Children.Add(Text("계절 선택", 12, "#93A7B9"));
            var seasons=new WrapPanel { Margin=new Thickness(0,0,0,8) };
            for(var option=-1;option<phases;option++)
            {
                int? index=option<0?null:option;
                var button=Choice(index is int i?SeasonName(i,phases):"전체 계절","ClimateSeason"+group);
                button.Name=index is int j?$"ClimateSeason{j}":"ClimateAllSeasons";
                if(index is int k)button.ToolTip=Season(k,phases);
                button.Checked+=(_,_)=> { if(!synchronizingSelection) SelectSeason(index); };
                seasonButtons.Add((index,button));seasons.Children.Add(button);
            }
            body.Children.Add(seasons);
            if(phases==4)body.Children.Add(Text("봄·여름·가을·겨울은 북반구 기준입니다. 남반구의 계절은 반대입니다.",11,"#93A7B9"));
            body.Children.Add(details);body.Children.Add(comparison);
            Select(catalog.Alternatives[0].Id);
        }
        body.Children.Add(Text("계절 구간은 공전 중 춘분을 시작으로 나눕니다. 전 지구 평균 요약이며 지역별 상태는 각 기후안의 파일에 별도로 보존됩니다.", 11, "#93A7B9"));
        var technical = new Expander { Header = "계산 기록", Margin = new Thickness(0, 12, 0, 0), Foreground = Brushes.LightGray };
        technical.Content = Text($"모델  {catalog.ModelId}\n지형  {catalog.TerrainId}\n물리 설정  {catalog.ProfileId}\n탐색 시도  {catalog.Attempts}회\n목록  {catalog.SourcePath}", 11, "#93A7B9");
        body.Children.Add(technical);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public void Select(string id)
    {
        var c = catalog.Alternatives.Single(x => x.Id == id); SelectedCandidateId = c.Id;
        RefreshSelection();
    }

    public void SelectSeason(int? index)
    {
        if(catalog.Alternatives.Count==0)throw new InvalidOperationException("선택할 기후안이 없습니다.");
        if(index is int i && (i<0 || i>=catalog.Alternatives[0].TemperatureC.Length))
            throw new ArgumentOutOfRangeException(nameof(index));
        SelectedSeasonIndex=index;RefreshSelection();
    }

    private void RefreshSelection()
    {
        if(SelectedCandidateId is null)return;
        var c=catalog.Alternatives.Single(x=>x.Id==SelectedCandidateId);
        synchronizingSelection=true;
        try
        {
            foreach(var pair in candidateButtons)pair.Value.IsChecked=pair.Key==SelectedCandidateId;
            foreach(var choice in seasonButtons)choice.Button.IsChecked=choice.Index==SelectedSeasonIndex;
        }
        finally { synchronizingSelection=false; }
        details.Children.Clear();comparison.Children.Clear();
        var phases=c.TemperatureC.Length;
        var selectedLabel=SelectedSeasonIndex is int selected?Season(selected,phases):"전체 계절";
        details.Children.Add(Text($"{c.Label} · {selectedLabel}", 18));
        var note = c.Kind == "local_family_sample"
            ? "주변에서 연속적으로 달라질 가능성이 있는 해의 표본입니다. 각 표본을 원래 식으로 다시 확인했습니다."
            : $"{c.KindLabel} · 선택한 계절의 전 지구 평균 요약입니다.";
        details.Children.Add(Text(note, 12, "#93A7B9"));
        var indices=SelectedSeasonIndex is int phase?new[]{phase}:Enumerable.Range(0,phases);
        details.Children.Add(Table(["공전 구간", "지표온도 (°C)", "강수 (mm/일)", "눈 피복 (%)"],
            indices.Select(i => new[] { Season(i,phases),
                c.TemperatureC[i].ToString("F2"), c.RainMmDay[i].ToString("F3"), (100*c.SnowFraction[i]).ToString("F1") })));
        var scope = c.Scope == "zero_water" ? "물 재고가 0인 건조 가지" : c.Scope == "open_surface" ? "얼지 않은 수면 가지" : c.Scope;
        details.Children.Add(Text($"계산 범위: {scope} · 원식 승인 잔차 {c.Residual:E2}", 11, "#93A7B9"));
        Grid table;
        if(SelectedSeasonIndex is int season)
        {
            comparison.Children.Add(Text($"기후안 비교 · {Season(season,phases)}",16));
            table=Table(["기후안","지표온도 (°C)","강수 (mm/일)","눈 피복 (%)"],
                catalog.Alternatives.Select(a=>new[]{a.Label,a.TemperatureC[season].ToString("F2"),
                    a.RainMmDay[season].ToString("F3"),(100*a.SnowFraction[season]).ToString("F1")}));
        }
        else
        {
            comparison.Children.Add(Text("기후안 비교 · 계절별 전 지구 평균 지표온도 (°C)",16));
            table=Table(new[]{"기후안"}.Concat(Enumerable.Range(0,phases).Select(i=>Season(i,phases))).ToArray(),
                catalog.Alternatives.Select(a=>new[]{a.Label}.Concat(a.TemperatureC.Select(t=>t.ToString("F2"))).ToArray()));
        }
        comparison.Children.Add(new ScrollViewer { Content=table,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled });
    }

    private RadioButton Choice(string label,string group) => new()
    {
        Content=label,GroupName=group,Margin=new Thickness(0,0,8,4),
        Style=TryFindResource(typeof(RadioButton)) as Style
    };
    private static string SeasonName(int index,int count)=>count==4?new[]{"봄","여름","가을","겨울"}[index]:$"계절 구간 {index+1}/{count}";
    private static string Season(int index, int count) => count == 4
        ? SeasonName(index,count)+" · "+new[] { "춘분 → 하지", "하지 → 추분", "추분 → 동지", "동지 → 춘분" }[index]
        : $"계절 구간 {index+1}/{count}";
    private static TextBlock Text(string value, double size, string color = "#EDF2F4") => new()
    { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), Margin = new Thickness(0, 5, 0, 8) };
    private static Grid Table(string[] headers, IEnumerable<string[]> values)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 18) };
        foreach (var _ in headers) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 125 });
        var rows = new[] { headers }.Concat(values).ToArray();
        for (var row = 0; row < rows.Length; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var col = 0; col < headers.Length; col++)
            {
                var label=Text(rows[row][col],12);label.Margin=new Thickness(0);
                var cell = new Border { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(row == 0 ? "#243F3D" : row % 2 == 0 ? "#152431" : "#111D28")), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 1, 1), Child = label };
                Grid.SetRow(cell, row); Grid.SetColumn(cell, col); grid.Children.Add(cell);
            }
        }
        return grid;
    }
}
