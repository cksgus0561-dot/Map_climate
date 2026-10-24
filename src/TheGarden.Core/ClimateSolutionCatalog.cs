using System.Security.Cryptography;
using System.Text.Json;

namespace TheGarden.Core.Climate;

public sealed record ClimateAlternative(string Id, string Label, string Kind, int? FreeDimensions,
    string? RelatedTo, double Residual, double[] TemperatureC, double[] RainMmDay, double[] SnowFraction,
    string Scope, string StatePath, bool SyntheticFixture)
{
    public string KindLabel => Kind switch
    {
        "local_family_sample" => "연속적으로 달라질 수 있는 후보",
        "isolated_candidate" => "개별 후보",
        _ => "해의 연결 관계 미분류"
    };
}

/// <summary>Reads accepted alternatives, preserving each state and its terrain identity.</summary>
public sealed record ClimateSolutionCatalog(string TerrainId, string ModelId, string ProfileId,
    string StopReason, IReadOnlyList<ClimateAlternative> Alternatives, int Attempts, string SourcePath)
{
    public bool Matches(string terrainId) => string.Equals(TerrainId, terrainId, StringComparison.OrdinalIgnoreCase);
    public string SearchLabel => StopReason switch
    {
        "result_limit" => "설정한 후보 수에 도달",
        "attempt_limit" => "설정한 탐색 횟수에 도달",
        "time_limit" => "시간 한도에 도달",
        "evaluation_limit" => "계산량 한도에 도달",
        "cancelled" => "탐색 취소됨",
        "planned_search_finished" => "계획한 탐색을 마침",
        "unsupported_or_invalid" or "unsupported_branch" => "지원 범위를 벗어나 탐색 중단",
        _ => "이번 탐색 종료"
    };

    public static ClimateSolutionCatalog Read(string path)
    {
        path = Path.GetFullPath(path);
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("기후안 목록이 너무 큽니다.");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.GetProperty("schema").GetString() != "garden-climate-solutions-v1")
                throw new InvalidDataException("지원하지 않는 기후안 형식입니다.");
            var terrain = Text(root, "terrain_id"); var profile = Text(root, "profile_id");
            if (!Hash(terrain) || !Hash(profile)) throw new InvalidDataException("지형·물리 설정 식별자가 올바르지 않습니다.");
            // This engine does not certify exhaustive enumeration, even if a file asserts it.
            var alternatives = new List<ClimateAlternative>(); var ids = new HashSet<string>();
            int? phases = null;
            foreach (var item in root.GetProperty("candidates").EnumerateArray())
            {
                if (!item.GetProperty("accepted").GetBoolean() ||
                    !item.GetProperty("checks").TryGetProperty("accepted", out var accepted) || !accepted.GetBoolean())
                    throw new InvalidDataException("원식 검증을 통과하지 않은 후보가 포함되어 있습니다.");
                var id = Text(item, "id"); if (!ids.Add(id)) throw new InvalidDataException("기후안 식별자가 중복됩니다.");
                var file = Text(item, "state_file");
                if (file != Path.GetFileName(file) || file.Contains('/') || file.Contains('\\') || Path.IsPathRooted(file))
                    throw new InvalidDataException("기후안 상태 파일 경로가 올바르지 않습니다.");
                var state = Path.Combine(Path.GetDirectoryName(path)!, file);
                var hash = Text(item, "state_sha256");
                using (var stream = File.OpenRead(state))
                    if (!Hash(hash) || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("기후안 상태 파일이 변경되었거나 손상되었습니다.");
                var summary = item.GetProperty("summary");
                var t = Values(summary, "seasonal_surface_temperature_c");
                var p = Values(summary, "seasonal_precipitation_mm_per_day");
                var s = Values(summary, "seasonal_snow_cover_fraction");
                if (t.Length < 2 || t.Length != p.Length || t.Length != s.Length ||
                    (phases is not null && phases != t.Length) || p.Any(x => x < 0) || s.Any(x => x < 0 || x > 1))
                    throw new InvalidDataException("계절별 기후값의 범위나 개수가 올바르지 않습니다.");
                phases = t.Length;
                var residual = item.GetProperty("max_scaled_residual").GetDouble();
                if (!double.IsFinite(residual) || residual < 0 || residual > 1e-7)
                    throw new InvalidDataException("기후안의 원식 잔차가 승인 기준을 넘습니다.");
                int? free = item.GetProperty("local_free_dimension_estimate").ValueKind == JsonValueKind.Null
                    ? null : item.GetProperty("local_free_dimension_estimate").GetInt32();
                if (free < 0) throw new InvalidDataException("해의 자유도 기록이 올바르지 않습니다.");
                alternatives.Add(new(id, Text(item, "label"), Text(item, "kind"), free,
                    item.TryGetProperty("related_to", out var related) ? related.GetString() : null,
                    residual, t, p, s, Text(summary, "scope"), state,
                    summary.TryGetProperty("synthetic_fixture", out var fixture) && fixture.GetBoolean()));
            }
            return new(terrain, Text(root, "model_id"), profile, Text(root, "stop_reason"), alternatives,
                root.GetProperty("attempts").GetArrayLength(), path);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("기후안 목록의 내용이 올바르지 않습니다.", ex); }
    }
    private static string Text(JsonElement item, string name) => item.GetProperty(name).GetString()
        ?? throw new InvalidDataException($"{name} 값이 없습니다.");
    private static bool Hash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static double[] Values(JsonElement item, string name)
    {
        var result = item.GetProperty(name).EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (result.Any(x => !double.IsFinite(x))) throw new InvalidDataException("유한하지 않은 기후값입니다.");
        return result;
    }
}
