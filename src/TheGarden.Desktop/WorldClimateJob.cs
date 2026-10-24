using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TheGarden.Core;
using TheGarden.Core.Climate;

namespace TheGarden.Desktop;

/// <summary>
/// Runs the worldbuilding climate model (tools/climate/worldclimate) on a frozen terrain snapshot.
/// Packaged app (climate\python\python.exe next to it): the bundled Windows Python writes straight into
/// this run's scratch folder (<see cref="SessionRoot"/>\worldclimate-ui\&lt;job&gt;); the map file keeps the result
/// once it is saved, so nothing of it stays on the computer after the app closes.
/// Repository build: WSL; outputs are written inside the WSL file system and read through \\wsl.localhost\Ubuntu\...,
/// then copied to artifacts/worldclimate-ui/&lt;job&gt; so the result stays with the project.
/// </summary>
public sealed class WorldClimateJob
{
    public const string Distro = "Ubuntu";
    public string SourceHash { get; }
    public string JobId { get; }
    public string ArchivePath { get; }
    public string Phase { get; private set; } = "preparing";
    public string Message { get; private set; } = "준비 중";
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public bool CancelRequested { get; private set; }
    public bool IsFinished { get; private set; }
    public WorldClimateAtlas? Result { get; private set; }
    public double ElapsedSeconds => watch.Elapsed.TotalSeconds;
    public Task<WorldClimateAtlas?>? Completion { get; private set; }
    private readonly World snapshot;
    private readonly string repository;
    private readonly GeoPoint[] deepwater;
    private readonly Stopwatch watch = new();
    private string? output;         // where status.json appears: the archive (bundled) or \\wsl.localhost\Ubuntu\root\wc-out\ui\<job>

    /// <summary>True for the packaged layout: climate\ holds tools\climate, data\climate and python\python.exe.</summary>
    public static bool IsBundled(string repository) => File.Exists(BundledPython(repository));
    private static string BundledPython(string repository) => Path.Combine(repository, "python", "python.exe");

    public static string ArchiveRoot(string repository) => IsBundled(repository)
        ? Path.Combine(SessionRoot, "worldclimate-ui")
        : Path.Combine(repository, "artifacts", "worldclimate-ui");

    private static readonly string ScratchRoot = Path.Combine(Path.GetTempPath(), "Map");
    /// <summary>This run's scratch folder (%TEMP%\Map\&lt;process id&gt;): fresh results and the climate unpacked from an
    /// opened map. Deleted when the app closes; <see cref="CleanScratch"/> also removes those of runs that ended without it.</summary>
    public static string SessionRoot { get; } = Path.Combine(ScratchRoot, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static void CleanScratch(bool own)
    {
        if (!Directory.Exists(ScratchRoot)) return;
        var name = Process.GetCurrentProcess().ProcessName;
        foreach (var dir in Directory.EnumerateDirectories(ScratchRoot))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;       // e.g. the matplotlib font cache
            var mine = pid == Environment.ProcessId;
            if (mine != own) continue;
            if (!mine)
                try { using var other = Process.GetProcessById(pid); if (other.ProcessName == name) continue; }   // still running
                catch (ArgumentException) { }                                                                       // ended
            try { Directory.Delete(dir, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The bundled climate folder next to the app, or else the repository holding tools/climate/worldclimate
    /// and its WSL Python runtime, searched upwards from the app.</summary>
    public static string? FindRepository()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "climate");
        if (IsBundled(bundled) && File.Exists(Path.Combine(bundled, "tools", "climate", "worldclimate", "run.py"))) return bundled;
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "tools", "climate", "worldclimate", "run_app.sh"))) return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    public WorldClimateJob(World world, string repository, IEnumerable<GeoPoint> deepwaterSites)
    {
        this.repository = repository; snapshot = world.Clone(); SourceHash = snapshot.Fingerprint();
        deepwater = deepwaterSites.ToArray();
        JobId = DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        ArchivePath = Path.Combine(ArchiveRoot(repository), JobId);
    }

    public void Start() => Completion = Task.Run(RunAsync);

