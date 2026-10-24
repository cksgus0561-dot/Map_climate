using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TheGarden.Core;
using TheGarden.Core.Climate;

namespace TheGarden.Desktop;

/// <summary>Runs the existing research adapter in WSL with a frozen terrain snapshot.</summary>
public sealed class ClimateTrialJob
{
    public string SourceHash { get; }
    public string DirectoryPath { get; }
    public string Phase { get; private set; } = "preparing";
    public string? Error { get; private set; }
    public bool CancelRequested { get; private set; }
    public bool IsFinished { get; private set; }
    public double ElapsedSeconds => watch.Elapsed.TotalSeconds;
    public Task<ClimateTrialSummary?>? Completion { get; private set; }
    private readonly World snapshot;
    private readonly string repository;
    private readonly Stopwatch watch = new();

    public static string? FindRepository()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName,"tools","climate","benchmark.py"))) return current.FullName;
            current = current.Parent;
        }
        return null;
    }
    public ClimateTrialJob(World world, string repository)
    {
        this.repository = repository;
        snapshot = world.Clone();
        SourceHash = snapshot.Fingerprint();
        DirectoryPath = Path.Combine(repository,"artifacts","climate-ui",DateTime.UtcNow.ToString("yyyyMMddTHHmmss")+"-"+Guid.NewGuid().ToString("N"));
    }
    public void Start() => throw new NotSupportedException("기존 격자 전용 시험 엔진은 벡터 지형을 지원하지 않습니다.");
    public void Cancel()
    {
        if (IsFinished) return;
        // The Linux supervisor observes this file and terminates only its own group.
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath,"cancel.request"), "cancel");
        CancelRequested = true;
    }
    public void Refresh()
    {
        if (IsFinished || CancelRequested) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(DirectoryPath,"status.json")));
            Phase = doc.RootElement.GetProperty("phase").GetString() ?? "preparing";
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* Atomic writer may be between files. */ }
    }
    private async Task<ClimateTrialSummary?> RunAsync()
    {
        watch.Start();
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            // Linux symlinks in a WSL venv may not be visible as File.Exists on Windows.
            if (!Directory.Exists(Path.Combine(repository,"artifacts","climate-env")))
                throw new InvalidOperationException("기후 시험 환경이 없습니다. docs/climate-trial.md의 설치 방법을 확인하세요.");
            await WorldFile.SaveAsync(snapshot,Path.Combine(DirectoryPath,"input.garden"));
            if (CancelRequested) { Phase = "cancelled"; return null; }
            var linuxRepository = await WslPath(repository);
            var linuxJob = await WslPath(DirectoryPath);
            using var process = new Process { StartInfo = WslStart(new[] {
                linuxRepository+"/artifacts/climate-env/bin/python", linuxRepository+"/tools/climate/benchmark.py",
                "--garden",linuxJob+"/input.garden", "--output-directory",linuxJob+"/results",
                "--status-file",linuxJob+"/status.json", "--cancel-file",linuxJob+"/cancel.request",
                "--seconds","180", "--setup-seconds","240", "--years","1", "--cpus","4" }) };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            // Solver phases are bounded inside Linux. This additional deadline handles a broken WSL launcher.
            var exit = process.WaitForExitAsync();
            if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromMinutes(8))) != exit)
            {
                Cancel();
                if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(10))) != exit) process.Kill(entireProcessTree:true);
                throw new TimeoutException("계산 연결이 응답하지 않아 중단했습니다. 로그를 확인하세요.");
            }
            var output = await stdout; var errors = await stderr;
            await File.WriteAllTextAsync(Path.Combine(DirectoryPath,"launcher.log"),output+"\n"+errors);
            Refresh();
            if (CancelRequested || Phase == "cancelled") { Phase="cancelled"; return null; }
            if (Phase.Contains("timeout",StringComparison.Ordinal)) { Phase="timeout"; return null; }
            if (process.ExitCode != 0) throw new InvalidOperationException("계산을 완료하지 못했습니다. 실행 기록을 확인하세요.");
            var result = ClimateTrialSummary.Read(Path.Combine(DirectoryPath,"results","report.json"))
                .SingleOrDefault(r => r.Matches(SourceHash)) ?? throw new InvalidDataException("현재 입력과 일치하는 완료 결과가 없습니다.");
            Phase = "completed";
            return result;
        }
        catch (Exception ex)
        {
            Phase = CancelRequested ? "cancelled" : "failed"; Error = ex.Message;
            try { File.WriteAllText(Path.Combine(DirectoryPath,"app-error.txt"),ex.ToString()); } catch (IOException) { }
            return null;
        }
        finally { watch.Stop(); IsFinished = true; }
    }
    private static ProcessStartInfo WslStart(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo("wsl.exe") { UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardOutput=true, RedirectStandardError=true, StandardOutputEncoding=Encoding.UTF8, StandardErrorEncoding=Encoding.UTF8 };
        foreach (var arg in new[] {"-d","Ubuntu","--exec"}.Concat(arguments)) start.ArgumentList.Add(arg);
        return start;
    }
    private static async Task<string> WslPath(string path)
    {
        using var process = Process.Start(WslStart(new[] {"wslpath","-a","-u",path.Replace('\\','/')}))
            ?? throw new InvalidOperationException("Ubuntu를 시작하지 못했습니다.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("Ubuntu 실행이 지연되고 있습니다."); }
        if (process.ExitCode != 0) throw new InvalidOperationException("Ubuntu에 연결하지 못했습니다. "+await error);
        return (await output).Trim();
    }
    public static IReadOnlyList<ClimateTrialSummary> ReadArchive(string repository)
    {
        var summaries = new List<ClimateTrialSummary>();
        foreach (var (folder,suffix) in new[] { ("climate","report.json"),("climate-ui",Path.Combine("results","report.json")) })
        {
            var root = Path.Combine(repository,"artifacts",folder);
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var path = Path.Combine(directory,suffix);
                if (!File.Exists(path)) continue;
                try { summaries.AddRange(ClimateTrialSummary.Read(path)); }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
            }
        }
        return summaries.OrderByDescending(s=>s.CreatedUtc).ToArray();
    }
}
