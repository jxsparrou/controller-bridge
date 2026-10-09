using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SBridge.Configuration;
using SBridge.Core;
using SBridge.Sisr;
using Xunit;

namespace SBridge.Tests;

public class ControllerProfileTests
{
    [Fact]
    public void IndependentControllerOnlyProfilesSurviveOtherOverrideChangesAndClone()
    {
        var settings = new AppSettings(); var first = Guid.NewGuid().ToString("D"); var second = Guid.NewGuid().ToString("D");
        var controller = new SisrControllerProfile(SisrControllerType.DualSenseEdge, false, true, true);
        settings.SetControllerProfile(first, controller); settings.SetControllerProfile(second, new SisrControllerProfile());
        settings.SetSteamInputMode(first, SteamInputMode.Disabled); settings.SetWatchProcess(first, "Game.exe");
        settings.SetSteamInputMode(first, SteamInputMode.Automatic); settings.SetWatchProcess(first, "");
        Assert.Equal(controller, settings.GetProfile(first).Controller); Assert.NotEqual(controller, settings.GetProfile(second).Controller);
        var clone = settings.Clone(); clone.SetControllerProfile(first, null);
        Assert.Equal(controller, settings.GetProfile(first).Controller); Assert.Null(clone.GetProfile(first).Controller);
        settings.SetWatchProcess(first, "Keep.exe"); settings.SetControllerProfile(first, null);
        Assert.Equal("Keep.exe", settings.GetProfile(first).WatchProcess);
        Assert.Throws<ArgumentException>(() => settings.SetControllerProfile(first, new SisrControllerProfile((SisrControllerType)999)));
    }

