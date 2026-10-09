using System.Security.Cryptography;
using System.Text;
using SBridge.Configuration;
using SBridge.Core;
using Xunit;

namespace SBridge.Tests;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sBridge-json-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string jsonPath;
    private readonly string legacyPath;
    private static readonly byte[] Legacy = Encoding.UTF8.GetBytes("# original retained\r\nSisrEnabled=false\r\n" +
        "SisrPath=C:\\SISR\\SISR.exe\r\nSisrArguments=--option=value\r\nSgdbApiKey=synthetic-secret\r\n" +
        "Sisr_Game_123!App=true\r\nWatch_Game_123!App=Actual.exe\r\nUnknown=Keep\r\n");

    public JsonSettingsStoreTests()
    {
        Directory.CreateDirectory(directory);
        jsonPath = Path.Combine(directory, "data", "config.json");
        legacyPath = Path.Combine(directory, "sBridge.cfg");
    }

    [Fact]
    public void MigrationIsProtectedNonDestructiveAndExistingJsonBecomesAuthoritative()
    {
        File.WriteAllBytes(legacyPath, Legacy);
        var store = new JsonSettingsStore(new TestProtector());
        var migrated = store.LoadOrMigrate(jsonPath, legacyPath, new AppSettings());
        Assert.False(migrated.Settings.SisrEnabled);
        Assert.True(migrated.Settings.IsSisrEnabledFor("game_123!app"));
        Assert.Equal("Actual.exe", migrated.Settings.GetProfile("Game_123!App").WatchProcess);
        Assert.Equal("synthetic-secret", migrated.Settings.SteamGridDbApiKey);
        Assert.Equal(Legacy, File.ReadAllBytes(legacyPath));
        Assert.False(File.Exists(legacyPath + ".bak"));
        Assert.DoesNotContain("synthetic-secret", File.ReadAllText(jsonPath));
        Assert.NotNull(migrated.Data?.LegacyMigration);
        byte[] first = File.ReadAllBytes(jsonPath);
        File.WriteAllText(legacyPath, "SisrEnabled=invalid");
        var reloaded = store.LoadOrMigrate(jsonPath, legacyPath, new AppSettings());
        Assert.False(reloaded.Settings.SisrEnabled);
        Assert.Equal(first, File.ReadAllBytes(jsonPath));
    }

    [Fact]
    public void ExistingLegacyBackupIsAlsoRetainedUntouched()
    {
        File.WriteAllBytes(legacyPath, Legacy);
        byte[] backup = Encoding.UTF8.GetBytes("# older backup with synthetic plaintext\n");
        File.WriteAllBytes(legacyPath + ".bak", backup);
        new JsonSettingsStore(new TestProtector()).LoadOrMigrate(jsonPath, legacyPath, new AppSettings());
        Assert.Equal(backup, File.ReadAllBytes(legacyPath + ".bak"));
    }

    [Fact]
    public void FreshUserDoesNotNeedLegacyFileOrWritableExecutableDirectory()
    {
        var store = new JsonSettingsStore(new TestProtector());
        var document = store.LoadOrMigrate(jsonPath, legacyPath, new AppSettings { SisrPath = "detected" });
        Assert.Equal("detected", document.Settings.SisrPath);
        Assert.Null(document.Data?.LegacyMigration);
        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists(jsonPath));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("partial-write")]
    [InlineData("validation")]
    [InlineData("protection")]
    [InlineData("decryption")]
    public void InterruptedMigrationLeavesLegacyIntactAndCanRetry(string stage)
    {
        File.WriteAllBytes(legacyPath, Legacy);
        var files = new Hooks();
        switch (stage)
        {
            case "write": files.BeforeWrite = _ => throw new IOException("Write failed"); break;
            case "partial-write": files.BeforeWrite = path => { File.WriteAllBytes(path, new byte[] { 0 }); throw new IOException("Partial write"); }; break;
            case "validation": files.TransformRead = (path, bytes) => path.EndsWith(".tmp") ? Encoding.UTF8.GetBytes("[]") : bytes; break;
        }
        var protector = new TestProtector { FailProtect = stage == "protection", FailUnprotect = stage == "decryption" };
        Assert.Throws<IOException>(() => new JsonSettingsStore(protector, files).LoadOrMigrate(jsonPath, legacyPath, new AppSettings()));
        Assert.Equal(Legacy, File.ReadAllBytes(legacyPath));
        Assert.False(File.Exists(jsonPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(jsonPath)!, "*.tmp"));
        var retry = new JsonSettingsStore(new TestProtector()).LoadOrMigrate(jsonPath, legacyPath, new AppSettings());
        Assert.Equal("synthetic-secret", retry.Settings.SteamGridDbApiKey);
    }

    [Fact]
    public void MalformedLegacyOrDeniedReadDoesNotWriteDefaults()
    {
        byte[] malformed = Encoding.UTF8.GetBytes("SisrEnabled=invalid"); File.WriteAllBytes(legacyPath, malformed);
        Assert.Throws<InvalidDataException>(() => new JsonSettingsStore(new TestProtector()).LoadOrMigrate(jsonPath, legacyPath, new AppSettings()));
        Assert.Equal(malformed, File.ReadAllBytes(legacyPath)); Assert.False(File.Exists(jsonPath));
        var files = new Hooks { BeforeRead = path => { if (path == legacyPath) throw new UnauthorizedAccessException("Denied legacy"); } };
        Assert.Throws<UnauthorizedAccessException>(() => new JsonSettingsStore(new TestProtector(), files).LoadOrMigrate(jsonPath, legacyPath, new AppSettings()));
        Assert.False(File.Exists(jsonPath));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":99}")]
    [InlineData("broken json")]
    public void ExistingUnsupportedOrMalformedJsonNeverFallsBackToLegacy(string content)
    {
        File.WriteAllBytes(legacyPath, Legacy); Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        File.WriteAllText(jsonPath, content);
        Assert.Throws<InvalidDataException>(() => new JsonSettingsStore(new TestProtector()).LoadOrMigrate(jsonPath, legacyPath, new AppSettings()));
        Assert.Equal(content, File.ReadAllText(jsonPath)); Assert.Equal(Legacy, File.ReadAllBytes(legacyPath));
    }

    [Fact]
    public void ExistingEncryptedCredentialFailureNeverReimportsPlaintextSource()
    {
        File.WriteAllBytes(legacyPath, Legacy);
        new JsonSettingsStore(new TestProtector()).LoadOrMigrate(jsonPath, legacyPath, new AppSettings());
        byte[] existing = File.ReadAllBytes(jsonPath);
        Assert.Throws<CryptographicException>(() => new JsonSettingsStore(new TestProtector { FailUnprotect = true })
            .LoadOrMigrate(jsonPath, legacyPath, new AppSettings()));
        Assert.Equal(existing, File.ReadAllBytes(jsonPath));
    }

    [Fact]
    public void DeniedExistingJsonDoesNotFallBackToReadableLegacy()
    {
        File.WriteAllBytes(legacyPath, Legacy);
        new JsonSettingsStore(new TestProtector()).LoadOrMigrate(jsonPath, legacyPath, new AppSettings());
        byte[] existing = File.ReadAllBytes(jsonPath);
        var files = new Hooks { BeforeRead = path => { if (path == jsonPath) throw new UnauthorizedAccessException("Denied JSON"); } };
        Assert.Throws<UnauthorizedAccessException>(() => new JsonSettingsStore(new TestProtector(), files).LoadOrMigrate(jsonPath, legacyPath, new AppSettings()));
        Assert.Equal(existing, File.ReadAllBytes(jsonPath)); Assert.Equal(Legacy, File.ReadAllBytes(legacyPath));
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("replace")]
    [InlineData("unsupported")]
    public void ReplacementFailurePreservesExistingJsonAndProtectedBackup(string stage)
    {
        File.WriteAllBytes(legacyPath, Legacy);
        var files = new Hooks(); var store = new JsonSettingsStore(new TestProtector(), files);
        var document = store.LoadOrMigrate(jsonPath, legacyPath, new AppSettings());
        byte[] existing = File.ReadAllBytes(jsonPath); document.Settings.SisrEnabled = true;
        if (stage == "backup") files.BeforeBackup = _ => throw new UnauthorizedAccessException("Backup failed");
        else files.BeforeReplace = _ => { if (stage == "unsupported") throw new PlatformNotSupportedException("Unsupported"); throw new IOException("Replace failed"); };
        Assert.False(store.Save(document).Succeeded); Assert.Equal(existing, File.ReadAllBytes(jsonPath));
        Assert.Equal(Legacy, File.ReadAllBytes(legacyPath));
        if (File.Exists(jsonPath + ".bak")) Assert.DoesNotContain("synthetic-secret", File.ReadAllText(jsonPath + ".bak"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(jsonPath)!, "*.tmp"));
    }

    [Fact]
    public void StaleDeletedAndCreationRaceJsonIsNotOverwritten()
    {
        var store = new JsonSettingsStore(new TestProtector());
        var first = store.LoadOrMigrate(jsonPath, legacyPath, new AppSettings());
        var stale = store.LoadOrMigrate(jsonPath, legacyPath, new AppSettings());
        first.Settings.SetWatchProcess(@"C:\Games\Foo=Bar.exe", "actual.exe"); Assert.True(store.Save(first).Succeeded);
        byte[] current = File.ReadAllBytes(jsonPath); Assert.False(store.Save(stale).Succeeded); Assert.Equal(current, File.ReadAllBytes(jsonPath));
        File.Delete(jsonPath); Assert.False(store.Save(first).Succeeded); Assert.False(File.Exists(jsonPath));
        var files = new Hooks { BeforeMove = path => File.WriteAllText(path, "{\"schemaVersion\":99}") };
        Assert.Throws<InvalidDataException>(() => new JsonSettingsStore(new TestProtector(), files).LoadOrMigrate(jsonPath, legacyPath, new AppSettings()));
        Assert.Equal("{\"schemaVersion\":99}", File.ReadAllText(jsonPath));
    }

    [Fact]
    public void ConcurrentInitialCreatorLoadsValidatedWinningJsonWithoutOverwritingIt()
    {
        byte[] winner = JsonSettingsCodec.Encode(new AppSettings { SisrPath = "other-copy-winner", SisrEnabled = false }, null, new TestProtector());
        var files = new Hooks { BeforeMove = path => File.WriteAllBytes(path, winner) };
        var document = new JsonSettingsStore(new TestProtector(), files).LoadOrMigrate(jsonPath, legacyPath, new AppSettings());
        Assert.Equal("other-copy-winner", document.Settings.SisrPath);
        Assert.False(document.Settings.SisrEnabled);
        Assert.Equal(winner, File.ReadAllBytes(jsonPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(jsonPath)!, "*.tmp"));
    }

    [Fact]
    public void ChangedMigrationSourceBeforeCommitIsDetected()
    {
        File.WriteAllBytes(legacyPath, Legacy);
        var files = new Hooks { BeforeWrite = _ => File.WriteAllText(legacyPath, "SisrEnabled=true") };
        Assert.Throws<IOException>(() => new JsonSettingsStore(new TestProtector(), files).LoadOrMigrate(jsonPath, legacyPath, new AppSettings()));
        Assert.False(File.Exists(jsonPath)); Assert.Equal("SisrEnabled=true", File.ReadAllText(legacyPath));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class Hooks : IConfigurationFiles
    {
        private readonly ConfigurationFileOperations inner = new();
        public Action<string>? BeforeRead { get; set; }
        public Action<string>? BeforeWrite { get; set; }
        public Action<string>? BeforeBackup { get; set; }
        public Action<string>? BeforeReplace { get; set; }
        public Action<string>? BeforeMove { get; set; }
        public Func<string, byte[], byte[]>? TransformRead { get; set; }
        public byte[] Read(string path) { BeforeRead?.Invoke(path); byte[] bytes = inner.Read(path); return TransformRead?.Invoke(path, bytes) ?? bytes; }
        public void WriteNew(string path, byte[] bytes) { BeforeWrite?.Invoke(path); inner.WriteNew(path, bytes); }
        public void Backup(string path, string backup) { BeforeBackup?.Invoke(path); inner.Backup(path, backup); }
        public void Replace(string temporary, string path) { BeforeReplace?.Invoke(path); inner.Replace(temporary, path); }
        public void MoveNew(string temporary, string path) { BeforeMove?.Invoke(path); inner.MoveNew(temporary, path); }
        public void Delete(string path) => inner.Delete(path);
    }
}
