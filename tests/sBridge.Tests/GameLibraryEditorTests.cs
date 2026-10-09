using System.Text;
using System.Text.Json.Nodes;
using SBridge.Configuration;
using SBridge.Core;
using SBridge.Launching;
using Xunit;

namespace SBridge.Tests;

public class GameLibraryEditorTests
{
    private static Game Original(string[]? arguments = null) => new(Guid.NewGuid(), "Original", "win32", @"C:\Old\Game.exe",
        GameLaunchKind.Executable, @"C:\Old\Game.exe", arguments ?? [], "Launcher.exe", @"C:\Old");

    [Fact]
    public void TargetArgumentAndProfileEditsKeepUuidAndIdDispatchWhileUpdatingStandardIdentity()
    {
        var settings = new AppSettings { SisrEnabled = true }; var original = GameCatalog.Register(settings, Original());
        string[] tokens = ["Player One", "", "quote\"inside", @"C:\Trailing Space\", "遊戲", "line\r\nbreak"];
        var edited = GameLibraryEditor.Definition(original, "Renamed", @"D:\Moved\Game.exe", tokens, "Game.exe", @"D:\Moved");
        var profile = new GameProfile(SteamInputMode.Disabled, "Actual.exe");
        int saves = 0;
        Assert.True(GameLibraryEditor.TryCommit(settings, original, settings.GetProfile(original.ProfileKey), edited, profile, () => { saves++; return true; }));
        Assert.Equal(1, saves); Assert.Equal(original.Id, edited.Id); Assert.Equal(edited.Target, edited.ProviderId);
        var request = LegacyLaunchRequest.FromGame(GameLaunchCommand.Resolve(settings, original.Id));
        Assert.Equal(tokens, request.Arguments); Assert.Equal(@"D:\Moved\Game.exe", request.Target);
        Assert.False(settings.IsSisrEnabledFor(original.ProfileKey)); Assert.Equal("Actual.exe", settings.GetProfile(original.ProfileKey).WatchProcess);
        var rediscovered = GameCatalog.Register(settings, edited.WithId(Guid.NewGuid())); Assert.Equal(original.Id, rediscovered.Id);
    }

    [Fact]
    public void FailedOrThrowingPersistenceRestoresExactGameAndProfileWithoutChangingOtherState()
    {
        var settings = new AppSettings(); var original = GameCatalog.Register(settings, Original(), new GameProfile(SteamInputMode.Enabled, "Old.exe"));
        var before = settings.GetProfile(original.ProfileKey); settings.SelectedSteamAccountIds.Add("1");
        var edit = GameLibraryEditor.Definition(original, "New", original.Target, ["--new"], "New.exe", null);
        Assert.False(GameLibraryEditor.TryCommit(settings, original, before, edit, new GameProfile(), () => false));
        Assert.Same(original, settings.Games[original.Id]); Assert.Equal(before, settings.GetProfile(original.ProfileKey));
        Assert.Throws<IOException>(() => GameLibraryEditor.TryCommit(settings, original, before, edit, new GameProfile(), () => throw new IOException("denied")));
        Assert.Same(original, settings.Games[original.Id]); Assert.Equal(new[] { "1" }, settings.SelectedSteamAccountIds);
    }

    [Fact]
    public void StaleGameStaleProfileDuplicateVariantAndChangedUuidFailBeforePersistence()
    {
        var settings = new AppSettings(); var original = GameCatalog.Register(settings, Original(["one"])); var before = settings.GetProfile(original.ProfileKey);
        var edit = GameLibraryEditor.Definition(original, "New", original.Target, ["two"], "", null);
        var other = GameCatalog.Register(settings, Original(["two"]));
        bool persisted = false;
        Assert.Throws<InvalidOperationException>(() => GameLibraryEditor.TryCommit(settings, original, before, edit, new GameProfile(), () => persisted = true));
        Assert.False(persisted); Assert.Same(original, settings.Games[original.Id]); Assert.Same(other, settings.Games[other.Id]);
        Assert.Throws<ArgumentException>(() => GameLibraryEditor.TryCommit(settings, original, before, edit.WithId(Guid.NewGuid()), new GameProfile(), () => persisted = true));
        settings.SetWatchProcess(original.ProfileKey, "Changed.exe");
        Assert.Throws<InvalidOperationException>(() => GameLibraryEditor.TryCommit(settings, original, before, original, new GameProfile(), () => persisted = true));
        Assert.Equal("Changed.exe", settings.GetProfile(original.ProfileKey).WatchProcess);
        settings.Games[original.Id] = original.WithId(original.Id);
        Assert.Throws<InvalidOperationException>(() => GameLibraryEditor.TryCommit(settings, original, settings.GetProfile(original.ProfileKey), original, new GameProfile(), () => persisted = true));
        Assert.False(persisted);
    }

    [Fact]
    public void ProviderKindAndEpicIdentityCannotBeReclassifiedByAnEdit()
    {
        var original = Original(); var settings = new AppSettings(); settings.Games.Add(original.Id, original);
        var changedKind = new Game(original.Id, "Name", original.Provider, original.ProviderId, GameLaunchKind.PackagedApplication, "Pkg!App", [], "", null);
        Assert.Throws<ArgumentException>(() => GameLibraryEditor.TryCommit(settings, original, new GameProfile(), changedKind, new GameProfile(), () => true));
        var packaged = new Game(Guid.NewGuid(), "Packaged Win32", "xbox", "opaque-packaged-identity", GameLaunchKind.Executable, @"C:\Old\Game.exe", [], "", null);
        Assert.Equal(packaged.ProviderId, GameLibraryEditor.Definition(packaged, "New", @"D:\New.exe", [], "", null).ProviderId);
        var epic = new Game(Guid.NewGuid(), "Epic", "epic", "ns:item:app", GameLaunchKind.EpicLauncher, EpicLaunchIdentity.Target("ns:item:app"), [], "Game.exe", @"C:\Epic");
        Assert.Throws<ArgumentException>(() => GameLibraryEditor.Definition(epic, "Changed", epic.Target, ["--extra"], "", null));
        Assert.Throws<ArgumentException>(() => GameLibraryEditor.Definition(epic, "Changed", EpicLaunchIdentity.Target("ns:item:other"), [], "", null));
        settings.Games.Add(epic.Id, epic);
        var swapped = new Game(epic.Id, "Changed", "epic", "ns:item:other", GameLaunchKind.EpicLauncher, EpicLaunchIdentity.Target("ns:item:other"), [], "", null);
        Assert.Throws<ArgumentException>(() => GameLibraryEditor.TryCommit(settings, epic, new GameProfile(), swapped, new GameProfile(), () => true));
    }

    [Fact]
    public void GameAndProfileUnknownJsonExtensionsSurviveEditsBackToAutomatic()
    {
        var settings = new AppSettings(); var original = GameCatalog.Register(settings, Original(), new GameProfile(SteamInputMode.Enabled, "Old.exe"));
        var protector = new NoSecret(); var json = JsonNode.Parse(JsonSettingsCodec.Encode(settings, null, protector))!;
        json["games"]![original.ProfileKey]!["futureGame"] = "keep game";
        json["gameProfiles"]![original.ProfileKey]!["futureProfile"] = "keep profile";
        var loaded = JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(json.ToJsonString()), protector);
        original = loaded.Settings.Games[original.Id]; var before = loaded.Settings.GetProfile(original.ProfileKey);
        var edit = GameLibraryEditor.Definition(original, "Edited", original.Target, ["", "Player One"], "", null);
        byte[]? saved = null;
        Assert.True(GameLibraryEditor.TryCommit(loaded.Settings, original, before, edit, new GameProfile(), () =>
        { saved = JsonSettingsCodec.Encode(loaded.Settings, loaded.Data, protector); return true; }));
        var after = JsonNode.Parse(saved!)!;
        Assert.Equal("keep game", (string?)after["games"]![original.ProfileKey]!["futureGame"]);
        Assert.Equal("keep profile", (string?)after["gameProfiles"]![original.ProfileKey]!["futureProfile"]);
        Assert.True(loaded.Settings.IsSisrEnabledFor(original.ProfileKey)); // Automatic uses the global enabled choice.
    }

    [Fact]
    public void ActualAtomicStoreFailureRollsBackDraftAndDoesNotOverwriteExternalEdits()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sBridge-library-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "config.json"); var store = new JsonSettingsStore(new NoSecret());
            var document = store.LoadOrMigrate(path, Path.Combine(directory, "missing.cfg"), new AppSettings());
            var original = GameCatalog.Register(document.Settings, Original()); Assert.True(store.Save(document).Succeeded);
            byte[] external = File.ReadAllBytes(path); var text = JsonNode.Parse(external)!; text["logEnabled"] = false;
            external = Encoding.UTF8.GetBytes(text.ToJsonString()); File.WriteAllBytes(path, external);
            var replacement = GameLibraryEditor.Definition(original, "Unsaved", original.Target, [], "", null);
            Assert.False(GameLibraryEditor.TryCommit(document.Settings, original, document.Settings.GetProfile(original.ProfileKey), replacement, new GameProfile(), () => store.Save(document).Succeeded));
            Assert.Same(original, document.Settings.Games[original.Id]); Assert.Equal(external, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class NoSecret : ISecretProtector
    { public string Protect(string value) => throw new InvalidOperationException(); public string Unprotect(string value) => throw new InvalidOperationException(); }
}
