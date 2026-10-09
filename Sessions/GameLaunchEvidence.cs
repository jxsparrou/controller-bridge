using System;
using System.Collections.Generic;
using System.Linq;

namespace SBridge.Sessions;

// Future providers can return the same evidence without owning session matching.
// MonitorAsync consumes/disposes InitialProcess, including on cancellation/error.
internal sealed class GameLaunchEvidence
{
    public GameLaunchEvidence(Guid sessionId, DateTimeOffset startedAtUtc, string target,
        string processHint, string? expectedPath, string? installDirectory,
        IReadOnlyList<ProcessObservation> beforeLaunch, ITrackedGameProcess? initialProcess,
        ProcessObservation? initialObservation = null)
    {
        SessionId = sessionId;
        StartedAtUtc = startedAtUtc;
        Target = target;
        ExpectedName = ProcessMatcher.ExecutableName(processHint);
        ExpectedPath = expectedPath;
        InstallDirectory = installDirectory;
        BeforeLaunch = Array.AsReadOnly(beforeLaunch.ToArray());
        InitialProcess = initialProcess;
        InitialObservation = initialObservation;
    }

    public Guid SessionId { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public string Target { get; }
    public string ExpectedName { get; }
    public string? ExpectedPath { get; }
    public string? InstallDirectory { get; }
    public IReadOnlyList<ProcessObservation> BeforeLaunch { get; }
    public ITrackedGameProcess? InitialProcess { get; }
    public ProcessObservation? InitialObservation { get; }
}
