using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SBridge.Configuration;
using SBridge.Core;
using Xunit;

namespace SBridge.Tests;

public class JsonSettingsTests
{
    [Fact]
    public void SettingsRoundTripWithTypedModesEqualsPathsAndProtectedCredential()
    {
        var protector = new TestProtector();
        var settings = new AppSettings { SisrPath = @"C:\Tools\SISR.exe", SisrArguments = "--config=\"C:\\Profile Folder\\config.json\"",
            SisrEnabled = false, LogEnabled = false, SteamGridDbApiKey = "synthetic-test-secret" };
        settings.SetSteamInputMode(@"C:\Games\Foo=Bar.exe", SteamInputMode.Enabled);
        settings.SetWatchProcess(@"C:\Games\Foo=Bar.exe", "遊戲.exe");
        settings.SetWatchProcess("WatchOnly", "Actual.exe");
        byte[] bytes = JsonSettingsCodec.Encode(settings, null, protector);
        Assert.DoesNotContain(settings.SteamGridDbApiKey, Encoding.UTF8.GetString(bytes));
        var decoded = JsonSettingsCodec.Decode(bytes, protector).Settings;
        Assert.False(decoded.SisrEnabled);
        Assert.False(decoded.LogEnabled);
        Assert.Equal(settings.SisrArguments, decoded.SisrArguments);
        Assert.Equal(settings.SteamGridDbApiKey, decoded.SteamGridDbApiKey);
        Assert.True(decoded.IsSisrEnabledFor(@"c:\games\foo=bar.exe"));
        Assert.Equal("遊戲.exe", decoded.GetProfile(@"C:\Games\Foo=Bar.exe").WatchProcess);
        Assert.Equal(SteamInputMode.Automatic, decoded.GetProfile("watchonly").SteamInput);
    }

    [Theory]
    [InlineData("{}")] // Missing schema
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":99}")]
    [InlineData("{\"schemaVersion\":\"1\"}")]
    [InlineData("{\"schemaVersion\":1}")] // Missing required settings
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"SchemaVersion\":1}")]
    public void UnsupportedIncompleteOrDuplicateSchemaIsRejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(json), new TestProtector()));
    }

    [Fact]
    public void UnknownJsonRootAndProfileFieldsSurviveSave()
    {
        var protector = new TestProtector();
        var settings = new AppSettings(); settings.SetWatchProcess("Game", "Actual.exe");
        string json = Encoding.UTF8.GetString(JsonSettingsCodec.Encode(settings, null, protector));
        json = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"futureRoot\": {\"enabled\":true}")
            .Replace("\"watchProcess\": \"Actual.exe\"", "\"watchProcess\": \"Actual.exe\", \"futureProfile\": [1,2]");
        var decoded = JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(json), protector);
        decoded.Settings.SisrEnabled = false;
        using var output = JsonDocument.Parse(JsonSettingsCodec.Encode(decoded.Settings, decoded.Data, protector));
        Assert.True(output.RootElement.GetProperty("futureRoot").GetProperty("enabled").GetBoolean());
        Assert.Equal(2, output.RootElement.GetProperty("gameProfiles").GetProperty("Game").GetProperty("futureProfile").GetArrayLength());
    }

    [Fact]
    public void PlaintextCredentialAliasesAndNumericModesAreRejected()
    {
        var protector = new TestProtector();
        var settings = new AppSettings(); settings.SetSteamInputMode("Game", SteamInputMode.Disabled);
        string valid = Encoding.UTF8.GetString(JsonSettingsCodec.Encode(settings, null, protector));
        foreach (string alias in new[] { "steamGridDbApiKey", "SgdbApiKey" })
        {
            string modified = valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"" + alias + "\":\"synthetic-secret\"");
            Assert.Throws<InvalidDataException>(() => JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(modified), protector));
        }
        string numeric = valid.Replace("\"disabled\"", "2");
        Assert.Throws<InvalidDataException>(() => JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(numeric), protector));
    }

    [Fact]
    public void DecryptionFailureDoesNotClearCredentialOrSilentlyReturnDefaults()
    {
        var settings = new AppSettings { SteamGridDbApiKey = "synthetic-secret" };
        byte[] bytes = JsonSettingsCodec.Encode(settings, null, new TestProtector());
        Assert.Throws<CryptographicException>(() => JsonSettingsCodec.Decode(bytes, new TestProtector { FailUnprotect = true }));
    }

    [Fact]
    public void LargeFilesNullProfilesAndBadTargetsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => JsonSettingsCodec.Decode(new byte[JsonSettingsCodec.MaxFileBytes + 1], new TestProtector()));
        var settings = new AppSettings(); settings.SetWatchProcess("Game", "actual.exe");
        string valid = Encoding.UTF8.GetString(JsonSettingsCodec.Encode(settings, null, new TestProtector()));
        string nullWatch = valid.Replace("\"actual.exe\"", "null");
        Assert.Throws<InvalidDataException>(() => JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(nullWatch), new TestProtector()));
        string emptyKey = valid.Replace("\"Game\":", "\"\":");
        Assert.Throws<InvalidDataException>(() => JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(emptyKey), new TestProtector()));
    }

    [Fact]
    public void InvalidUnicodeIsNotSilentlyReplacedDuringJsonEncoding()
    {
        var settings = new AppSettings { SisrPath = new string((char)0xD800, 1) };
        Assert.Throws<InvalidDataException>(() => JsonSettingsCodec.Encode(settings, null, new TestProtector()));
    }

    [Fact]
    public void UnchangedKeyReusesCiphertextAndChangingOrClearingItIsExplicit()
    {
        var protector = new TestProtector();
        var settings = new AppSettings { SteamGridDbApiKey = "synthetic-first" };
        var original = JsonSettingsCodec.Decode(JsonSettingsCodec.Encode(settings, null, protector), protector);
        var unchanged = JsonSettingsCodec.Decode(JsonSettingsCodec.Encode(settings, original.Data, new TestProtector { FailProtect = true }), protector);
        Assert.Equal(original.Data.ProtectedSteamGridDbApiKey, unchanged.Data.ProtectedSteamGridDbApiKey);
        settings.SteamGridDbApiKey = "synthetic-second";
        Assert.Equal("synthetic-second", JsonSettingsCodec.Decode(JsonSettingsCodec.Encode(settings, original.Data, protector), protector).Settings.SteamGridDbApiKey);
        settings.SteamGridDbApiKey = "";
        Assert.Null(JsonSettingsCodec.Decode(JsonSettingsCodec.Encode(settings, original.Data, protector), protector).Data.ProtectedSteamGridDbApiKey);
    }
}

// Decision tests use a deterministic fake, not a claim of Windows protection.
internal sealed class TestProtector : ISecretProtector
{
    public bool FailProtect { get; init; }
    public bool FailUnprotect { get; init; }
    public string Protect(string value) => FailProtect ? throw new CryptographicException("Protection failed") :
        "test-protected:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    public string Unprotect(string value)
    {
        if (FailUnprotect || !value.StartsWith("test-protected:")) throw new CryptographicException("Decryption failed");
        return Encoding.UTF8.GetString(Convert.FromBase64String(value["test-protected:".Length..]));
    }
}
