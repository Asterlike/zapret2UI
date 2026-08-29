using System.Diagnostics;
using System.Runtime.InteropServices;
using Zapret2UI.Localization;

namespace Zapret2UI.Services.Engine;

// Windows job object with KILL_ON_JOB_CLOSE, so winws2 dies with us even if we are killed rather
// than closed: an orphaned engine keeps the WinDivert driver loaded and keeps desyncing traffic
// with nothing left to stop it. Split out because it is pure Win32 interop and reads as noise in
// the middle of the process lifecycle.
public sealed partial class EngineService
{
    private IntPtr _job = IntPtr.Zero;

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    /// <summary>Create the kill-on-close job once and assign <paramref name="proc"/> to it. Best-effort:
    /// if any step fails the engine still runs (graceful Stop/Dispose covers a normal exit).</summary>
    private void EnsureJobAndAssign(Process proc)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return;

                var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
                };
                int len = Marshal.SizeOf(ext);
                IntPtr buf = Marshal.AllocHGlobal(len);
                try
                {
                    Marshal.StructureToPtr(ext, buf, false);
                    if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, buf, (uint)len))
                        _job = job;
                    else
                        CloseHandle(job);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }

            if (_job != IntPtr.Zero && !AssignProcessToJobObject(_job, proc.Handle))
                Emit(Loc.T("Предупреждение: не удалось привязать движок к job-объекту — ") +
                     "автозакрытие при падении приложения может не сработать.");
        }
        catch { /* best-effort; graceful Stop() still handles a clean exit */ }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
