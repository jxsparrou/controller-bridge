using System.Text;
using SBridge.Configuration;
using SBridge.Core;
using Xunit;

namespace SBridge.Tests;

public sealed class LegacyConfigurationStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sBridge-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string path;
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("# retained comment\r\nSisrEnabled=false\r\nWatch_Game=Real.exe\r\nFuture=Keep\r\n");

    public LegacyConfigurationStoreTests() { Directory.CreateDirectory(directory); path = Path.Combine(directory, "sBridge.cfg"); }

    [Fact]
    public void MissingOwnConfigCanBeCreatedThenUpdatedWithExactBackup()
    {
        var store = new LegacyConfigurationStore();
        var document = store.Load(path, new AppSettings { SisrPath = "detected" });
        Assert.True(document.IsNew);
        Assert.False(File.Exists(path));
        Assert.True(store.Save(document).Succeeded);
        Assert.False(document.IsNew);
        byte[] first = File.ReadAllBytes(path);
        document.Settings.SetWatchProcess("Game", "Actual.exe");
        Assert.True(store.Save(document).Succeeded);
        Assert.Equal(first, File.ReadAllBytes(path + ".bak"));
        Assert.Equal("Actual.exe", store.Load(path, new AppSettings()).Settings.GetProfile("game").WatchProcess);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void ExistingConfigIsValidatedAndCommentsUnknownValuesAreRetained()
    {
        File.WriteAllBytes(path, Original);
        var store = new LegacyConfigurationStore();
        var document = store.Load(path, new AppSettings());
        document.Settings.SetSteamInputMode("Game", SteamInputMode.Enabled);
        Assert.True(store.Save(document).Succeeded);
        Assert.Equal(Original, File.ReadAllBytes(path + ".bak"));
        string[] lines = File.ReadAllLines(path);
        Assert.Contains("# retained comment", lines);
        Assert.Contains("Future=Keep", lines);
        Assert.True(store.Load(path, new AppSettings()).Settings.IsSisrEnabledFor("GAME"));
    }

    [Fact]
    public void MalformedOrDeniedExistingFileDoesNotBecomeDefaultDocument()
    {
        byte[] malformed = Encoding.UTF8.GetBytes("SisrEnabled=synthetic-secret-invalid\n");
        File.WriteAllBytes(path, malformed);
        Assert.Throws<InvalidDataException>(() => new LegacyConfigurationStore().Load(path, new AppSettings()));
        Assert.Equal(malformed, File.ReadAllBytes(path));
        var files = new HookedFiles { BeforeRead = _ => throw new UnauthorizedAccessException("Denied") };
        Assert.Throws<UnauthorizedAccessException>(() => new LegacyConfigurationStore(files).Load(path, new AppSettings()));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("partial-write")]
    [InlineData("validation")]
    [InlineData("different-valid-temp")]
    [InlineData("backup")]
    [InlineData("replace")]
    [InlineData("unsupported")]
    public void SaveFailureLeavesExistingSettingsUntouched(string stage)
    {
        File.WriteAllBytes(path, Original);
        var files = new HookedFiles();
        var store = new LegacyConfigurationStore(files);
        var document = store.Load(path, new AppSettings());
        document.Settings.SisrEnabled = true;
        switch (stage)
        {
            case "write": files.BeforeWrite = _ => throw new IOException("Failed write"); break;
            case "partial-write": files.BeforeWrite = file => { File.WriteAllBytes(file, new byte[] { 0 }); throw new IOException("Partial write"); }; break;
            case "validation": files.TransformRead = (file, bytes) => file == path ? bytes : Encoding.UTF8.GetBytes("SisrEnabled=invalid\n"); break;
            case "different-valid-temp": files.TransformRead = (file, bytes) => file == path ? bytes : Original; break;
            case "backup": files.BeforeBackup = _ => throw new UnauthorizedAccessException("Failed backup"); break;
            case "replace": files.BeforeReplace = _ => throw new IOException("Failed replacement"); break;
            case "unsupported": files.BeforeReplace = _ => throw new PlatformNotSupportedException("No atomic replacement"); break;
        }
        var result = store.Save(document);
        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Equal(Original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void StaleChangedDeletedAndNewlyAppearedFilesAreNotOverwritten()
    {
        var store = new LegacyConfigurationStore();
        var missing = store.Load(path, new AppSettings());
        File.WriteAllBytes(path, Original);
        Assert.False(store.Save(missing).Succeeded);
        var first = store.Load(path, new AppSettings());
        var stale = store.Load(path, new AppSettings());
        first.Settings.SisrEnabled = true;
        Assert.True(store.Save(first).Succeeded);
        byte[] current = File.ReadAllBytes(path);
        Assert.False(store.Save(stale).Succeeded);
        Assert.Equal(current, File.ReadAllBytes(path));
        File.Delete(path);
        Assert.False(store.Save(first).Succeeded);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void InvalidChangedValueCannotReplaceOriginal()
    {
        File.WriteAllBytes(path, Original);
        var store = new LegacyConfigurationStore();
        var document = store.Load(path, new AppSettings());
        document.Settings.SteamGridDbApiKey = "synthetic-key\r\nLogEnabled=false";
        Assert.False(store.Save(document).Succeeded);
        Assert.Equal(Original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void FirstCreationRaceDoesNotOverwriteFileThatAppearsAtCommit()
    {
        var files = new HookedFiles { BeforeMoveNew = _ => File.WriteAllBytes(path, Original) };
        var store = new LegacyConfigurationStore(files);
        var document = store.Load(path, new AppSettings());
        Assert.False(store.Save(document).Succeeded);
        Assert.Equal(Original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
    private sealed class HookedFiles : IConfigurationFiles
    {
        private readonly ConfigurationFileOperations inner = new();
        public Action<string>? BeforeRead { get; set; }
        public Action<string>? BeforeWrite { get; set; }
        public Action<string>? BeforeBackup { get; set; }
        public Action<string>? BeforeReplace { get; set; }
        public Action<string>? BeforeMoveNew { get; set; }
        public Func<string, byte[], byte[]>? TransformRead { get; set; }
        public byte[] Read(string file) { BeforeRead?.Invoke(file); byte[] bytes = inner.Read(file); return TransformRead?.Invoke(file, bytes) ?? bytes; }
        public void WriteNew(string file, byte[] bytes) { BeforeWrite?.Invoke(file); inner.WriteNew(file, bytes); }
        public void Backup(string file, string backup) { BeforeBackup?.Invoke(file); inner.Backup(file, backup); }
        public void Replace(string temporary, string file) { BeforeReplace?.Invoke(file); inner.Replace(temporary, file); }
        public void MoveNew(string temporary, string file) { BeforeMoveNew?.Invoke(file); inner.MoveNew(temporary, file); }
        public void Delete(string file) => inner.Delete(file);
    }
}
