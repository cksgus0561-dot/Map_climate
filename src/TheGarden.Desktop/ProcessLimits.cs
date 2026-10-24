using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TheGarden.Desktop;

/// <summary>
/// A Windows job object for the bundled climate Python: caps its committed memory (the counterpart of
/// ulimit -v in run_app.sh) and ends it when the app closes.
/// </summary>
internal sealed class ProcessLimits : IDisposable
{
    private const int ExtendedLimitInformation = 9;
    private const uint LimitProcessMemory = 0x100, LimitKillOnJobClose = 0x2000;
    private readonly IntPtr handle;

    public ProcessLimits(long memoryBytes)
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new Win32Exception();
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = LimitProcessMemory | LimitKillOnJobClose },
            ProcessMemoryLimit = (UIntPtr)(ulong)memoryBytes };
        if (!SetInformationJobObject(handle, ExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimit>()))
        { var error = new Win32Exception(); CloseHandle(handle); throw error; }
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception();
    }

    public void Dispose() => CloseHandle(handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
