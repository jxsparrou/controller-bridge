using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using SBridge.Sisr;
using Xunit;

namespace SBridge.Tests;

[CollectionDefinition("Windows integration", DisableParallelization = true)]
public sealed class WindowsSisrCollection { }

[Collection("Windows integration")]
[SupportedOSPlatform("windows")]
public class WindowsSisrTests
{
    [WindowsFact]
    public void WindowsListenerTableIdentifiesIpv4AndIpv6Owner()
    {
        using var ipv4 = new TcpListener(IPAddress.Loopback, 0);
        ipv4.Start();
        var endpoint = (IPEndPoint)ipv4.LocalEndpoint;
        Assert.Contains(endpoint, WindowsTcpListeners.ForProcess(Environment.ProcessId));
        Assert.True(WindowsTcpListeners.IsOwned(Environment.ProcessId, endpoint));
        Assert.False(WindowsTcpListeners.IsOwned(-1, endpoint));
        if (Socket.OSSupportsIPv6)
        {
            using var ipv6 = new TcpListener(IPAddress.IPv6Loopback, 0);
            ipv6.Start();
            Assert.Contains((IPEndPoint)ipv6.LocalEndpoint, WindowsTcpListeners.ForProcess(Environment.ProcessId));
        }
    }

    [WindowsFact]
    public async Task OwnedApiConnectionChecksVersionAndRequestsQuit()
    {
        await using var server = new ApiServer("v0.6.1");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var process = new ApiProcess(Environment.ProcessId);
        Assert.True(await new WindowsSisrShutdown().RequestQuitAsync(process, timeout.Token));
        Assert.Contains("GET /api/v1/version/info HTTP/1.1", server.Requests);
        Assert.Contains("POST /api/v1/quit HTTP/1.1", server.Requests);
    }

    [WindowsFact]
    public async Task UnsupportedApiVersionDoesNotReceiveMutation()
    {
        await using var server = new ApiServer("v0.7.0");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.False(await new WindowsSisrShutdown().RequestQuitAsync(new ApiProcess(Environment.ProcessId), timeout.Token));
        Assert.Contains("GET /api/v1/version/info HTTP/1.1", server.Requests);
        Assert.DoesNotContain(server.Requests, line => line.StartsWith("POST"));
    }

    [WindowsFact]
    public async Task UnownedAndExitedProcessesNeverReceiveApiRequests()
    {
        await using var server = new ApiServer("v0.6.1");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.False(await new WindowsSisrShutdown().RequestQuitAsync(new ApiProcess(-1), timeout.Token));
        Assert.False(await new WindowsSisrShutdown().RequestQuitAsync(new ApiProcess(Environment.ProcessId) { HasExited = true }, timeout.Token));
        Assert.Empty(server.Requests);
    }

