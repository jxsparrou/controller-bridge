using System.Text;
using SBridge.Configuration;
using SBridge.Core;
using Xunit;

namespace SBridge.Tests;

public class LegacyConfigurationTests
{
    [Fact]
    public void ProfilesDistinguishAutomaticDisabledAndWatchOnlyWithoutCreatingOnRead()
    {
        var settings = new AppSettings();
        Assert.Equal(SteamInputMode.Automatic, settings.GetProfile("Game_123!App").SteamInput);
        Assert.Empty(settings.GameProfiles);
        settings.SetWatchProcess("Game_123!App", "RealGame.exe");
        settings.SetSteamInputMode("game_123!app", SteamInputMode.Disabled);
        Assert.False(settings.IsSisrEnabledFor("GAME_123!APP"));
        settings.SetSteamInputMode("GAME_123!APP", SteamInputMode.Automatic);
        Assert.True(settings.IsSisrEnabledFor("Game_123!App"));
        Assert.Equal("RealGame.exe", settings.GetProfile("Game_123!App").WatchProcess);
        settings.SetWatchProcess("Game_123!App", "");
        Assert.Empty(settings.GameProfiles);
    }

    [Fact]
    public void CloneIsIndependentAndKeepsCaseInsensitiveKeys()
    {
        var original = new AppSettings { SisrPath = @"C:\SISR\SISR.exe", SteamGridDbApiKey = "synthetic-key" };
        original.SetSteamInputMode("Game", SteamInputMode.Disabled);
        var copy = original.Clone();
        copy.SisrPath = "Other";
        copy.SetSteamInputMode("GAME", SteamInputMode.Enabled);
        Assert.Equal(@"C:\SISR\SISR.exe", original.SisrPath);
        Assert.False(original.IsSisrEnabledFor("game"));
        Assert.True(copy.IsSisrEnabledFor("game"));
    }

    [Fact]
    public void LegacyReaderPreservesDefaultsCaseWhitespaceEqualsAndLastValidOccurrence()
    {
        var defaults = new AppSettings { SisrPath = "detected-default", SisrEnabled = true };
        var settings = LegacyConfigurationCodec.Parse(new[]
        {
            " # comment", "; other comment", "",
            "sisrPATH= C:\\Folder With Spaces\\SISR.exe ", "SisrArguments=--config=\"C:\\Config Folder\\profile.json\"",
            "sisrenabled = TRUE", "SISRENABLED=false", "LogEnabled=false", "SgdbApiKey= synthetic=key ",
            "Sisr_C:\\Game Folder\\遊戲.exe=true", "Watch_c:\\game folder\\遊戲.exe=Real Game.exe",
            "Sisr_Example_123!App=false", "FutureSetting=value"
        }, defaults);
        Assert.Equal(@"C:\Folder With Spaces\SISR.exe", settings.SisrPath);
        Assert.Equal("--config=\"C:\\Config Folder\\profile.json\"", settings.SisrArguments);
        Assert.False(settings.SisrEnabled);
        Assert.False(settings.LogEnabled);
        Assert.Equal("synthetic=key", settings.SteamGridDbApiKey);
        Assert.True(settings.IsSisrEnabledFor(@"c:\game folder\遊戲.exe"));
        Assert.Equal("Real Game.exe", settings.GetProfile(@"C:\Game Folder\遊戲.exe").WatchProcess);
        Assert.False(settings.IsSisrEnabledFor("example_123!app"));
        Assert.Equal("detected-default", defaults.SisrPath);
        Assert.Empty(defaults.GameProfiles);
    }

    [Theory]
    [InlineData("SisrEnabled=invalid")]
    [InlineData("LogEnabled=invalid")]
    [InlineData("Sisr_Game=invalid")]
    [InlineData("Sisr_=true")]
    [InlineData("Watch_=game.exe")]
    [InlineData("SisrEnabled false")]
    [InlineData("=orphan")]
    public void MalformedKnownValuesOrLinesAreRejectedWithoutExposingValues(string line)
    {
        var error = Assert.Throws<InvalidDataException>(() => LegacyConfigurationCodec.Parse(new[] { line }, new AppSettings()));
        Assert.DoesNotContain("=invalid", error.Message);
    }

