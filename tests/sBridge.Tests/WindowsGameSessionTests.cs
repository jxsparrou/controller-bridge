using System.Diagnostics;
using System.Runtime.Versioning;
using SBridge.Sessions;
using Xunit;

namespace SBridge.Tests;

[SupportedOSPlatform("windows")]
[Collection("Windows integration")]
public sealed class WindowsGameSessionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sBridge session " + Guid.NewGuid().ToString("N"));
    private readonly string events;
    private readonly DateTimeOffset createdAt = DateTimeOffset.UtcNow;

    public WindowsGameSessionTests()
    {
        Directory.CreateDirectory(directory);
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        foreach (string name in new[] { "Launcher", "Bootstrap", "Game" })
            File.Copy(Path.Combine(directory, "sBridge.Tests.exe"), Path.Combine(directory, name + ".exe"));
        events = Path.Combine(directory, "processes.txt");
    }

    [WindowsFact]
    public async Task RealWindowsLauncherBootstrapGameChainIsTrackedWithoutFirstMatchSelection()
    {
        var observer = new WindowsProcessObserver();
        var before = observer.Capture();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var process = Start("Launcher", 1000, 700, 2500, "Bootstrap", "Game");
        var initial = WindowsTrackedGameProcess.TryRetain(process)!;
        var evidence = Evidence(before, started, initial);
        var log = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var result = await new GameSessionMonitor(observer, Options).MonitorAsync(evidence, log.Add, timeout.Token);
        int[] ids = File.ReadAllLines(events).Select(int.Parse).ToArray();
        Assert.Equal(3, ids.Length);
        Assert.Equal(GameSessionOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.Handoffs);
        Assert.Equal(ids[2], result.FinalProcess?.Id);
        Assert.Contains(log, message => message.Contains("provisional bootstrap"));
        Assert.Contains(log, message => message.Contains("descendant"));
    }

    [WindowsFact]
    public async Task CancellationDisposesObservationButDoesNotTerminateRealGame()
    {
        var observer = new WindowsProcessObserver();
        var before = observer.Capture();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var process = Start("Game", 0, 0, 30000);
        using var observerHandle = Process.GetProcessById(process.Id);
        var initial = WindowsTrackedGameProcess.TryRetain(process)!;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GameSessionMonitor(observer, Options)
            .MonitorAsync(Evidence(before, started, initial), _ => { }, cancellation.Token));
        Assert.False(observerHandle.HasExited);
        observerHandle.Kill();
        await observerHandle.WaitForExitAsync();
    }

    [WindowsFact]
    public async Task ObserverChecksCreationIdentityAndRecordsActualParentAndPath()
    {
        using var process = Start("Game", 0, 0, 10000);
        var observer = new WindowsProcessObserver();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ProcessObservation? observed;
        do
        {
            observed = observer.Capture().FirstOrDefault(item => item.Id == process.Id);
            if (observed?.Identity != null) break;
            await Task.Delay(50, timeout.Token);
        } while (true);
        Assert.Equal(Environment.ProcessId, observed.ParentId);
        Assert.True(ProcessMatcher.SamePath(Path.Combine(directory, "Game.exe"), observed.ExecutablePath));
        Assert.Null(observer.TryTrack(new ProcessIdentity(process.Id, observed.StartedAtUtc!.Value.AddSeconds(1))));
        using var tracked = observer.TryTrack(observed.Identity!.Value);
        Assert.NotNull(tracked);
        process.Kill();
        await process.WaitForExitAsync();
    }

    private static GameSessionOptions Options => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(100), LaunchSearchTimeout = TimeSpan.FromSeconds(4),
        ExitGrace = TimeSpan.FromMilliseconds(300), MaxProvisionalLifetime = TimeSpan.FromSeconds(15)
    };

    private GameLaunchEvidence Evidence(IReadOnlyList<ProcessObservation> baseline, DateTimeOffset started, WindowsTrackedGameProcess initial) =>
        new(Guid.NewGuid(), started, Path.Combine(directory, "Launcher.exe"), "Game.exe", Path.Combine(directory, "Game.exe"),
            directory, baseline, initial, initial.Observation);

    private Process Start(string name, int beforeSpawn, int overlap, int finalLifetime, params string[] children)
    {
        var info = new ProcessStartInfo(Path.Combine(directory, name + ".exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (string token in new[] { "--session-chain", events, beforeSpawn.ToString(), overlap.ToString(), finalLifetime.ToString() })
            info.ArgumentList.Add(token);
        foreach (string child in children) info.ArgumentList.Add(Path.Combine(directory, child + ".exe"));
        return Process.Start(info)!;
    }

    public void Dispose()
    {
        // Only recorded processes launched from this unique test directory may be
        // cleaned up, and creation time/path are checked before using the handle.
        if (File.Exists(events))
        {
            foreach (int id in File.ReadAllLines(events).Select(int.Parse))
            {
                try
                {
                    using var process = Process.GetProcessById(id);
                    if (process.StartTime.ToUniversalTime() >= createdAt.UtcDateTime &&
                        ProcessMatcher.IsWithinDirectory(WindowsProcessObserver.ImagePath(id), directory))
                    { process.Kill(); process.WaitForExit(); }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }
        // Windows can signal process exit before its executable image section
        // (or a runner's scanner handle) is released. Retry only deletion of this
        // fixture's owned staging directory; preserve all session assertions.
        var cleanup = Stopwatch.StartNew();
        while (true)
        {
            try { Directory.Delete(directory, recursive: true); break; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && cleanup.Elapsed < TimeSpan.FromSeconds(5))
            { Thread.Sleep(100); }
        }
    }
}
