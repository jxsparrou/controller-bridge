using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace SBridge.Sessions;

[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessObserver : IProcessObserver
{
    private readonly int sessionId;
    private readonly int excludedId;

    public WindowsProcessObserver()
    {
        using var self = Process.GetCurrentProcess();
        sessionId = self.SessionId;
        excludedId = self.Id;
    }

    public IReadOnlyList<ProcessObservation> Capture()
    {
        var windows = new HashSet<int>();
        EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window)) { GetWindowThreadProcessId(window, out uint id); windows.Add((int)id); }
            return true;
        }, IntPtr.Zero);
        var observations = new List<ProcessObservation>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
        bool available = Process32First(snapshot, ref entry);
        while (available)
        {
            int id = (int)entry.ProcessId;
            if (id != 0 && id != excludedId)
            {
                try
                {
                    using var process = Process.GetProcessById(id);
                    if (process.SessionId == sessionId)
                    {
                        DateTimeOffset? start = null;
                        try { start = new DateTimeOffset(process.StartTime.ToUniversalTime()); }
                        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { /* Unknown, not proof of a new process. */ }
                        observations.Add(new ProcessObservation(id, start, ProcessMatcher.ExecutableName(entry.Executable),
                            ImagePath(id), (int)entry.ParentProcessId, windows.Contains(id)));
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException)
                {
                    // Process exited during enumeration, or belongs to an inaccessible session.
                }
            }
            if (observations.Count > 16000) throw new InvalidOperationException("Process observation count exceeded the bounded session scan.");
            available = Process32Next(snapshot, ref entry);
        }
        int error = Marshal.GetLastWin32Error();
        if (error != 0 && error != 18) throw new Win32Exception(error); // ERROR_NO_MORE_FILES
        return observations;
    }

    public ITrackedGameProcess? TryTrack(ProcessIdentity identity)
    {
        try
        {
            var tracked = WindowsTrackedGameProcess.TryRetain(Process.GetProcessById(identity.Id));
            if (tracked == null) return null;
            if (tracked.Identity != identity || tracked.HasExited) { tracked.Dispose(); return null; }
            return tracked;
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException) { return null; }
    }

    internal static string? ImagePath(int id)
    {
        using var handle = OpenProcess(0x1000, false, id); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) return null;
        var path = new StringBuilder(1024);
        int size = path.Capacity;
        if (QueryFullProcessImageName(handle, 0, path, ref size)) return path.ToString();
        if (Marshal.GetLastWin32Error() != 122) return null;
        path = new StringBuilder(32768);
        size = path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }

    private sealed class SnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SnapshotHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SnapshotHandle CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(SnapshotHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(SnapshotHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsTrackedGameProcess : ITrackedGameProcess
{
    private readonly Process process;
    private WindowsTrackedGameProcess(Process process, ProcessIdentity identity, ProcessObservation observation)
    {
        this.process = process;
        Identity = identity;
        Observation = observation;
    }

    public ProcessIdentity Identity { get; }
    public ProcessObservation Observation { get; }
    public bool HasExited => process.HasExited;
    public DateTimeOffset? ExitedAtUtc => process.HasExited ? new DateTimeOffset(process.ExitTime.ToUniversalTime()) : null;
    public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
    public void Dispose() => process.Dispose();

    public static WindowsTrackedGameProcess? TryRetain(Process process, string? fallbackName = null, string? fallbackPath = null)
    {
        try
        {
            // Pin an observation/wait handle before matching; never reacquire a PID
            // as if it were the original process after reuse.
            _ = process.SafeHandle;
            var identity = new ProcessIdentity(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()));
            string name = fallbackName ?? "";
            try { name = process.ProcessName; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            var observation = new ProcessObservation(process.Id, identity.StartedAtUtc, name,
                WindowsProcessObserver.ImagePath(process.Id) ?? fallbackPath, null, false);
            return new WindowsTrackedGameProcess(process, identity, observation);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            return null;
        }
    }
}