    [Fact]
    public void JsonRoundtripAndOldSchemaInheritancePreserveNestedExtensions()
    {
        var settings = new AppSettings(); string key = Guid.NewGuid().ToString("D");
        settings.SetControllerProfile(key, new SisrControllerProfile(SisrControllerType.DualShock4, false, false, true));
        var protector = new NoSecret(); var json = JsonNode.Parse(JsonSettingsCodec.Encode(settings, null, protector))!;
        json["gameProfiles"]![key]!["controller"]!["futureController"] = "retain";
        var decoded = JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(json.ToJsonString()), protector);
        Assert.Equal(settings.GetProfile(key), decoded.Settings.GetProfile(key));
        decoded.Settings.SetControllerProfile(key, new SisrControllerProfile(SisrControllerType.Switch2Pro, true, false, false));
        var saved = JsonNode.Parse(JsonSettingsCodec.Encode(decoded.Settings, decoded.Data, protector))!;
        Assert.Equal("retain", (string?)saved["gameProfiles"]![key]!["controller"]!["futureController"]);
        saved["gameProfiles"]![key]!.AsObject().Remove("controller");
        Assert.Null(JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(saved.ToJsonString()), protector).Settings.GetProfile(key).Controller);
    }

    [Theory]
    [InlineData("{\"controllerType\":\"bad\",\"gyroPassthrough\":true,\"touchpadPassthrough\":true,\"backButtonPassthrough\":false}")]
    [InlineData("{\"controllerType\":42,\"gyroPassthrough\":true,\"touchpadPassthrough\":true,\"backButtonPassthrough\":false}")]
    [InlineData("{\"controllerType\":\"xbox360\",\"gyroPassthrough\":\"true\",\"touchpadPassthrough\":true,\"backButtonPassthrough\":false}")]
    [InlineData("{\"controllerType\":\"xbox360\"}")]
    [InlineData("[]")]
    public void InvalidTypedProfilesFailLoadingInsteadOfFallingBack(string controller)
    {
        var settings = new AppSettings(); settings.SetWatchProcess("game", "Game.exe");
        var json = JsonNode.Parse(JsonSettingsCodec.Encode(settings, null, new NoSecret()))!;
        json["gameProfiles"]!["game"]!["controller"] = JsonNode.Parse(controller);
        Assert.Throws<InvalidDataException>(() => JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(json.ToJsonString()), new NoSecret()));
    }

    [Fact]
    public void RediscoveryAndLibraryEditsRetainControllerSettingsAndRollBackFailedChanges()
    {
        var settings = new AppSettings(); var game = new Game(Guid.NewGuid(), "Game", "win32", @"C:\Game.exe", GameLaunchKind.Executable, @"C:\Game.exe", [], "", null);
        var controller = new SisrControllerProfile(SisrControllerType.DualSense, false, false, true);
        game = GameCatalog.Register(settings, game, new GameProfile(SteamInputMode.Enabled, "Game.exe", controller));
        game = GameCatalog.Register(settings, game.WithId(Guid.NewGuid()), new GameProfile(SteamInputMode.Disabled, ""));
        Assert.Equal(controller, settings.GetProfile(game.ProfileKey).Controller);
        var old = settings.GetProfile(game.ProfileKey); var replacement = GameLibraryEditor.Definition(game, "Renamed", game.Target, [], "", null);
        Assert.False(GameLibraryEditor.TryCommit(settings, game, old, replacement, old with { Controller = null }, () => false));
        Assert.Equal(controller, settings.GetProfile(game.ProfileKey).Controller);
        Assert.True(GameLibraryEditor.TryCommit(settings, game, old, replacement, old with { Controller = null }, () => true));
        Assert.Null(settings.GetProfile(game.ProfileKey).Controller); Assert.Equal(SteamInputMode.Disabled, settings.GetProfile(game.ProfileKey).SteamInput);
    }

    [Theory]
    [InlineData("--default-controller-type=dualsense")]
    [InlineData("--gp=false")]
    [InlineData("--touchpad-passthrough=false")]
    [InlineData("--no-back-button-passthrough")]
    [InlineData("-ct xbox360")]
    public void StructuredProfilesRejectConflictingAdvancedFlagsButInheritanceAllowsThem(string arguments)
    {
        Assert.Throws<ArgumentException>(() => SisrManagedStartup.Arguments(arguments, new SisrControllerProfile()));
        Assert.NotEmpty(SisrManagedStartup.Arguments(arguments));
    }

    [Fact]
    public void GeneratedConfigEnvironmentAndEffectiveVerificationAgreeOnAllControllerOptions()
    {
        string root = Path.Combine(Path.GetTempPath(), "sBridge-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new SisrControllerProfile(SisrControllerType.DualSenseEdge, false, false, true);
            using var session = SisrManagedStartup.Create(root, options);
            using var config = JsonDocument.Parse(File.ReadAllBytes(session.ConfigPath));
            Assert.Equal("dualsenseedge", config.RootElement.GetProperty("default_controller_type").GetString());
            Assert.False(config.RootElement.GetProperty("gyro_passthrough").GetBoolean());
            Assert.False(config.RootElement.GetProperty("touchpad_passthrough").GetBoolean());
            Assert.True(config.RootElement.GetProperty("back_button_passthrough").GetBoolean());
            var info = session.StartInfo(Path.GetFullPath("SISR.exe"), SisrManagedStartup.Arguments("--no-steam", options));
            Assert.Equal("false", info.Environment["SISR_GYRO_PASSTHROUGH"]); Assert.Equal("true", info.Environment["SISR_BACK_BUTTON_PASSTHROUGH"]);
            var status = new SisrStatusSnapshot("v0.6.1", true, false, true, false, false, false, false, 0, "dualsenseedge", false,
                GyroPassthrough: false, TouchpadPassthrough: false, BackButtonPassthrough: true);
            SisrManagedStartup.VerifyEffectiveProfile(options, status);
            Assert.Throws<InvalidOperationException>(() => SisrManagedStartup.VerifyEffectiveProfile(options, status with { GyroPassthrough = true }));
            Assert.Throws<InvalidOperationException>(() => SisrManagedStartup.VerifyEffectiveProfile(options, status with { BackButtonPassthrough = null }));
            SisrManagedStartup.VerifyEffectiveProfile(null, status with { ControllerType = null });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(0, "xbox360")]
    [InlineData(1, "dualshock4")]
    [InlineData(2, "dualsense")]
    [InlineData(3, "dualsenseedge")]
    [InlineData(4, "ns2pro")]
    public void ControllerNamesMatchPinnedSisrEnum(int type, string expected) => Assert.Equal(expected, new SisrControllerProfile((SisrControllerType)type).ApiType);

    private sealed class NoSecret : ISecretProtector
    { public string Protect(string value) => throw new InvalidOperationException(); public string Unprotect(string value) => throw new InvalidOperationException(); }
}
