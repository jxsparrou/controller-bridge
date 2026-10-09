using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using SBridge.Launching;
using SBridge.Sessions;

namespace SBridge.Providers;

internal sealed class XboxProvider : IGameProvider
{
    private readonly IPackagedDiscovery discovery;
    private readonly IGameActivation activation;
    public XboxProvider(IPackagedDiscovery discovery, IGameActivation activation) { this.discovery = discovery; this.activation = activation; }
    public string Id => "xbox";
    public bool CanLaunch(LegacyLaunchRequest request) => request.Kind == LegacyLaunchKind.Packaged;
    public Task<GameDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) => discovery.DiscoverAsync(cancellationToken);
    public GameLaunchEvidence Launch(LegacyLaunchRequest request, ProviderLaunchContext context, Action<string> log)
    {
        if (!CanLaunch(request)) throw new ArgumentException("Xbox provider requires a packaged launch target.", nameof(request));
        return context.Evidence(request, activation.Activate(request, log));
    }
}

internal sealed class Win32Provider : IGameProvider
{
    private readonly IGameActivation activation;
    public Win32Provider(IGameActivation activation) => this.activation = activation;
    public string Id => "win32";
    public bool CanLaunch(LegacyLaunchRequest request) => request.Kind == LegacyLaunchKind.Win32;
    public Task<GameDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Custom games are user registrations, not an exhaustive drive scan.
        return Task.FromResult(new GameDiscoveryResult(Array.Empty<DiscoveredGame>(), Array.Empty<string>()));
    }
    public GameLaunchEvidence Launch(LegacyLaunchRequest request, ProviderLaunchContext context, Action<string> log)
    {
        if (!CanLaunch(request)) throw new ArgumentException("Win32 provider requires an executable target.", nameof(request));
        return context.Evidence(request, activation.Activate(request, log));
    }

    [SupportedOSPlatform("windows")]
    public static DiscoveredGame CustomRegistration(string name, string path, string rawArguments)
    {
        string fullPath = Path.GetFullPath(path);
        return new DiscoveredGame(name, "win32", fullPath, Core.GameLaunchKind.Executable, fullPath,
            WindowsCommandLine.Split(rawArguments), fullPath, Path.GetDirectoryName(fullPath));
    }
}

internal sealed class GameProviders
{
    private readonly IReadOnlyList<IGameProvider> providers;
    public GameProviders(params IGameProvider[] providers) => this.providers = Array.AsReadOnly(providers);
    public IGameProvider ForLaunch(LegacyLaunchRequest request)
    {
        IGameProvider? found = null;
        foreach (var provider in providers)
        {
            if (!provider.CanLaunch(request)) continue;
            if (found != null) throw new InvalidOperationException("Multiple providers claim the launch target.");
            found = provider;
        }
        return found ?? throw new InvalidOperationException("No provider supports this launch target.");
    }
}