    [Fact]
    public void AutosaveRetainsCommentsUnknownKeysAndAvoidsDuplicateKnownEntries()
    {
        string[] lines = { "# custom comment", " FutureSetting = Keep=Value ", "sisrenabled=true", "SISRENABLED=false", "", "; tail" };
        var settings = LegacyConfigurationCodec.Parse(lines, new AppSettings());
        settings.SisrEnabled = true;
        byte[] saved = LegacyConfigurationCodec.Serialize(settings, lines);
        string[] output = LegacyConfigurationCodec.Decode(saved);
        Assert.Contains("# custom comment", output);
        Assert.Contains(" FutureSetting = Keep=Value ", output);
        Assert.Contains("; tail", output);
        Assert.Single(output, line => line.StartsWith("SisrEnabled=", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(saved, LegacyConfigurationCodec.Serialize(LegacyConfigurationCodec.Parse(output, new AppSettings()), output));
    }

    [Fact]
    public void ReturningToAutomaticRemovesOnlySisrOverrideAndEmptyWatchRemovesOnlyWatch()
    {
        string[] lines = { "Sisr_Game=false", "Watch_Game=real.exe" };
        var settings = LegacyConfigurationCodec.Parse(lines, new AppSettings());
        settings.SetSteamInputMode("GAME", SteamInputMode.Automatic);
        string[] output = LegacyConfigurationCodec.Decode(LegacyConfigurationCodec.Serialize(settings, lines));
        Assert.DoesNotContain(output, line => line.StartsWith("Sisr_Game=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Watch_Game=real.exe", output);
        settings.SetSteamInputMode("game", SteamInputMode.Disabled);
        settings.SetWatchProcess("Game", "");
        output = LegacyConfigurationCodec.Decode(LegacyConfigurationCodec.Serialize(settings, output));
        Assert.DoesNotContain(output, line => line.StartsWith("Watch_Game=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(output, line => line.Equals("Sisr_Game=false", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Utf8Utf16AndUtf32BomFilesDecodeWithoutLosingUnicode()
    {
        foreach (var encoding in new Encoding[] { new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true),
            new UnicodeEncoding(true, true, true), new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) })
        {
            byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes("Watch_Game=遊戲.exe\r\n")).ToArray();
            var settings = LegacyConfigurationCodec.Parse(LegacyConfigurationCodec.Decode(bytes), new AppSettings());
            Assert.Equal("遊戲.exe", settings.GetProfile("Game").WatchProcess);
        }
    }

    [Theory]
    [InlineData("FF")]
    [InlineData("FFFE00")]
    [InlineData("EFBBBF00")]
    public void InvalidEncodingAndNulAreRejected(string hex)
    {
        Assert.Throws<InvalidDataException>(() => LegacyConfigurationCodec.Decode(Convert.FromHexString(hex)));
    }

    [Fact]
    public void InjectionAndUnrepresentableLegacyKeysCannotBeSerialized()
    {
        var settings = new AppSettings { SisrArguments = "--option\nSisrEnabled=false" };
        Assert.Throws<InvalidDataException>(() => LegacyConfigurationCodec.Serialize(settings, Array.Empty<string>()));
        settings.SetSteamInputMode(@"C:\Games\Foo=Bar.exe", SteamInputMode.Disabled);
        settings.SisrArguments = "";
        Assert.Throws<InvalidDataException>(() => LegacyConfigurationCodec.Serialize(settings, Array.Empty<string>()));
        settings.GameProfiles.Clear();
        settings.GameProfiles["bad=key"] = new GameProfile(SteamInputMode.Enabled);
        Assert.Throws<InvalidDataException>(() => LegacyConfigurationCodec.Serialize(settings, Array.Empty<string>()));
    }

    [Fact]
    public void FileSizeBoundIsAppliedBeforeDecode()
    {
        Assert.Throws<InvalidDataException>(() => LegacyConfigurationCodec.Decode(new byte[LegacyConfigurationCodec.MaxFileBytes + 1]));
    }
}
