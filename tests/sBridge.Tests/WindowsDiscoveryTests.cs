using System.Diagnostics;
using System.Runtime.Versioning;
using SBridge.Launching;
using SBridge.Providers;
using Xunit;

namespace SBridge.Tests;

[SupportedOSPlatform("windows")]
[Collection("Windows integration")]
public class WindowsDiscoveryTests
{
    [WindowsFact]
    public async Task BothPipesAreDrainedConcurrentlyWithoutSequentialReadDeadlock()
    {
        var output = await new DiscoveryProcessRunner().RunAsync(Command(
            "[Console]::Error.Write([String]::new([char]120,200000)); [Console]::Write('{\"schemaVersion\":1,\"games\":[],\"warnings\":[]}')"),
            TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(0, output.ExitCode);
        Assert.Equal(200000, output.StandardError.Length);
        Assert.Empty(XboxDiscoveryCodec.Decode(output.StandardOutput).Games);
    }

    [WindowsFact]
    public async Task OutputLimitAndTimeoutAreBoundedAndNotEmptySuccess()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new DiscoveryProcessRunner().RunAsync(
            Command("[Console]::Write([String]::new([char]120,300000)); Start-Sleep -Seconds 30"),
            TimeSpan.FromSeconds(10), CancellationToken.None, maxOutputBytes: 8192));
        await Assert.ThrowsAsync<TimeoutException>(() => new DiscoveryProcessRunner().RunAsync(
            Command("Start-Sleep -Seconds 30"), TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [WindowsFact]
    public async Task CancellationStopsOnlyTheRetainedDiscoveryProcess()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sBridge-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); string pidFile = Path.Combine(directory, "pid.txt");
        using var cancellation = new CancellationTokenSource();
        using var unrelated = Process.Start(Command("Start-Sleep -Seconds 30"))!;
        try
        {
            string command = "[IO.File]::WriteAllText('" + pidFile.Replace("'", "''") + "',[string]$PID); Start-Sleep -Seconds 30";
            Task<DiscoveryCommandOutput> task = new DiscoveryProcessRunner().RunAsync(Command(command), TimeSpan.FromSeconds(12), cancellation.Token);
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!File.Exists(pidFile)) await Task.Delay(50, ready.Token);
            using var observed = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.True(observed.HasExited); Assert.False(unrelated.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            if (!unrelated.HasExited) { unrelated.Kill(); await unrelated.WaitForExitAsync(); }
            Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public async Task ExitCodeAndUnicodeOutputArePreserved()
    {
        var output = await new DiscoveryProcessRunner().RunAsync(Command("[Console]::Write([char]0x904A); exit 7"),
            TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal("遊", output.StandardOutput); Assert.Equal(7, output.ExitCode);
    }

    [WindowsFact]
    public void PackagedComGuardRejectsMtaBeforeActivatingAnyApp()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { new WindowsPackagedActivation().Activate(LegacyLaunchRequest.Parse(new[] { "invalid-test-aumid" }, _ => false)!, _ => { }); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.MTA); thread.Start(); thread.Join();
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("STA", failure!.Message);
    }

    [PackagedDiscoveryFact]
    public async Task InstalledPackagedInventoryReadOnlyDiscovery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        var result = await new WindowsXboxDiscovery().DiscoverAsync(timeout.Token);
        Assert.All(result.Games, game =>
        {
            Assert.Equal("xbox", game.Provider); Assert.Contains("!", game.Target);
            Assert.Equal(Core.GameLaunchKind.PackagedApplication, game.LaunchKind);
            Assert.False(string.IsNullOrWhiteSpace(game.InstallDirectory));
        });
    }

    private static ProcessStartInfo Command(string script)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        string command = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " + script;
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)) })
            info.ArgumentList.Add(argument);
        return info;
    }
}

internal sealed class PackagedDiscoveryFactAttribute : FactAttribute
{
    public PackagedDiscoveryFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires real Windows Appx inventory.";
        else if (Environment.GetEnvironmentVariable("SBRIDGE_TEST_PACKAGED_DISCOVERY") != "1")
            Skip = "Opt-in: set SBRIDGE_TEST_PACKAGED_DISCOVERY=1 for read-only Appx inventory discovery.";
    }
}
