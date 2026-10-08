using SBridge.Core;
using SBridge.Launching;
using Xunit;

namespace SBridge.Tests;

public class GameCatalogTests
{
    [Fact]
    public void RediscoveryAndRenameKeepUuidAndExistingProfile()
    {
        var settings = new AppSettings();
        settings.SetSteamInputMode(@"C:\Games\game.exe", SteamInputMode.Disabled);
        var first = GameCatalog.Register(settings, Definition());
        Assert.False(settings.IsSisrEnabledFor(first.ProfileKey));
        settings.SetWatchProcess(first.ProfileKey, "Actual.exe");
        var updated = GameCatalog.Register(settings, Definition(name: "Renamed", providerId: "c:/games/GAME.EXE"));
        Assert.Equal(first.Id, updated.Id);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("Actual.exe", settings.GetProfile(updated.ProfileKey).WatchProcess);
        Assert.False(settings.IsSisrEnabledFor(updated.ProfileKey));
        Assert.Single(settings.Games);
    }

    [Fact]
    public void ArgumentVariantsHaveDistinctIdsAndIndependentProfiles()
    {
        var settings = new AppSettings { SisrEnabled = true };
        var first = GameCatalog.Register(settings, Definition(arguments: new[] { "--profile", "One" }), new GameProfile(SteamInputMode.Disabled));
        var second = GameCatalog.Register(settings, Definition(arguments: new[] { "--profile", "Two" }));
        Assert.NotEqual(first.Id, second.Id);
        Assert.False(settings.IsSisrEnabledFor(first.ProfileKey));
        Assert.True(settings.IsSisrEnabledFor(second.ProfileKey));
        var empty = GameCatalog.Register(settings, Definition());
        var emptyArgument = GameCatalog.Register(settings, Definition(arguments: new[] { "" }));
        Assert.NotEqual(empty.Id, emptyArgument.Id);
    }

    [Fact]
    public void ExplicitAutomaticUuidChoiceDoesNotFallBackToDisabledLegacyTarget()
    {
        var settings = new AppSettings { SisrEnabled = true };
        settings.SetSteamInputMode(@"C:\Games\game.exe", SteamInputMode.Disabled);
        var game = GameCatalog.Register(settings, Definition(), new GameProfile());
        Assert.True(settings.IsSisrEnabledFor(game.ProfileKey));
        Assert.False(settings.IsSisrEnabledFor(game.Target));
    }

    [Fact]
    public void StoredLaunchKindAndHintPositionsDoNotDependOnFileExistsHeuristics()
    {
        var game = new Game(Guid.NewGuid(), "Packaged Win32", "xbox", "Pkg_123!App", GameLaunchKind.PackagedApplication,
            "Pkg_123!App", new[] { "--profile", "Player One", "" }, "Game Folder/Game.exe", null);
        var request = LegacyLaunchRequest.FromGame(game);
        Assert.Equal(LegacyLaunchKind.Packaged, request.Kind);
        Assert.Equal(game.Arguments, request.Arguments);
        Assert.Equal(@"Game Folder\Game.exe", request.ResolveProcessHint(null));
        var custom = new Game(Guid.NewGuid(), "Custom", "win32", "user-registration", GameLaunchKind.Executable,
            "launch-target-without-extension", Array.Empty<string>(), "", null);
        Assert.Equal(LegacyLaunchKind.Win32, LegacyLaunchRequest.FromGame(custom).Kind);
    }

    [Fact]
    public void GameArgumentsAndClonedCatalogCannotBeMutatedThroughInputArray()
    {
        string[] input = { "Original Value" };
        var settings = new AppSettings();
        var game = GameCatalog.Register(settings, Definition(arguments: input));
        input[0] = "Mutated";
        Assert.Equal("Original Value", game.Arguments[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)game.Arguments)[0] = "Mutated");
        settings.Clone().Games.Clear();
        Assert.Single(settings.Games);
    }

    [Fact]
    public void CollisionsAndAmbiguousProviderIdentityNeverReplaceAnotherGame()
    {
        var settings = new AppSettings(); var first = GameCatalog.Register(settings, Definition());
        var collision = new Game(first.Id, "Other", "win32", @"D:\Other.exe", GameLaunchKind.Executable, @"D:\Other.exe", Array.Empty<string>(), "", null);
        Assert.Throws<InvalidOperationException>(() => GameCatalog.Register(settings, collision));
        var duplicate = Definition(); settings.Games.Add(duplicate.Id, duplicate);
        Assert.Throws<InvalidOperationException>(() => GameCatalog.Register(settings, Definition()));
        Assert.Equal(first.Id, settings.Games[first.Id].Id);
    }

    [Fact]
    public void FailedRegistrationCommitRollsBackRegistryAndProfilesBeforeAnyShortcutCanUseThem()
    {
        var settings = new AppSettings(); settings.SetWatchProcess("legacy", "Keep.exe");
        bool attempted = false;
        bool success = GameCatalog.TryCommit(settings, new[] { Definition() }, new[] { new GameProfile(SteamInputMode.Disabled) },
            () => { attempted = true; Assert.Single(settings.Games); return false; }, out var committed);
        Assert.False(success); Assert.True(attempted); Assert.Empty(committed); Assert.Empty(settings.Games);
        Assert.Equal("Keep.exe", settings.GetProfile("legacy").WatchProcess);
        Assert.Single(settings.GameProfiles);
    }

    [Theory]
    [InlineData("launch")]
    [InlineData("launch invalid")]
    [InlineData("launch 00000000-0000-0000-0000-000000000000")]
    [InlineData("launch 01234567-89ab-cdef-0123-456789abcdef extra")]
    [InlineData("launch {01234567-89ab-cdef-0123-456789abcdef}")]
    public void MalformedModernCommandDoesNotBecomeLegacyComLaunch(string line)
    {
        Assert.Throws<ArgumentException>(() => GameLaunchCommand.TryParse(WindowsCommandLine.Split(line), out _));
    }

    [Fact]
    public void ModernCommandsResolveUuidAndUiProfileWhileLegacyKeysRemainUnchanged()
    {
        var settings = new AppSettings(); var game = GameCatalog.Register(settings, Definition());
        string options = GameLaunchCommand.Options(game.Id);
        Assert.True(GameLaunchCommand.TryParse(WindowsCommandLine.Split(options), out Guid id));
        Assert.Same(game, GameLaunchCommand.Resolve(settings, id));
        Assert.Equal(game.ProfileKey, GameLaunchCommand.ShortcutProfileKey(options, settings));
        Assert.Equal(game.Target, GameLaunchCommand.ShortcutTarget(options, settings));
        Assert.Equal(@"C:\Game Folder\old.exe", GameLaunchCommand.ShortcutProfileKey("\"C:\\Game Folder\\old.exe\" --flag", settings));
        Assert.False(GameLaunchCommand.TryParse(new[] { "Pkg_123!App", "Game.exe" }, out _));
        string missing = GameLaunchCommand.Options(Guid.NewGuid());
        Assert.Equal("", GameLaunchCommand.ShortcutProfileKey(missing, settings));
        Assert.Throws<KeyNotFoundException>(() => GameLaunchCommand.Resolve(settings, Guid.NewGuid()));
    }

    internal static Game Definition(string name = "Example Game", string providerId = @"C:\Games\game.exe", string[]? arguments = null) =>
        new(Guid.NewGuid(), name, "win32", providerId, GameLaunchKind.Executable, @"C:\Games\game.exe",
            arguments ?? Array.Empty<string>(), @"C:\Games\game.exe", @"C:\Games");
}
