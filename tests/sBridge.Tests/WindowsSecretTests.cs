using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using SBridge.Configuration;
using SBridge.Core;
using Xunit;

namespace SBridge.Tests;

[SupportedOSPlatform("windows")]
[Collection("Windows integration")]
public class WindowsSecretTests
{
    [WindowsFact]
    public void RealCurrentUserDpapiRoundTripAndTamperRejection()
    {
        var protector = new WindowsSecretProtector();
        const string secret = "synthetic-SGDB-key-遊戲";
        string encrypted = protector.Protect(secret);
        Assert.StartsWith("dpapi-current-user-v1:", encrypted);
        Assert.DoesNotContain(secret, encrypted);
        Assert.Equal(secret, protector.Unprotect(encrypted));
        int separator = encrypted.IndexOf(':');
        byte[] bytes = Convert.FromBase64String(encrypted[(separator + 1)..]); bytes[^1] ^= 0x55;
        Assert.Throws<CryptographicException>(() => protector.Unprotect(encrypted[..(separator + 1)] + Convert.ToBase64String(bytes)));
    }

    [WindowsFact]
    public void JsonAndItsAtomicBackupDoNotContainKnownPlaintextKey()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sBridge-DPAPI-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "config.json");
            var store = new JsonSettingsStore(new WindowsSecretProtector());
            var document = store.LoadOrMigrate(path, Path.Combine(directory, "legacy.cfg"), new AppSettings { SteamGridDbApiKey = "synthetic-secret-disk-check" });
            document.Settings.SisrEnabled = false; Assert.True(store.Save(document).Succeeded);
            foreach (string file in new[] { path, path + ".bak" })
                Assert.DoesNotContain("synthetic-secret-disk-check", Encoding.UTF8.GetString(File.ReadAllBytes(file)));
            Assert.Equal("synthetic-secret-disk-check", store.LoadOrMigrate(path, Path.Combine(directory, "legacy.cfg"), new AppSettings()).Settings.SteamGridDbApiKey);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [WindowsFact]
    public void DataPathsDefaultToWindowsLocalAppDataAndTestOverrideMustBeAbsolute()
    {
        string? previous = Environment.GetEnvironmentVariable("SBRIDGE_TEST_DATA_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("SBRIDGE_TEST_DATA_DIRECTORY", null);
            var paths = AppPaths.ForWindows();
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sBridge", "config.json"), paths.ConfigFile);
            Assert.Equal(Path.Combine(paths.DataDirectory, "logs", "sBridge.log"), paths.LogFile);
            Environment.SetEnvironmentVariable("SBRIDGE_TEST_DATA_DIRECTORY", "relative-data");
            Assert.Throws<InvalidOperationException>(() => AppPaths.ForWindows());
        }
        finally { Environment.SetEnvironmentVariable("SBRIDGE_TEST_DATA_DIRECTORY", previous); }
    }

    [WindowsFact]
    public void ReadOnlyLegacyFileCanMigrateWithoutBeingRewritten()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sBridge-readonly-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string legacy = Path.Combine(directory, "sBridge.cfg");
        byte[] original = Encoding.UTF8.GetBytes("SisrEnabled=false\r\nSgdbApiKey=synthetic-readonly-key\r\n");
        try
        {
            File.WriteAllBytes(legacy, original); File.SetAttributes(legacy, FileAttributes.ReadOnly);
            var document = new JsonSettingsStore(new WindowsSecretProtector()).LoadOrMigrate(
                Path.Combine(directory, "data", "config.json"), legacy, new AppSettings());
            Assert.False(document.Settings.SisrEnabled);
            Assert.Equal(original, File.ReadAllBytes(legacy));
            Assert.False(File.Exists(legacy + ".bak"));
        }
        finally { File.SetAttributes(legacy, FileAttributes.Normal); Directory.Delete(directory, recursive: true); }
    }
}
