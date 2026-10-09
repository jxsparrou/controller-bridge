using System;
using System.Threading;
using System.Threading.Tasks;
using SBridge.Launching;
using SBridge.Sessions;

namespace SBridge.Providers;

// Epic manifest discovery and launcher-protocol concepts adapted from Luke1505's
// PR #1: https://github.com/jxsparrou/controller-bridge/pull/1
// Typed parsing, stable identities, and session evidence are a new implementation.
internal sealed class EpicProvider : IGameProvider
{
    private readonly IEpicDiscovery discovery;
    private readonly IGameActivation activation;
    public EpicProvider(IEpicDiscovery discovery, IGameActivation activation)
    { this.discovery = discovery; this.activation = activation; }
    public string Id => "epic";
    public bool CanLaunch(LegacyLaunchRequest request) => request.Kind == LegacyLaunchKind.Epic;
    public Task<GameDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) => discovery.DiscoverAsync(cancellationToken);
    public GameLaunchEvidence Launch(LegacyLaunchRequest request, ProviderLaunchContext context, Action<string> log)
    {
        if (!CanLaunch(request)) throw new ArgumentException("Epic provider requires an Epic launcher request.", nameof(request));
        return context.Evidence(request, activation.Activate(request, log));
    }
}

internal interface IEpicDiscovery
{
    Task<GameDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken);
}
