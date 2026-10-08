using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SBridge.Core;
using SBridge.Launching;
using SBridge.Sessions;

namespace SBridge.Providers;

internal sealed record DiscoveredGame(string Name, string Provider, string ProviderId, GameLaunchKind LaunchKind,
    string Target, IReadOnlyList<string> Arguments, string ProcessHint, string? InstallDirectory)
{
    // Discovery does not allocate persistent IDs or write settings/Steam files.
    public Game CreateRegistration() => new(Guid.NewGuid(), Name, Provider, ProviderId, LaunchKind,
        Target, Arguments, ProcessHint, InstallDirectory);
}

internal sealed record GameDiscoveryResult(IReadOnlyList<DiscoveredGame> Games, IReadOnlyList<string> Warnings);
internal sealed record InitialGameObservation(ITrackedGameProcess? Process, ProcessObservation? Observation);

internal sealed record ProviderLaunchContext(Guid SessionId, DateTimeOffset StartedAtUtc,
    IReadOnlyList<ProcessObservation> BeforeLaunch, string ProcessHint, string? ExpectedPath, string? InstallDirectory)
{
    public GameLaunchEvidence Evidence(LegacyLaunchRequest request, InitialGameObservation initial) =>
        new(SessionId, StartedAtUtc, request.Target, ProcessHint, ExpectedPath, InstallDirectory,
            BeforeLaunch, initial.Process, initial.Observation);
}

internal interface IGameProvider
{
    string Id { get; }
    bool CanLaunch(LegacyLaunchRequest request);
    Task<GameDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken);
    // Deliberately synchronous: packaged COM activation must stay on an STA caller.
    GameLaunchEvidence Launch(LegacyLaunchRequest request, ProviderLaunchContext context, Action<string> log);
}

internal interface IPackagedDiscovery
{
    Task<GameDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken);
}

internal interface IGameActivation
{
    InitialGameObservation Activate(LegacyLaunchRequest request, Action<string> log);
}
