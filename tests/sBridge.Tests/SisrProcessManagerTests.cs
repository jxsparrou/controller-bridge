using System.ComponentModel;
using SBridge.Sisr;
using Xunit;

namespace SBridge.Tests;

public class SisrProcessManagerTests
{
    [Fact]
    public void ExistingExternalInstanceIsUntouchedAndStartupLeaseIsReleased()
    {
        var lease = new Lease();
        bool started = false;
        var external = new FakeProcess();
        Assert.Throws<InvalidOperationException>(() => SisrProcessManager.Start(
            () => { started = true; return external; }, () => lease, () => true, new Shutdown(), _ => { }));
        Assert.False(started);
        Assert.Equal(1, lease.DisposeCount);
        Assert.Equal(0, external.KillCount);
        Assert.Equal(0, external.DisposeCount);
    }

    [Fact]
    public void BusyLeaseNeverStartsOrAdoptsAnyProcess()
    {
        bool started = false;
        Assert.Throws<InvalidOperationException>(() => SisrProcessManager.Start(
            () => { started = true; return new FakeProcess(); },
            () => throw new InvalidOperationException("Busy"), () => false, new Shutdown(), _ => { }));
        Assert.False(started);
    }

    [Fact]
    public void ProcessStartFailureReleasesLeaseWithoutShutdownRequest()
    {
        var lease = new Lease();
        var shutdown = new Shutdown();
        Assert.Throws<Win32Exception>(() => SisrProcessManager.Start(
            () => throw new Win32Exception("Startup failed"), () => lease, () => false, shutdown, _ => { }));
        Assert.Equal(1, lease.DisposeCount);
        Assert.Equal(0, shutdown.Requests);
    }

    [Fact]
    public void EarlyExitFailsStartupAndDisposesOnlyOwnedResources()
    {
        var process = new FakeProcess { HasExited = true };
        var lease = new Lease();
        var shutdown = new Shutdown();
        Assert.Throws<InvalidOperationException>(() => Create(process, lease, shutdown));
        Assert.Equal(1, process.DisposeCount);
        Assert.Equal(1, lease.DisposeCount);
        Assert.Equal(0, process.KillCount);
        Assert.Equal(0, shutdown.Requests);
    }

