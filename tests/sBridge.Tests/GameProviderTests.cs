using SBridge.Core;
using SBridge.Launching;
using SBridge.Providers;
using SBridge.Sessions;
using Xunit;

namespace SBridge.Tests;

public class GameProviderTests
{
    [Fact]
    public void RoutingUsesExplicitKindAndEvidenceWithoutChangingArgumentOrProfileMetadata()
    {
        var activation = new Activation();
        var xbox = new XboxProvider(new Discovery(), activation);
        var win32 = new Win32Provider(activation);
        var router = new GameProviders(xbox, win32);
        var packaged = LegacyLaunchRequest.Parse(new[] { "Pkg_123!App", "Game Folder/Game.exe", "Player One", "" }, _ => false)!;
        Assert.Same(xbox, router.ForLaunch(packaged));
        var baseline = new[] { new ProcessObservation(9, DateTimeOffset.UtcNow.AddHours(-1), "unrelated", null, null, false) };
        var context = new ProviderLaunchContext(Guid.NewGuid(), DateTimeOffset.UtcNow, baseline, "Actual.exe", null, @"C:\Games");
        int caller = Environment.CurrentManagedThreadId;
        var evidence = xbox.Launch(packaged, context, _ => { });
        Assert.Equal(caller, activation.CallerThread);
        Assert.Same(packaged, activation.Request);
        Assert.Equal(new[] { "Player One", "" }, activation.Request!.Arguments);
        Assert.Equal(context.SessionId, evidence.SessionId);
        Assert.Equal(context.StartedAtUtc, evidence.StartedAtUtc);
        Assert.Equal("Actual", evidence.ExpectedName);
        Assert.Equal(context.InstallDirectory, evidence.InstallDirectory);
        Assert.Equal(baseline, evidence.BeforeLaunch);
        var stored = new Game(Guid.NewGuid(), "Custom", "xbox", "packaged-win32-identity", GameLaunchKind.Executable,
            "target-without-extension", Array.Empty<string>(), "", null);
        Assert.Same(win32, router.ForLaunch(LegacyLaunchRequest.FromGame(stored)));
    }

    [Fact]
    public void WrongAndAmbiguousProvidersFailBeforeActivation()
    {
        var activation = new Activation();
        var provider = new XboxProvider(new Discovery(), activation);
        var request = LegacyLaunchRequest.Parse(new[] { "Game.exe" }, _ => false)!;
        var context = new ProviderLaunchContext(Guid.NewGuid(), DateTimeOffset.UtcNow, Array.Empty<ProcessObservation>(), "", null, null);
        Assert.Throws<ArgumentException>(() => provider.Launch(request, context, _ => { }));
        Assert.Null(activation.Request);
        Assert.Throws<InvalidOperationException>(() => new GameProviders(provider).ForLaunch(request));
        Assert.Throws<InvalidOperationException>(() => new GameProviders(new Win32Provider(activation), new Win32Provider(activation)).ForLaunch(request));
    }

    [Fact]
    public async Task DiscoveryPassesCancellationAndDoesNotRegisterGamesOrScanCustomDrives()
    {
        var discovery = new Discovery();
        using var cancellation = new CancellationTokenSource();
        var provider = new XboxProvider(discovery, new Activation());
        Assert.Empty((await provider.DiscoverAsync(cancellation.Token)).Games);
        Assert.Equal(cancellation.Token, discovery.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DiscoverAsync(cancellation.Token));
        Assert.Empty((await new Win32Provider(new Activation()).DiscoverAsync(CancellationToken.None)).Games);
    }

    [Fact]
    public void JsonMetadataKeepsPipesUnicodeHintsAndInstallDirectoryAsStructuredFields()
    {
        const string json = """
        {"schemaVersion":1,"games":[{"name":"遊戲 | Café","aumid":"Pkg_123!App","executable":"Game Folder/Game.exe","installDirectory":"C:\\Packages\\Game"}],"warnings":["One manifest was unreadable"]}
        """;
        var result = XboxDiscoveryCodec.Decode(json);
        var game = Assert.Single(result.Games);
        Assert.Equal("遊戲 | Café", game.Name);
        Assert.Equal("Pkg_123!App", game.ProviderId);
        Assert.Equal(GameLaunchKind.PackagedApplication, game.LaunchKind);
        Assert.Equal(@"Game Folder\Game.exe", game.ProcessHint);
        Assert.Equal(@"C:\Packages\Game", game.InstallDirectory);
        Assert.Single(result.Warnings);
        Assert.NotEqual(Guid.Empty, game.CreateRegistration().Id);
        Assert.NotEqual(game.CreateRegistration().Id, game.CreateRegistration().Id); // Persistence/catalog assigns stability, not scanning.
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"games\":[],\"warnings\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"games\":{},\"warnings\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"games\":[{\"name\":\"Game\",\"aumid\":\"bad-id\",\"executable\":\"game.exe\",\"installDirectory\":null}],\"warnings\":[]}")]
    public void MalformedDiscoveryIsAnErrorRatherThanEmptySuccess(string json)
    {
        Assert.Throws<InvalidDataException>(() => XboxDiscoveryCodec.Decode(json));
    }

    private sealed class Activation : IGameActivation
    {
        public LegacyLaunchRequest? Request { get; private set; }
        public int CallerThread { get; private set; }
        public InitialGameObservation Activate(LegacyLaunchRequest request, Action<string> log)
        { Request = request; CallerThread = Environment.CurrentManagedThreadId; return new InitialGameObservation(null, null); }
    }
    private sealed class Discovery : IPackagedDiscovery
    {
        public CancellationToken Token { get; private set; }
        public Task<GameDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
        {
            Token = cancellationToken; cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new GameDiscoveryResult(Array.Empty<DiscoveredGame>(), Array.Empty<string>()));
        }
    }
}
