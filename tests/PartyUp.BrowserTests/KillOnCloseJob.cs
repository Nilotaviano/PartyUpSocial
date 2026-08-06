using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PartyUp.BrowserTests;

/// <summary>
/// Windows Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> for the fixture's dev-server
/// children. <c>Process.Kill(entireProcessTree: true)</c> walks the live parent/child tree, but the
/// <c>cmd → npm → node → vite/esbuild</c> chain has intermediates that exit early, so reparented
/// grandchildren escape the walk. Those orphans inherit the test host's (and its callers') stdio
/// handles, which keeps any pipe reading the test run's output open forever — the run looks hung
/// after <c>dotnet test</c> itself has exited. Job membership is inherited by every descendant
/// regardless of reparenting, and the OS kills the whole job when the last job handle closes —
/// including when the test host dies without running teardown.
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly SafeFileHandle _handle;

    [SupportedOSPlatform("windows")]
    public KillOnCloseJob()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        if (!SetInformationJobObject(
                _handle,
                JobObjectExtendedLimitInformationClass,
                in info,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            int error = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw new System.ComponentModel.Win32Exception(error);
        }
    }

    [SupportedOSPlatform("windows")]
    public void Assign(System.Diagnostics.Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        uint informationClass,
        in JobObjectExtendedLimitInformation info,
        uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
