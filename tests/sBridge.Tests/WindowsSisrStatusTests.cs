using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using SBridge.Sisr;
using SBridge.Core;
using Xunit;

namespace SBridge.Tests;

[SupportedOSPlatform("windows")]
[Collection("Windows integration")]
public class WindowsSisrStatusTests
{
    [WindowsFact]
    public async Task OwnedStatusReadsVersionSteamViiperDevicesAndEffectiveConfigWithoutMutations()
    {
        await using var server = new Server(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var status = await new WindowsSisrStatus().ProbeAsync(new ProcessStub(Environment.ProcessId), timeout.Token);
        Assert.NotNull(status); Assert.True(status.ApiSupported); Assert.True(status.NoSteamMode); Assert.False(status.ViiperConnected);
        Assert.Equal(0, status.DeviceCount); Assert.Equal("dualsense", status.ControllerType); Assert.True(status.InitialLaunch);
        Assert.DoesNotContain("must-not-log", string.Join(" ", SisrReadiness.Describe(status)));
        Assert.Equal(5, server.Requests.Count); Assert.All(server.Requests, request => Assert.StartsWith("GET ", request));
    }

    [WindowsFact]
    public async Task UnsupportedApiAndStaleOwnershipNeverReceiveFurtherRequests()
    {
        await using var server = new Server { Version = "v0.7.0" }; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var status = await new WindowsSisrStatus().ProbeAsync(new ProcessStub(Environment.ProcessId), timeout.Token);
        Assert.False(status!.ApiSupported); Assert.Single(server.Requests);
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }) info.ArgumentList.Add(argument);
        using var sleeper = System.Diagnostics.Process.Start(info)!;
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => new WindowsSisrStatus(_ => new[] { server.Endpoint }).ProbeAsync(new ProcessStub(sleeper.Id), timeout.Token));
            Assert.Single(server.Requests);
        }
        finally { if (!sleeper.HasExited) { sleeper.Kill(); sleeper.WaitForExit(); } }
    }

    [WindowsFact]
    public async Task MalformedAndOversizeStatusResponsesAreRejectedWithinBudget()
    {
        await using var server = new Server { VersionBody = "[]" }; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsSisrStatus().ProbeAsync(new ProcessStub(Environment.ProcessId), timeout.Token));
        server.VersionBody = new string('x', 70000);
        await Assert.ThrowsAsync<HttpRequestException>(() => new WindowsSisrStatus().ProbeAsync(new ProcessStub(Environment.ProcessId), timeout.Token));
    }

    [InstalledSisrFact]
    public async Task InstalledSISRManagedNoSteamReadinessAndConfiguration()
    {
        string executable = Environment.GetEnvironmentVariable("SBRIDGE_TEST_SISR_PATH")!;
        string directory = Path.Combine(Path.GetTempPath(), "sBridge-SISR-managed-" + Guid.NewGuid().ToString("N"));
        var lines = new List<string>();
        try
        {
            using var manager = SisrProcessManager.StartWindowsManaged(executable,
                "--no-steam --viiper.address=sbridge-smoke.invalid:3242 --update-notify=none", Path.GetFullPath(directory), lines.Add);
            var status = await manager.WaitForReadyAsync(new WindowsSisrStatus(), CancellationToken.None, TimeSpan.FromSeconds(20));
            Assert.True(status.ApiSupported); Assert.True(status.NoSteamMode); Assert.False(status.ViiperConnected);
            Assert.NotNull(status.ControllerType);
            Assert.False(status.WindowFullscreen); Assert.False(status.WindowShown); // Prove generated JSON overrides SISR's fullscreen=true default.
            await manager.StopAsync(); Assert.DoesNotContain(lines, line => line.Contains("Force-stopping"));
            string config = Assert.Single(Directory.GetFiles(directory, "startup.json", SearchOption.AllDirectories));
            string log = Path.Combine(Path.GetDirectoryName(config)!, "SISR.log");
            Assert.Contains("Shutting down", File.ReadAllText(log));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [InstalledSisrFact]
    public async Task InstalledSISRManagedControllerProfileMatchesEffectiveApiConfiguration()
    {
        string executable = Environment.GetEnvironmentVariable("SBRIDGE_TEST_SISR_PATH")!;
        string directory = Path.Combine(Path.GetTempPath(), "sBridge-SISR-profile-" + Guid.NewGuid().ToString("N"));
        var options = new SisrControllerProfile(SisrControllerType.DualSenseEdge, false, false, true);
        var lines = new List<string>();
        try
        {
            using var manager = SisrProcessManager.StartWindowsManaged(executable,
                "--no-steam --viiper.address=sbridge-smoke.invalid:3242 --update-notify=none", directory, lines.Add, options);
            var status = await manager.WaitForReadyAsync(new WindowsSisrStatus(), CancellationToken.None, TimeSpan.FromSeconds(20));
            SisrManagedStartup.VerifyEffectiveProfile(options, status);
            Assert.True(status.NoSteamMode); Assert.False(status.ViiperConnected);
            await manager.StopAsync(); Assert.DoesNotContain(lines, line => line.Contains("Force-stopping"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class ProcessStub(int id) : IOwnedSisrProcess
    {
        public int Id => id; public bool HasExited => false;
        public Task WaitForExitAsync(CancellationToken token) => throw new NotSupportedException();
        public bool CloseMainWindow() => throw new NotSupportedException(); public void Kill() => throw new NotSupportedException(); public void Dispose() { }
    }
    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0); private readonly CancellationTokenSource stop = new(); private readonly Task serve;
        public string Version { get; set; } = "v0.6.1"; public string? VersionBody { get; set; }
        public ConcurrentQueue<string> Requests { get; } = new(); public IPEndPoint Endpoint => (IPEndPoint)listener.LocalEndpoint;
        public Server() { listener.Start(); serve = Serve(); }
        private async Task Serve()
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token); using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    string? request = await reader.ReadLineAsync(stop.Token); if (request == null) continue;
                    Requests.Enqueue(request); while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                    string body = request.Split(' ')[1] switch
                    {
                        "/api/v1/version/info" => VersionBody ?? "{\"version\":\"" + Version + "\"}",
                        "/api/v1/steam/status" => "{\"steam_running\":false,\"no_steam_mode\":true,\"launched_via_steam\":false,\"cef_debug_reachable\":false,\"marker_shortcut_present\":false}",
                        "/api/v1/viiper/status" => "{\"address\":\"hidden\",\"status\":null}",
                        "/api/v1/devices" => "null",
                        _ => "{\"controllerEmulation\":{\"DefaultControllerType\":\"dualsense\"},\"runMisc\":{\"InitialLaunch\":true},\"viiper\":{\"Password\":\"must-not-log\"}}"
                    };
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n"), stop.Token);
                    await stream.WriteAsync(bytes, stop.Token);
                }
            }
            catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await serve; stop.Dispose(); }
    }
}
