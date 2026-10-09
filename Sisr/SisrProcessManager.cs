using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using SBridge.Diagnostics;
using SBridge.Core;

namespace SBridge.Sisr;

internal interface IOwnedSisrProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    bool CloseMainWindow();
    void Kill();
}

internal interface ISisrShutdown
{
    Task<bool> RequestQuitAsync(IOwnedSisrProcess process, CancellationToken cancellationToken);
}

internal sealed class SisrProcessManager : IDisposable
{
    private readonly IOwnedSisrProcess process;
    private readonly IDisposable lease;
    private readonly ISisrShutdown shutdown;
    private readonly Action<string> log;
    private readonly TimeSpan shutdownTimeout;
    private readonly object stopGate = new();
    private Task? stopTask;
    private SisrManagedStartup? managedSession;
    private string? managedDataDirectory;

    private SisrProcessManager(IOwnedSisrProcess process, IDisposable lease,
        ISisrShutdown shutdown, Action<string> log, TimeSpan shutdownTimeout)
    {
        this.process = process;
        this.lease = lease;
        this.shutdown = shutdown;
        this.log = log;
        this.shutdownTimeout = shutdownTimeout;
        ProcessId = process.Id;
    }

    public int ProcessId { get; }
    public async Task<SisrStatusSnapshot> WaitForReadyAsync(ISisrStatusSource source, CancellationToken token, TimeSpan? timeout = null)
    {
        var status = await SisrReadiness.WaitAsync(process, source, token, timeout).ConfigureAwait(false);
        managedSession?.RecordStatus("ready", status); return status;
    }

    internal static SisrProcessManager Start(Func<IOwnedSisrProcess> start,
        Func<IDisposable> acquireLease, Func<bool> externalSisrRunning,
        ISisrShutdown shutdown, Action<string> log, TimeSpan? shutdownTimeout = null)
    {
        IDisposable lease = acquireLease();
        SisrProcessManager? manager = null;
        try
        {
            if (externalSisrRunning())
                throw new InvalidOperationException("SISR is already running. It was left untouched. Close it or disable sBridge's SISR integration before launching.");
            var process = start();
            manager = new SisrProcessManager(process, lease, shutdown, log, shutdownTimeout ?? TimeSpan.FromSeconds(5));
            if (process.HasExited)
                throw new InvalidOperationException("The owned SISR process exited during startup. Check its configuration and dependencies.");
            log("Started owned SISR PID=" + process.Id);
            return manager;
        }
        catch
        {
            if (manager != null) manager.Dispose();
            else lease.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    public static SisrProcessManager StartWindows(string executable, string arguments, Action<string> log)
    {
        if (!File.Exists(executable))
            throw new FileNotFoundException("SISR executable was not found. Check the configured SISR path.", executable);
        return Start(() => new RetainedSisrProcess(Process.Start(new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true
            // Preserve the legacy arguments, working directory, and Steam environment.
        }) ?? throw new InvalidOperationException("Windows did not return an owned SISR process.")),
            AcquireWindowsLease, () => HasExternalSisr(executable), new WindowsSisrShutdown(), log);
    }

    [SupportedOSPlatform("windows")]
    public static SisrProcessManager StartWindowsManaged(string executable, string arguments, string dataDirectory, Action<string> log, SisrControllerProfile? controller = null)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("SISR executable was not found. Check the configured SISR path.", executable);
        var tokens = SisrManagedStartup.Arguments(arguments, controller); // Reject conflicting owned flags before resources/startup.
        SisrManagedStartup? config = null;
        try
        {
            var manager = Start(() =>
            {
                config = SisrManagedStartup.Create(dataDirectory, controller);
                log("Managed SISR session log: " + config.LogPath);
                return new RetainedSisrProcess(Process.Start(config.StartInfo(executable, tokens)) ??
                    throw new InvalidOperationException("Windows did not return an owned SISR process."));
            }, AcquireWindowsLease, () => HasExternalSisr(executable), new WindowsSisrShutdown(), log);
            manager.managedSession = config; manager.managedDataDirectory = dataDirectory; return manager;
        }
        catch { config?.RecordStatus("ended", null); config?.Dispose(); throw; }
    }

