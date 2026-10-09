using SBridge.Core;
using SBridge.Launching;
using Xunit;

namespace SBridge.Tests;

public class EpicIdentityTests
{
    [Fact]
    public void CatalogIdentityHasExactCanonicalEscapingAndNoArgumentQuery()
    {
        string identity = EpicLaunchIdentity.Create("namespace", "catalog-id", "Game_1.2");
        Assert.Equal("namespace:catalog-id:Game_1.2", identity);
        string target = EpicLaunchIdentity.Target(identity);
        Assert.Equal("com.epicgames.launcher://apps/namespace%3Acatalog-id%3AGame_1.2?action=launch&silent=true", target);
        EpicLaunchIdentity.ValidateTarget(target);
        var game = new Game(Guid.NewGuid(), "Game", "epic", identity, GameLaunchKind.EpicLauncher, target, [], @"C:\Games\Game.exe", @"C:\Games");
        var request = LegacyLaunchRequest.FromGame(game);
        Assert.Equal(LegacyLaunchKind.Epic, request.Kind);
        Assert.Equal(target, request.Target); Assert.Empty(request.Arguments);
    }

    [Theory]
    [InlineData("namespace:catalog:app?evil=1")]
    [InlineData("namespace:catalog:app/other")]
    [InlineData("namespace::app")]
    [InlineData("namespace:catalog:app:extra")]
    [InlineData("namespace:catalog:app%3F")]
    public void InvalidIdentitiesCannotInjectUriPathOrQuery(string identity) =>
        Assert.Throws<ArgumentException>(() => EpicLaunchIdentity.Target(identity));

    [Fact]
    public void UnverifiedArgumentsAndMismatchedTargetsFailAtRegistrationBeforeResources()
    {
        string target = EpicLaunchIdentity.Target("ns:item:app");
        Assert.Throws<ArgumentException>(() => new Game(Guid.NewGuid(), "Game", "epic", "ns:item:app", GameLaunchKind.EpicLauncher,
            target, ["--flag"], "", null));
        Assert.Throws<ArgumentException>(() => new Game(Guid.NewGuid(), "Game", "epic", "ns:item:other", GameLaunchKind.EpicLauncher,
            target, [], "", null));
        Assert.Throws<ArgumentException>(() => EpicLaunchIdentity.ValidateTarget(target + "&args=ignored"));
        Assert.Throws<ArgumentException>(() => EpicLaunchIdentity.ValidateTarget(target.Replace("%3A", ":")));
    }

    [Fact]
    public void RediscoveryRetainsUuidAndProfileButSameAppInOtherCatalogIsDistinct()
    {
        static Game Definition(string catalog, string name) => new(Guid.NewGuid(), name, "epic", "ns:" + catalog + ":app",
            GameLaunchKind.EpicLauncher, EpicLaunchIdentity.Target("ns:" + catalog + ":app"), [], "Game.exe", @"C:\Games");
        var settings = new AppSettings();
        var original = GameCatalog.Register(settings, Definition("item", "Original"), new GameProfile(SteamInputMode.Disabled, "RealGame.exe"));
        var refreshed = GameCatalog.Register(settings, Definition("item", "遊戲 Café"));
        Assert.Equal(original.Id, refreshed.Id);
        Assert.Equal("RealGame.exe", settings.GetProfile(refreshed.ProfileKey).WatchProcess);
        Assert.Equal(SteamInputMode.Disabled, settings.GetProfile(refreshed.ProfileKey).SteamInput);
        Assert.NotEqual(original.Id, GameCatalog.Register(settings, Definition("other-item", "Original")).Id);
    }
}