    public void Cancel()
    {
        if (IsFinished) return;
        CancelRequested = true;
        try { if (output is not null) File.WriteAllText(Path.Combine(output, "cancel.request"), "cancel"); }
        catch (IOException) { }
        // the model reads the cancel file only between its steps, some of which take minutes (maps, rivers,
        // stability): if it has not stopped within 3 s, end it outright
        _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(3)); await Stop(); });
    }

    private Process? running;
    private bool runningInWsl;
    private async Task Stop()
    {
        if (IsFinished || running is not { } process) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        // the Linux side outlives wsl.exe: end the model there by its job id (it is in every command line of the job)
        if (runningInWsl)
            try { await Wsl("pkill", "-f", JobId); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { /* none left */ }
    }

    public void Refresh()
    {
        if (IsFinished || output is null) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "status.json")));
            var root = doc.RootElement;
            Phase = root.GetProperty("phase").GetString() ?? Phase;
            Progress = root.GetProperty("progress").GetDouble();
            Message = root.GetProperty("message").GetString() ?? Message;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or KeyNotFoundException) { /* between writes */ }
    }

    private async Task<WorldClimateAtlas?> RunAsync()
    {
        watch.Start();
        try
        {
            Directory.CreateDirectory(ArchivePath);
            var input = Path.Combine(ArchivePath, "input.garden");
            await WorldFile.SaveClimateInputAsync(snapshot, input);    // the model's own terrain format, sampled from the cube
            if (CancelRequested) { Phase = "cancelled"; return null; }
            var bundled = IsBundled(repository);
            var start = bundled ? BundledStart(input) : await WslRunStart(input);
            if (CancelRequested) { Phase = "cancelled"; return null; }      // cancelled while connecting to WSL
            using var limits = bundled ? new ProcessLimits(4L << 30) : null;
            using var process = new Process { StartInfo = start };
            process.Start();
            running = process; runningInWsl = !bundled;
            if (limits is not null)
            {
                limits.Assign(process);
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
            }
            if (CancelRequested) await Stop();                               // cancelled just as it started
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var exit = process.WaitForExitAsync();
            // 40 minutes for the model (run_app.sh has its own 40-minute limit; this then guards a stuck launcher).
            if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromMinutes(bundled ? 40 : 45))) != exit)
            {
                Cancel();
                if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(20))) != exit) process.Kill(entireProcessTree: true);
                throw new TimeoutException("기후 계산이 응답하지 않아 중단했습니다.");
            }
            await File.WriteAllTextAsync(Path.Combine(ArchivePath, "launcher.log"), await stdout + "\n" + await stderr);
            Refresh();
            if (CancelRequested || Phase == "cancelled" || process.ExitCode == 3) { Phase = "cancelled"; return null; }
            if (process.ExitCode != 0 || !File.Exists(Path.Combine(output!, "result.json")))
                throw new InvalidOperationException(Phase == "failed" ? "계산 실패: " + Message : "계산을 완료하지 못했습니다. launcher.log를 확인하세요.");
            if (!bundled) { Message = "결과를 프로젝트 폴더로 옮기는 중"; CopyDirectory(output!, ArchivePath); }
            var atlas = WorldClimateAtlas.Read(ArchivePath);
            if (!atlas.Matches(SourceHash)) throw new InvalidDataException("계산 결과가 입력 지형과 일치하지 않습니다.");
            Result = atlas; Phase = "completed"; Progress = 1; Message = "완료";
            return atlas;
        }
        catch (Exception ex)
        {
            Phase = CancelRequested ? "cancelled" : "failed"; Error = ex.Message;
            try { File.WriteAllText(Path.Combine(ArchivePath, "app-error.txt"), ex.ToString()); } catch (IOException) { }
            return null;
        }
        finally { watch.Stop(); IsFinished = true; }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name is "cancel.request" or "status.tmp") continue;
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    private IEnumerable<string> ModelArguments()
    {
        // climate layers are drawn at 1024 x 512 (~39 km pixels, finer than the climate itself) and scaled onto the terrain
        yield return "1024x512";
        foreach (var p in deepwater) { yield return "--deepwater"; yield return FormattableString.Invariant($"{p.Latitude:F2},{p.Longitude:F2}"); }
    }

    /// <summary>The bundled Windows Python (its python312._pth puts ..\tools\climate on the path), with the
    /// thread limits of run_app.sh; memory and priority are set on the started process.</summary>
    private ProcessStartInfo BundledStart(string input)
    {
        output = ArchivePath;
        var start = new ProcessStartInfo(BundledPython(repository)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = repository,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var name in new[] { "OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMEXPR_NUM_THREADS" }) start.Environment[name] = "2";
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["MPLCONFIGDIR"] = Path.Combine(ScratchRoot, "matplotlib");     // font cache, kept between runs
        var app = ModelArguments().ToList();
        foreach (var arg in new[] { "-m", "worldclimate.run", input, "--out", ArchivePath, "--app", app[0],
                     "--status", Path.Combine(ArchivePath, "status.json"), "--cancel", Path.Combine(ArchivePath, "cancel.request") }.Concat(app.Skip(1)))
            start.ArgumentList.Add(arg);
        return start;
    }

    private async Task<ProcessStartInfo> WslRunStart(string input)
    {
        Message = "Ubuntu(WSL)에 연결하는 중";
        var linuxRepository = await Wsl("wslpath", "-a", "-u", repository.Replace('\\', '/'));
        var linuxInput = await Wsl("wslpath", "-a", "-u", input.Replace('\\', '/'));
        var home = await Wsl("sh", "-c", "echo $HOME");
        output = $@"\\wsl.localhost\{Distro}" + (home + "/wc-out/ui/" + JobId).Replace('/', '\\');
        return WslStart(new[] { "bash", linuxRepository + "/tools/climate/worldclimate/run_app.sh", linuxRepository, JobId, linuxInput }.Concat(ModelArguments()));
    }

    private static ProcessStartInfo WslStart(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "-d", Distro, "--exec" }.Concat(arguments)) start.ArgumentList.Add(arg);
        return start;
    }

    private static async Task<string> Wsl(params string[] arguments)
    {
        using var process = Process.Start(WslStart(arguments)) ?? throw new InvalidOperationException("Ubuntu(WSL)를 시작하지 못했습니다.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("Ubuntu(WSL) 실행이 지연되고 있습니다."); }
        if (process.ExitCode != 0) throw new InvalidOperationException("Ubuntu(WSL)에 연결하지 못했습니다. " + await error);
        return (await output).Trim();
    }
}
