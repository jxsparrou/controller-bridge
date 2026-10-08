using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SBridge.Configuration;
using SBridge.Core;
using SBridge.Launching;
using SBridge.Steam;
using Xunit;

namespace SBridge.Tests;

public class GamePersistenceTests
{
    [Fact]
    public void ExistingSchemaOneWithoutCatalogLoadsAndCanRegisterWithoutLosingExtensionsOrCredential()
    {
        var protector = new TestProtector(); var settings = new AppSettings { SteamGridDbApiKey = "synthetic-key" };
        var node = JsonNode.Parse(JsonSettingsCodec.Encode(settings, null, protector))!.AsObject();
        node.Remove("games"); node["futureSettings"] = 42;
        var decoded = JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(node.ToJsonString()), protector);
        Assert.Empty(decoded.Settings.Games);
        var game = GameCatalog.Register(decoded.Settings, GameCatalogTests.Definition(arguments: new[] { "", "Player One", @"C:\Folder With Spaces\" }));
        var saved = JsonSettingsCodec.Decode(JsonSettingsCodec.Encode(decoded.Settings, decoded.Data, protector), protector);
        Assert.Equal(game.Id, Assert.Single(saved.Settings.Games).Key);
        Assert.Equal(game.Arguments, saved.Settings.Games[game.Id].Arguments);
        Assert.Equal("synthetic-key", saved.Settings.SteamGridDbApiKey);
        Assert.Equal(42, saved.Data.Extra!["futureSettings"].GetInt32());
    }

    [Fact]
    public void RegistrationAndProfileCommitTogetherAndPersistStableIdAcrossRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sBridge-game-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "config.json"); var store = new JsonSettingsStore(new TestProtector());
            var document = store.LoadOrMigrate(path, Path.Combine(directory, "missing.cfg"), new AppSettings());
            var game = GameCatalog.Register(document.Settings, GameCatalogTests.Definition(), new GameProfile(SteamInputMode.Disabled, "Actual.exe"));
            Assert.True(store.Save(document).Succeeded);
            var reloaded = store.LoadOrMigrate(path, Path.Combine(directory, "missing.cfg"), new AppSettings());
            Assert.Equal(game.Id, reloaded.Settings.Games[game.Id].Id);
            Assert.False(reloaded.Settings.IsSisrEnabledFor(game.ProfileKey));
            Assert.Equal("Actual.exe", reloaded.Settings.GetProfile(game.ProfileKey).WatchProcess);
            var again = GameCatalog.Register(reloaded.Settings, GameCatalogTests.Definition(name: "Renamed"));
            Assert.Equal(game.Id, again.Id);
            Assert.Equal("Actual.exe", reloaded.Settings.GetProfile(again.ProfileKey).WatchProcess);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CorruptIdsAndDuplicateRegistrationCannotOverwriteExistingState()
    {
        var settings = new AppSettings(); var game = GameCatalog.Register(settings, GameCatalogTests.Definition());
        var protector = new TestProtector(); var original = JsonNode.Parse(JsonSettingsCodec.Encode(settings, null, protector))!.AsObject();
        foreach (string badId in new[] { "invalid", Guid.Empty.ToString("D"), "{" + game.Id.ToString("D") + "}" })
        {
            var node = original.DeepClone().AsObject(); var games = node["games"]!.AsObject();
            JsonNode data = games[game.ProfileKey]!.DeepClone(); games.Remove(game.ProfileKey); games[badId] = data;
            Assert.Throws<InvalidDataException>(() => JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(node.ToJsonString()), protector));
        }
        var duplicateNode = original.DeepClone().AsObject();
        duplicateNode["games"]!.AsObject()[Guid.NewGuid().ToString("D")] = duplicateNode["games"]![game.ProfileKey]!.DeepClone();
        Assert.Throws<InvalidDataException>(() => JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(duplicateNode.ToJsonString()), protector));
        var invalidKind = original.DeepClone().AsObject(); invalidKind["games"]![game.ProfileKey]!["launchKind"] = 0;
        Assert.Throws<InvalidDataException>(() => JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(invalidKind.ToJsonString()), protector));
    }

    [Fact]
    public void UnknownGameFieldsSurviveMetadataUpdate()
    {
        var protector = new TestProtector(); var settings = new AppSettings(); var game = GameCatalog.Register(settings, GameCatalogTests.Definition());
        var node = JsonNode.Parse(JsonSettingsCodec.Encode(settings, null, protector))!;
        node["games"]![game.ProfileKey]!["futureArtwork"] = new JsonObject { ["url"] = "https://example.invalid/art" };
        var loaded = JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(node.ToJsonString()), protector);
        GameCatalog.Register(loaded.Settings, GameCatalogTests.Definition(name: "Updated"));
        using var output = JsonDocument.Parse(JsonSettingsCodec.Encode(loaded.Settings, loaded.Data, protector));
        Assert.Equal("https://example.invalid/art", output.RootElement.GetProperty("games").GetProperty(game.ProfileKey).GetProperty("futureArtwork").GetProperty("url").GetString());
    }

    [Fact]
    public void GameIdLaunchOptionsDoNotChangeSteamExeQuotingAppIdOrOtherFields()
    {
        const string executable = @"C:\Program Files\sBridge\sBridge.exe";
        const string directory = @"C:\Program Files\sBridge\";
        var old = SteamShortcutBuilder.Create(0, "Example Game", executable, directory, "Pkg_123!App Game.exe", "");
        var modern = SteamShortcutBuilder.Create(0, "Example Game", executable, directory, GameLaunchCommand.Options(Guid.NewGuid()), "");
        foreach (var field in old.Children.Where(field => field.Name != "LaunchOptions"))
        {
            var actual = Assert.Single(modern.Children, child => child.Name == field.Name);
            Assert.Equal(field.Type, actual.Type); Assert.Equal(field.StringValue, actual.StringValue); Assert.Equal(field.IntValue, actual.IntValue);
        }
        Assert.Equal(unchecked((int)0x94CFC93E), Assert.Single(modern.Children, field => field.Name == "appid").IntValue);
        Assert.Equal("\"" + executable + "\"", Assert.Single(modern.Children, field => field.Name == "Exe").StringValue);
        Assert.StartsWith("launch ", Assert.Single(modern.Children, field => field.Name == "LaunchOptions").StringValue);
        Assert.Empty(Assert.Single(modern.Children, field => field.Name == "tags").Children);
    }
}