    [SupportedOSPlatform("windows")]
    private static IDisposable AcquireWindowsLease()
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("Cannot determine the Windows user for SISR ownership.");
        // A semaphore is not thread-affine: cleanup may resume on a worker thread.
        var semaphore = new Semaphore(1, 1, @"Local\sBridge.Sisr." + sid);
        if (!semaphore.WaitOne(0))
        {
            semaphore.Dispose();
            throw new InvalidOperationException("Another sBridge session owns SISR integration. Wait for that session to finish or disable SISR for this game.");
        }
        return new SemaphoreLease(semaphore);
    }

    private static bool HasExternalSisr(string executable)
    {
        string configuredName = Path.GetFileNameWithoutExtension(executable);
        foreach (string name in new[] { "SISR", configuredName })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) return true; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }

    public Task StopAsync()
    {
        lock (stopGate)
            return stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        try
        {
            if (process.HasExited)
            {
                log("Owned SISR already exited; no shutdown action needed.");
                return;
            }
            bool requested = false;
            try
            {
                using var timeout = new CancellationTokenSource(shutdownTimeout);
                requested = await shutdown.RequestQuitAsync(process, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or
                System.Net.Http.HttpRequestException or System.Text.Json.JsonException or Win32Exception or InvalidOperationException)
            {
                log("Owned SISR API shutdown unavailable: " + ex.Message);
            }

            if (requested)
            {
                log("Requested graceful quit for owned SISR PID=" + process.Id);
                if (await WaitForExitAsync().ConfigureAwait(false)) return;
                log("Owned SISR did not exit within the graceful shutdown budget.");
            }
            if (process.HasExited) return;
            try
            {
                if (process.CloseMainWindow() && await WaitForExitAsync().ConfigureAwait(false))
                {
                    log("Owned SISR exited after a window-close request.");
                    return;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                log("Owned SISR window-close request unavailable: " + ex.Message);
            }
            if (process.HasExited) return;
            log("Force-stopping only owned SISR PID=" + process.Id + ". VIIPER and other processes are not terminated; integration cleanup may be incomplete.");
            process.Kill(); // Retained process handle, never a PID/name lookup or process-tree kill.
            if (!await WaitForExitAsync().ConfigureAwait(false))
                log("Owned SISR still has not exited after forced termination.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            log("Owned SISR cleanup failed: " + ex.Message);
        }
        finally
        {
            try
            {
                if (managedSession != null)
                {
                    bool exited = process.HasExited;
                    managedSession.RecordStatus(exited ? "ended" : "cleanup-incomplete", null);
                    // Keep the marker locked if an owned root could not be stopped.
                    // Normal shutdown releases it before completed-session retention.
                    if (exited) managedSession.Dispose();
                }
                process.Dispose();
            }
            finally
            {
                lease.Dispose();
                if (managedDataDirectory != null) ManagedSessionDiagnostics.Prune(managedDataDirectory);
            }
        }
    }

    private async Task<bool> WaitForExitAsync()
    {
        using var timeout = new CancellationTokenSource(shutdownTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return false;
        }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    internal sealed class RetainedSisrProcess : IOwnedSisrProcess
    {
        private readonly Process process;
        public RetainedSisrProcess(Process process) => this.process = process;
        public int Id => process.Id;
        public bool HasExited => process.HasExited;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public bool CloseMainWindow() => process.CloseMainWindow();
        public void Kill() => process.Kill(entireProcessTree: false);
        public void Dispose() => process.Dispose();
    }

    private sealed class SemaphoreLease : IDisposable
    {
        private Semaphore? semaphore;
        public SemaphoreLease(Semaphore semaphore) => this.semaphore = semaphore;
        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref semaphore, null);
            if (owned == null) return;
            try { owned.Release(); }
            finally { owned.Dispose(); }
        }
    }
}
