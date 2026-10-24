using System.Text.Json;

namespace TheGarden.Core.Climate;

/// <summary>Execution evidence from a bounded experiment, never a validated climatology.</summary>
public sealed record ClimateTrialSummary(string TerrainSha256, DateTimeOffset CreatedUtc,
    string Model, string Version, int ModelYears, double RunSeconds,
    double SurfaceTemperatureKelvin, double PrecipitationMmDay, double ToaNetWattsPerSquareMeter,
    string ReportPath)
{
    public bool Matches(string terrainSha256) => string.Equals(TerrainSha256, terrainSha256, StringComparison.Ordinal);
    public string StateLabel(string terrainSha256) => Matches(terrainSha256) ? "시험 완료 · 미수렴" : "재계산 필요";

    public static IReadOnlyList<ClimateTrialSummary> Read(string path)
    {
        if (new FileInfo(path).Length > 8*1024*1024) throw new InvalidDataException("계산 기록이 너무 큽니다.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.GetProperty("schema_version").GetInt32() != 1) throw new InvalidDataException("지원하지 않는 계산 기록입니다.");
        var time = root.GetProperty("created_utc").GetDateTimeOffset();
        var output = new List<ClimateTrialSummary>();
        foreach (var item in root.GetProperty("experiments").EnumerateArray())
        {
            if (item.GetProperty("execution_status").GetString() != "completed") continue;
            var result = item.GetProperty("result");
            if (result.GetProperty("climate_status").GetString() != "unconverged" || result.GetProperty("convergence_assessed").GetBoolean())
                throw new InvalidDataException("이 화면은 미수렴 시험 결과만 지원합니다.");
            var hash = item.GetProperty("input").GetProperty("terrain_sha256").GetString() ?? "";
            if (hash.Length != 64 || hash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("입력 지형 식별자가 올바르지 않습니다.");
            var years = result.GetProperty("simulated_years").GetInt32();
            var annual = result.GetProperty("annual");
            if (years is < 1 or > 5 || annual.GetArrayLength() != years) throw new InvalidDataException("시험 기간 기록이 올바르지 않습니다.");
            var last = annual[years-1];
            var model = item.GetProperty("provenance");
            if (model.GetProperty("model").GetString() != "ExoPlaSim" || model.GetProperty("version").GetString() != "3.4.2")
                throw new InvalidDataException("지원하지 않는 시험 모델입니다.");
            output.Add(new(hash, time, "ExoPlaSim", "3.4.2", years,
                Number(item.GetProperty("measurement"), "run_seconds"),
                Number(last, "global_surface_temperature_k"), Number(last, "precipitation_mm_day"),
                Number(last, "toa_net_downward_w_m2"), Path.GetFullPath(path)));
        }
        return output;
    }
    private static double Number(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetDouble();
        if (!double.IsFinite(value)) throw new InvalidDataException("계산 기록에 유효하지 않은 수치가 있습니다.");
        return value;
    }
}