    [WindowsFact]
    public async Task StaleDiscoveryCannotSendHttpToADifferentSocketOwner()
    {
        await using var server = new ApiServer("v0.6.1");
        using var owned = StartSleeper();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Simulate a stale discovery result. The listener now belongs to the
            // test host, not the retained child. Connection-time native ownership
            // verification must reject it before even the version GET is sent.
            var shutdown = new WindowsSisrShutdown(_ => new[] { server.Endpoint });
            await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() =>
                shutdown.RequestQuitAsync(new ApiProcess(owned.Id), timeout.Token));
            Assert.Empty(server.Requests);
        }
        finally { if (!owned.HasExited) { owned.Kill(); owned.WaitForExit(); } }
    }

    [WindowsFact]
    public void RetainedHandleFallbackStopsOwnedProcessAndLeavesUnrelatedProcessAlive()
    {
        using var unrelated = StartSleeper();
        var owned = StartSleeper();
        using var observer = Process.GetProcessById(owned.Id);
        try
        {
            using var manager = SisrProcessManager.Start(() => new SisrProcessManager.RetainedSisrProcess(owned),
                () => new EmptyLease(), () => false, new NoApi(), _ => { });
            manager.Dispose();
            unrelated.Refresh();
            Assert.False(unrelated.HasExited);
            Assert.True(observer.HasExited);
        }
        finally
        {
            // Only the two processes created by this test are candidates for cleanup.
            if (!unrelated.HasExited) { unrelated.Kill(); unrelated.WaitForExit(); }
            if (!observer.HasExited) { observer.Kill(); observer.WaitForExit(); }
            owned.Dispose();
            // The manager has disposed 'owned'; it never enumerates/kills siblings.
        }
    }

    [InstalledSisrFact]
    public async Task InstalledSISRNoSteamApiLifecycle()
    {
        string executable = Environment.GetEnvironmentVariable("SBRIDGE_TEST_SISR_PATH")!;
        string directory = Path.Combine(Path.GetTempPath(), "sBridge-SISR-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string logPath = Path.Combine(directory, "sisr.log");
        var logs = new List<string>();
        try
        {
            // An intentionally unresolvable non-loopback VIIPER target prevents
            // SISR auto-spawning VIIPER. No Steam forcing, marker creation, controller
            // functionality, or driver readiness is validated by this lifecycle test.
            using var manager = SisrProcessManager.StartWindows(executable,
                "--no-steam --api.listen-address=127.0.0.1:0 --viiper.address=sbridge-smoke.invalid:3242 " +
                "--window.fullscreen=false --window.show=false --update-notify=none --log.file=\"" + logPath + "\"", logs.Add);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (WindowsTcpListeners.ForProcess(manager.ProcessId).Count == 0)
                await Task.Delay(100, timeout.Token);
            await manager.StopAsync();
            Assert.DoesNotContain(logs, line => line.Contains("Force-stopping") || line.Contains("cleanup failed"));
            Assert.Contains(logs, line => line.Contains("Requested graceful quit"));
            Assert.Contains("Shutting down", File.ReadAllText(logPath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static Process StartSleeper()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 300" })
            info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }

    private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    private sealed class NoApi : ISisrShutdown
    {
        public Task<bool> RequestQuitAsync(IOwnedSisrProcess process, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class ApiProcess(int id) : IOwnedSisrProcess
    {
        public int Id => id;
        public bool HasExited { get; init; }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public bool CloseMainWindow() => throw new InvalidOperationException("API tests must not close a real test-host window.");
        public void Kill() => throw new InvalidOperationException("API tests must not kill the test host.");
        public void Dispose() { }
    }

    private sealed class ApiServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task serve;
        public ConcurrentQueue<string> Requests { get; } = new();
        public IPEndPoint Endpoint => (IPEndPoint)listener.LocalEndpoint;

        public ApiServer(string version)
        {
            listener.Start();
            serve = ServeAsync(version);
        }

        private async Task ServeAsync(string version)
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    string? request = await reader.ReadLineAsync(cancellation.Token);
                    if (request == null) continue;
                    Requests.Enqueue(request);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellation.Token))) { }
                    string body = request.StartsWith("GET") ? "{\"version\":\"" + version + "\"}" : "";
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    string status = request.StartsWith("GET") ? "200 OK" : "204 No Content";
                    string headers = "HTTP/1.1 " + status + "\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellation.Token);
                    await stream.WriteAsync(bytes, cancellation.Token);
                }
            }
            catch (Exception ex) when (cancellation.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Expected listener cancellation during test cleanup.
            }
        }

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            listener.Stop();
            await serve;
            cancellation.Dispose();
        }
    }
}

internal sealed class InstalledSisrFactAttribute : FactAttribute
{
    public InstalledSisrFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires the real Windows SISR runtime.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SBRIDGE_TEST_SISR_PATH")))
            Skip = "Opt-in: set SBRIDGE_TEST_SISR_PATH to test an installed SISR in no-Steam mode.";
    }
}
