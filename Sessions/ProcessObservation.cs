using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SBridge.Sessions;

internal readonly record struct ProcessIdentity(int Id, DateTimeOffset StartedAtUtc);

internal sealed record ProcessObservation(int Id, DateTimeOffset? StartedAtUtc, string Name,
    string? ExecutablePath, int? ParentId, bool HasVisibleWindow)
{
    public ProcessIdentity? Identity => StartedAtUtc is { } start ? new ProcessIdentity(Id, start) : null;
}

internal sealed record ProcessLifetime(ProcessIdentity Identity, DateTimeOffset? ExitedAtUtc);

// Ownership here means ownership of an observation handle, never permission to kill a game.
internal interface ITrackedGameProcess : IDisposable
{
    ProcessIdentity Identity { get; }
    bool HasExited { get; }
    DateTimeOffset? ExitedAtUtc { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal interface IProcessObserver
{
    IReadOnlyList<ProcessObservation> Capture();
    ITrackedGameProcess? TryTrack(ProcessIdentity identity);
}

internal interface ISessionClock
{
    DateTimeOffset UtcNow { get; }
    TimeSpan Elapsed { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SessionClock : ISessionClock
{
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeSpan Elapsed => stopwatch.Elapsed;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
