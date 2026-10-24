namespace TheGarden.Core.Climate;

public enum OceanTreatment { None, MixedLayer, DynamicHeatTransport }
public enum ClimateStatus { NotCalculated, Running, Unconverged, Converged, Cancelled, Failed }

// Contract only. No solver or synthetic climate results are implemented in this milestone.
public sealed record ClimateCapabilities(string ModelId, string ModelVersion, OceanTreatment Ocean,
    string[] Assumptions, string[] SupportedVariables);
/// <summary>A frozen copy of the world; an engine takes the terrain in its own form (e.g. WorldFile.SaveClimateInput).</summary>
public sealed record ClimateRequest(World Terrain, int TargetSeconds)
{
    public PlanetSettings Planet => Terrain.Planet;
    public string TerrainSha256 => Terrain.Fingerprint();
}
public sealed record ClimateProgress(double? Fraction, string Message, double ElapsedSeconds);
public sealed record MonthlyClimateField(string Variable, string Unit, int Width, int Height,
    ReadOnlyMemory<float> Values); // month-major: 12 × height × width; native grid, not display interpolation.
public sealed record ClimateResult(ClimateCapabilities Model, string TerrainSha256, ClimateStatus Status,
    string GridDescription, string[] Diagnostics, MonthlyClimateField[] Fields);
public interface IClimateEngine
{
    ClimateCapabilities Capabilities { get; }
    Task<ClimateResult> RunAsync(ClimateRequest request, IProgress<ClimateProgress> progress, CancellationToken cancellationToken);
}