    [Fact]
    public async Task GracefulQuitDoesNotKillOrRequestWindowCloseAndStopIsIdempotent()
    {
        var process = new FakeProcess();
        var lease = new Lease();
        var shutdown = new Shutdown { OnRequest = () => process.HasExited = true, Accepted = true };
        using var manager = Create(process, lease, shutdown);
        await Task.WhenAll(manager.StopAsync(), manager.StopAsync());
        Assert.Equal(1, shutdown.Requests);
        Assert.Equal(0, process.KillCount);
        Assert.Equal(0, process.CloseCount);
        Assert.Equal(1, process.DisposeCount);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public void GameLaunchExceptionStillCleansOwnedProcessBeforeEscaping()
    {
        var process = new FakeProcess();
        var lease = new Lease();
        var shutdown = new Shutdown { Accepted = true, OnRequest = () => process.HasExited = true };
        Assert.Throws<IOException>((Action)(() =>
        {
            using var manager = Create(process, lease, shutdown);
            throw new IOException("Game launch failed");
        }));
        Assert.Equal(1, shutdown.Requests);
        Assert.Equal(1, lease.DisposeCount);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public void AlreadyExitedOwnedProcessDoesNotIssueAnyShutdown()
    {
        var process = new FakeProcess();
        var shutdown = new Shutdown();
        using var manager = Create(process, new Lease(), shutdown);
        process.HasExited = true;
        manager.Dispose();
        Assert.Equal(0, shutdown.Requests);
        Assert.Equal(0, process.CloseCount);
        Assert.Equal(0, process.KillCount);
    }

    [Fact]
    public void MissingApiUsesWindowCloseWithoutKillingIfItExits()
    {
        var process = new FakeProcess { WindowCloses = true };
        using var manager = Create(process, new Lease(), new Shutdown());
        manager.Dispose();
        Assert.Equal(1, process.CloseCount);
        Assert.Equal(0, process.KillCount);
    }

    [Fact]
    public void ApiFailureFallsBackOnlyToRetainedProcess()
    {
        var owned = new FakeProcess();
        var unrelated = new FakeProcess();
        var shutdown = new Shutdown { Failure = new IOException("Ownership/API unavailable") };
        var log = new List<string>();
        using var manager = Create(owned, new Lease(), shutdown, log.Add);
        manager.Dispose();
        Assert.Equal(1, owned.KillCount);
        Assert.Equal(0, unrelated.KillCount);
        Assert.Equal(0, unrelated.CloseCount);
        Assert.Contains(log, line => line.Contains("only owned SISR"));
    }

    [Fact]
    public void AcceptedQuitThatNeverExitsHasBoundedFallback()
    {
        var process = new FakeProcess();
        var lease = new Lease();
        using var manager = Create(process, lease, new Shutdown { Accepted = true });
        manager.Dispose();
        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public void FailedWindowCloseStillAllowsOwnedHandleFallback()
    {
        var process = new FakeProcess { WindowFailure = new Win32Exception("Window unavailable") };
        using var manager = Create(process, new Lease(), new Shutdown());
        manager.Dispose();
        Assert.Equal(1, process.KillCount);
    }

    [Fact]
    public void CleanupFailureIsLoggedAndStillReleasesLease()
    {
        var process = new FakeProcess { KillFailure = new Win32Exception("Denied") };
        var lease = new Lease();
        var log = new List<string>();
        using var manager = Create(process, lease, new Shutdown(), log.Add);
        manager.Dispose();
        Assert.Contains(log, line => line.Contains("cleanup failed"));
        Assert.Equal(1, process.DisposeCount);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Theory]
    [InlineData("v0.6.1", true)]
    [InlineData("0.6.1", true)]
    [InlineData("v0.6.1-2-gabc", true)]
    [InlineData("v0.6.2", true)]
    [InlineData("v0.6.0", false)]
    [InlineData("v0.7.0", false)]
    [InlineData("v1.0.0", false)]
    [InlineData("invalid", false)]
    [InlineData(null, false)]
    public void QuitContractIsRestrictedToAuditedVersionFamily(string? version, bool expected)
    {
        Assert.Equal(expected, SisrApiContract.SupportsQuit(version));
    }

    private static SisrProcessManager Create(FakeProcess process, Lease lease, Shutdown shutdown, Action<string>? log = null) =>
        SisrProcessManager.Start(() => process, () => lease, () => false, shutdown, log ?? (_ => { }), TimeSpan.FromMilliseconds(10));

    private sealed class Lease : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class Shutdown : ISisrShutdown
    {
        public int Requests { get; private set; }
        public bool Accepted { get; init; }
        public Action? OnRequest { get; init; }
        public Exception? Failure { get; init; }
        public Task<bool> RequestQuitAsync(IOwnedSisrProcess process, CancellationToken cancellationToken)
        {
            Requests++;
            if (Failure != null) throw Failure;
            OnRequest?.Invoke();
            return Task.FromResult(Accepted);
        }
    }

    private sealed class FakeProcess : IOwnedSisrProcess
    {
        public int Id => 123;
        public bool HasExited { get; set; }
        public bool WindowCloses { get; init; }
        public Exception? KillFailure { get; init; }
        public Exception? WindowFailure { get; init; }
        public int CloseCount { get; private set; }
        public int KillCount { get; private set; }
        public int DisposeCount { get; private set; }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => HasExited
            ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancellationToken);
        public bool CloseMainWindow()
        {
            CloseCount++;
            if (WindowFailure != null) throw WindowFailure;
            if (WindowCloses) HasExited = true;
            return WindowCloses;
        }
        public void Kill()
        {
            KillCount++;
            if (KillFailure != null) throw KillFailure;
            HasExited = true;
        }
        public void Dispose() => DisposeCount++;
    }
}
