using SBridge.Steam;
using Xunit;

namespace SBridge.Tests;

public sealed class SteamShortcutRepositoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sBridge-vdf-tests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] original;
    private readonly string path;

    public SteamShortcutRepositoryTests()
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "shortcuts.vdf");
        string hex = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "shortcuts-supported.hex"));
        original = Convert.FromHexString(string.Concat(hex.Where(character => !char.IsWhiteSpace(character))));
        File.WriteAllBytes(path, original);
    }

    [Fact]
    public void SuccessfulReplacementBacksUpExactOriginalAndPreservesUnrelatedData()
    {
        var repository = new SteamShortcutRepository(() => false);
        var document = repository.LoadExisting(path);
        Rename(document, "Renamed Game");
        var result = repository.Save(document);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Path.GetFullPath(path), result.Path);
        Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        var root = Program.ParseShortcuts(File.ReadAllBytes(path));
        Assert.Equal("Renamed Game", Field(root.Children[0], "AppName").StringValue);
        Assert.Equal("Keep me", Field(root.Children[0], "CustomField").StringValue);
        Assert.Equal(unchecked((int)0x94CFC93E), Field(root.Children[0], "appid").IntValue);
        Assert.Equal("遊戲 Café 🎮", Field(root.Children[1], "AppName").StringValue);
        AssertNoTemporaryFiles();

        byte[] firstSave = File.ReadAllBytes(path);
        Rename(document, "Second Rename");
        Assert.True(repository.Save(document).Succeeded);
        Assert.Equal(firstSave, File.ReadAllBytes(path + ".bak"));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("partial-write")]
    [InlineData("validation")]
    [InlineData("different-valid-temp")]
    [InlineData("backup")]
    [InlineData("replace")]
    [InlineData("read")]
    [InlineData("unsupported-replace")]
    public void TransactionFailuresLeaveOriginalUntouchedAndCleanTemporaryOutput(string stage)
    {
        var files = new HookedFiles();
        var repository = new SteamShortcutRepository(() => false, files);
        var document = repository.LoadExisting(path);
        Rename(document, "New Name");
        byte[] priorBackup = { 1, 2, 3, 4 };
        File.WriteAllBytes(path + ".bak", priorBackup);
        switch (stage)
        {
            case "write": files.BeforeWrite = _ => throw new IOException("Injected temp write failure"); break;
            case "partial-write": files.BeforeWrite = temporary =>
                {
                    File.WriteAllBytes(temporary, new byte[] { 0 });
                    throw new IOException("Injected partial temp write");
                }; break;
            case "validation": files.TransformRead = (file, bytes) => file == path ? bytes : new byte[] { 0 }; break;
            case "different-valid-temp": files.TransformRead = (file, bytes) => file == path ? bytes : original; break;
            case "backup": files.BeforeBackup = _ => throw new UnauthorizedAccessException("Injected backup failure"); break;
            case "replace": files.BeforeReplace = _ => throw new IOException("Injected replacement failure"); break;
            case "read": files.BeforeRead = _ => throw new UnauthorizedAccessException("Injected read failure"); break;
            case "unsupported-replace": files.BeforeReplace = _ => throw new PlatformNotSupportedException("No atomic replacement"); break;
        }

        var result = repository.Save(document);
        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(stage is "replace" or "unsupported-replace" ? original : priorBackup, File.ReadAllBytes(path + ".bak"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void SteamRunningBlocksWriteBeforeBackupOrTemporaryOutput()
    {
        var repository = new SteamShortcutRepository(() => true);
        var document = repository.LoadExisting(path);
        Rename(document, "Not Saved");
        var result = repository.Save(document);
        Assert.False(result.Succeeded);
        Assert.Contains("Steam is running", result.Error);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void SteamRestartAfterBackupBlocksCommit()
    {
        bool steamRunning = false;
        var files = new HookedFiles { AfterBackup = _ => steamRunning = true };
        var repository = new SteamShortcutRepository(() => steamRunning, files);
        var document = repository.LoadExisting(path);
        Rename(document, "Not Saved");
        Assert.False(repository.Save(document).Succeeded);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void StaleSnapshotCannotOverwriteAnotherSuccessfulEdit()
    {
        var repository = new SteamShortcutRepository(() => false);
        var first = repository.LoadExisting(path);
        var stale = repository.LoadExisting(path);
        Rename(first, "First Editor");
        Assert.True(repository.Save(first).Succeeded);
        byte[] committed = File.ReadAllBytes(path);
        Rename(stale, "Stale Editor");
        var result = repository.Save(stale);
        Assert.False(result.Succeeded);
        Assert.Contains("changed since", result.Error);
        Assert.Equal(committed, File.ReadAllBytes(path));
        Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void ExternalChangeAfterBackupIsNotOverwritten()
    {
        var externalRoot = Program.ParseShortcuts(original);
        Field(externalRoot.Children[0], "AppName").StringValue = "External Edit";
        byte[] external = Program.SerializeShortcuts(externalRoot);
        var files = new HookedFiles { AfterBackup = _ => File.WriteAllBytes(path, external) };
        var repository = new SteamShortcutRepository(() => false, files);
        var document = repository.LoadExisting(path);
        Rename(document, "Stale Edit");
        Assert.False(repository.Save(document).Succeeded);
        Assert.Equal(external, File.ReadAllBytes(path));
        Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void MalformedExternalChangeIsNotReplacedByPreviouslyValidDocument()
    {
        var repository = new SteamShortcutRepository(() => false);
        var document = repository.LoadExisting(path);
        byte[] malformed = Convert.FromHexString("0173686F727463757473000808");
        File.WriteAllBytes(path, malformed);
        Assert.False(repository.Save(document).Succeeded);
        Assert.Equal(malformed, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
        AssertNoTemporaryFiles();
    }

    [WindowsFact]
    public void RealWindowsSharingViolationAtReplacementPreservesOriginal()
    {
        var repository = new SteamShortcutRepository(() => false);
        var document = repository.LoadExisting(path);
        Rename(document, "Blocked By Handle");
        SteamShortcutSaveResult result;
        // Reads/backup are allowed but Windows refuses replacement while a
        // retained handle does not share deletion. This uses real filesystem I/O.
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = repository.Save(document);
        Assert.False(result.Succeeded);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void CleanupFailureIsReportedWithoutReplacingOriginal()
    {
        var files = new HookedFiles
        {
            BeforeReplace = _ => throw new IOException("Replacement failed"),
            BeforeDelete = _ => throw new UnauthorizedAccessException("Temp cleanup denied")
        };
        var repository = new SteamShortcutRepository(() => false, files);
        var document = repository.LoadExisting(path);
        Rename(document, "Not Saved");
        var result = repository.Save(document);
        Assert.False(result.Succeeded);
        Assert.Contains("Temporary cleanup failed", result.Error);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void MalformedExistingFileIsRejectedAndNeverRecreated()
    {
        byte[] malformed = { 0, 1, 2 };
        File.WriteAllBytes(path, malformed);
        var repository = new SteamShortcutRepository(() => false);
        Assert.Throws<EndOfStreamException>(() => repository.LoadExisting(path));
        Assert.Equal(malformed, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void MissingFileIsNotImplicitlyCreated()
    {
        File.Delete(path);
        var repository = new SteamShortcutRepository(() => false);
        Assert.Throws<FileNotFoundException>(() => repository.LoadExisting(path));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void UnreadableExistingFileDoesNotBecomeEmptyDocument()
    {
        var files = new HookedFiles { BeforeRead = _ => throw new UnauthorizedAccessException("Denied") };
        var repository = new SteamShortcutRepository(() => false, files);
        Assert.Throws<UnauthorizedAccessException>(() => repository.LoadExisting(path));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void InvalidEditCannotOverwriteOriginalOrPriorBackup()
    {
        var repository = new SteamShortcutRepository(() => false);
        var document = repository.LoadExisting(path);
        Rename(document, "Invalid\0Name");
        Assert.False(repository.Save(document).Succeeded);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void DeletedOriginalIsNotRecreatedBySave()
    {
        var repository = new SteamShortcutRepository(() => false);
        var document = repository.LoadExisting(path);
        File.Delete(path);
        Assert.False(repository.Save(document).Succeeded);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void OversizeExistingFileIsRejectedWithoutReadingItsContents()
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
            stream.SetLength(Program.MaxVdfFileBytes + 1L);
        var repository = new SteamShortcutRepository(() => false);
        Assert.Throws<InvalidDataException>(() => repository.LoadExisting(path));
        Assert.Equal(Program.MaxVdfFileBytes + 1L, new FileInfo(path).Length);
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void IndependentAccountResultsDoNotBroadenWriteTargets()
    {
        string secondPath = Path.Combine(directory, "second-account.vdf");
        File.WriteAllBytes(secondPath, original);
        var files = new HookedFiles { BeforeReplace = file =>
            { if (file == path) throw new IOException("First account locked"); } };
        var repository = new SteamShortcutRepository(() => false, files);
        var first = repository.LoadExisting(path);
        var second = repository.LoadExisting(secondPath);
        Rename(first, "First Account");
        Rename(second, "Second Account");
        var results = new[] { repository.Save(first), repository.Save(second) };
        Assert.False(results[0].Succeeded);
        Assert.True(results[1].Succeeded, results[1].Error);
        Assert.Equal(path, results[0].Path);
        Assert.Equal(secondPath, results[1].Path);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal("Second Account", Field(Program.ParseShortcuts(File.ReadAllBytes(secondPath)).Children[0], "AppName").StringValue);
        AssertNoTemporaryFiles();
    }

    private static void Rename(SteamShortcutDocument document, string name) => Field(document.Root.Children[0], "AppName").StringValue = name;
    private static Program.VdfElement Field(Program.VdfElement map, string name) => Assert.Single(map.Children, child => child.Name == name);
    private void AssertNoTemporaryFiles() => Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class HookedFiles : IShortcutFileOperations
    {
        private readonly ShortcutFileOperations inner = new();
        public Action<string>? BeforeRead { get; set; }
        public Action<string>? BeforeWrite { get; set; }
        public Action<string>? BeforeBackup { get; set; }
        public Action<string>? AfterBackup { get; set; }
        public Action<string>? BeforeReplace { get; set; }
        public Action<string>? BeforeDelete { get; set; }
        public Func<string, byte[], byte[]>? TransformRead { get; set; }

        public byte[] Read(string file)
        {
            BeforeRead?.Invoke(file);
            byte[] bytes = inner.Read(file);
            return TransformRead?.Invoke(file, bytes) ?? bytes;
        }
        public void WriteNew(string file, byte[] bytes) { BeforeWrite?.Invoke(file); inner.WriteNew(file, bytes); }
        public void Backup(string file, string backup) { BeforeBackup?.Invoke(file); inner.Backup(file, backup); AfterBackup?.Invoke(file); }
        public void Replace(string temporary, string file) { BeforeReplace?.Invoke(file); inner.Replace(temporary, file); }
        public void Delete(string file) { BeforeDelete?.Invoke(file); inner.Delete(file); }
        public void PrepareCreation(string account, string config) => inner.PrepareCreation(account, config);
        public void Create(string temporary, string file) => inner.Create(temporary, file);
    }
}

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Requires real Windows filesystem sharing/replacement semantics.";
    }
}
