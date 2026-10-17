using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Blazma.AI;

/// <summary>
/// Starts runner processes. On Linux every runner is started from one dedicated thread that lives as long as the host:
/// the runner asks the kernel for SIGKILL when its parent dies (PR_SET_PDEATHSIG), and Linux means the parent
/// <i>thread</i> by that, so a runner started from a thread-pool thread would be killed when that thread retires.
/// Elsewhere this is plain <see cref="Process.Start(ProcessStartInfo)"/>.
/// </summary>
internal static class RunnerSpawner
{
    static readonly BlockingCollection<(ProcessStartInfo Psi, TaskCompletionSource<Process?> Done)> Queue = new();
    static readonly object Lock = new();
    static Thread? _thread;

    public static Process? Start(ProcessStartInfo psi)
    {
        if (!OperatingSystem.IsLinux()) return Process.Start(psi);
        lock (Lock)
        {
            if (_thread is null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "kimodo-runner spawner" };
                _thread.Start();
            }
        }
        var done = new TaskCompletionSource<Process?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add((psi, done));
        return done.Task.GetAwaiter().GetResult();
    }

    static void Loop()
    {
        foreach (var (psi, done) in Queue.GetConsumingEnumerable())
        {
            try { done.TrySetResult(Process.Start(psi)); }
            catch (Exception e) { done.TrySetException(e); }
        }
    }
}

/// <summary>
/// Windows: every runner is assigned to one Job object created with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Its handle is never
/// closed by Blazma, so Windows closes it when the host process ends for any reason (crash, kill, Task Manager) and the
/// runner is killed with it.
/// <para>Limits of <see cref="Process"/> (documented in docs/AI_INTEGRATION.md §5): the child exists for a moment before it is
/// assigned (the runner starts no processes of its own, and it also exits when its stdin closes); and with redirected
/// standard streams .NET calls CreateProcess with bInheritHandles = TRUE, so every handle of the host that is marked
/// inheritable is inherited. .NET opens its own files and sockets non-inheritable, but handles made inheritable by native code
/// (drivers, the engine) cannot be excluded: .NET has no API for PROC_THREAD_ATTRIBUTE_HANDLE_LIST.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RunnerJob
{
    const int JobObjectExtendedLimitInformation = 9;
    const uint JobObjectLimitKillOnJobClose = 0x2000;

    static readonly object Lock = new();
    static SafeFileHandle? _job;
    static string? _error;

    public static bool Assign(Process process, out string? why)
    {
        lock (Lock)
        {
            if (_job is null && _error is null) Create();
            if (_job is null) { why = _error; return false; }
            if (!AssignProcessToJobObject(_job, process.Handle))
            {
                why = $"AssignProcessToJobObject failed ({Marshal.GetLastWin32Error()})";
                return false;
            }
            why = null;
            return true;
        }
    }

    static void Create()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            _error = $"CreateJobObject failed ({Marshal.GetLastWin32Error()})";
            job.Dispose();
            return;
        }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            _error = $"SetInformationJobObject failed ({Marshal.GetLastWin32Error()})";
            job.Dispose();
            return;
        }
        _job = job; // kept open for the life of the process on purpose
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
