using System.Text;
using SBridge.Diagnostics;
using SBridge.Sisr;
using Xunit;

namespace SBridge.Tests;

[Collection("Windows integration")]
public sealed class DiagnosticsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sBridge-diagnostics-" + Guid.NewGuid().ToString("N"));
    public DiagnosticsTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void RotationCapsFilesAndKeepsNewestUtf8Entries()
    {
        string path = Path.Combine(directory, "logs", "sBridge.log"); var log = new BoundedLog(path, 1024, 2);
        for (int index = 0; index < 100; index++) Assert.True(log.Write("entry=" + index + " 遊戲 Café " + new string('x', 100)));
        Assert.Equal(3, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
        Assert.All(Directory.GetFiles(Path.GetDirectoryName(path)!), file => Assert.InRange(new FileInfo(file).Length, 1, 1024));
        Assert.Contains("entry=99", File.ReadAllText(path)); Assert.DoesNotContain("entry=0 ", string.Join("", Directory.GetFiles(Path.GetDirectoryName(path)!).Select(File.ReadAllText)));
        Assert.Contains("[bridge=", File.ReadAllText(path)); Assert.Equal(0, log.FailedWrites);
    }

    [Fact]
    public async Task ConcurrentWritersProduceBoundedSingleLineRecords()
    {
        string path = Path.Combine(directory, "sBridge.log");
        var first = new BoundedLog(path, 4096, 3); var second = new BoundedLog(path, 4096, 3);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int count = 0; count < 20; count++) Assert.True((worker % 2 == 0 ? first : second).Write("worker=" + worker + " line\r\nforged"));
        })));
        foreach (string file in Directory.GetFiles(directory))
        {
            Assert.InRange(new FileInfo(file).Length, 1, 4096);
            Assert.All(File.ReadAllLines(file), line => { Assert.StartsWith("[", line); Assert.Contains("line\\r\\nforged", line); });
        }
    }

    [Fact]
    public void OversizedLegacyLogAndOversizedMessagesAreBoundedWithoutPartialUtf8()
    {
        string path = Path.Combine(directory, "sBridge.log");
        File.WriteAllText(path, string.Concat(Enumerable.Repeat("遊戲 Café old entry\r\n", 500)));
        var log = new BoundedLog(path, 1024, 2); Assert.True(log.Write("new entry")); Assert.True(log.Write(new string('z', 20000)));
        foreach (string file in Directory.GetFiles(directory))
        { Assert.InRange(new FileInfo(file).Length, 0, 1024); _ = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(file)); }
        Assert.Contains("oversized", string.Join("", Directory.GetFiles(directory).Select(File.ReadAllText)));
    }

    [Fact]
    public void RedactionRemovesKnownKeyCredentialFlagsAndBearerTokens()
    {
        string value = DiagnosticRedaction.LogMessage("Args=--password=\"has spaces\" --token abc --api-key=xyz Authorization: Bearer hidden key=private-key", "private-key");
        foreach (string secret in new[] { "has spaces", "abc", "xyz", "hidden", "private-key" }) Assert.DoesNotContain(secret, value);
        Assert.Contains("--password=[redacted]", value); Assert.Contains("Bearer [redacted]", value);
        Assert.Equal("[oversized diagnostic message omitted]", DiagnosticRedaction.LogMessage(new string('x', 20000), "key"));
    }

    [WindowsFact]
    public void LoggingFailureIsVisibleAsCountAndDoesNotOverwriteReadOnlyFiles()
    {
        string path = Path.Combine(directory, "sBridge.log"); File.WriteAllText(path, "original");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var log = new BoundedLog(path, 1024, 1); Assert.False(log.Write("new")); Assert.Equal(1, log.FailedWrites);
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public void CompletedSessionsAreRetainedAndCappedWhileActiveAndUnknownContentAreProtected()
    {
        using var active = SisrManagedStartup.Create(directory);
        var completed = new List<string>();
        for (int index = 0; index < 12; index++)
        {
            using var session = SisrManagedStartup.Create(directory); completed.Add(session.Directory);
            session.RecordStatus("ended", null);
        }
        // Startup pruning may already have removed older completed folders.
        string latest = completed.Last();
        File.WriteAllText(Path.Combine(latest, "SISR.log"), string.Concat(Enumerable.Repeat("line\n", 900000)));
        using (var unknown = SisrManagedStartup.Create(directory)) File.WriteAllText(Path.Combine(unknown.Directory, "user-notes.txt"), "Keep me");
        string legacy = Path.Combine(directory, "sisr", "sessions", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "startup.json"), "unmarked prior data");
        ManagedSessionDiagnostics.Prune(directory);
        Assert.True(Directory.Exists(active.Directory)); Assert.True(Directory.Exists(legacy));
        Assert.Single(Directory.GetFiles(directory, "user-notes.txt", SearchOption.AllDirectories));
        Assert.InRange(completed.Count(Directory.Exists), 1, ManagedSessionDiagnostics.RetainedSessions);
        Assert.InRange(new FileInfo(Path.Combine(latest, "SISR.log")).Length, 1, ManagedSessionDiagnostics.MaxCompletedLogBytes);
    }

    [Fact]
    public void SafeSummaryContainsOnlyTypedStatusAndReportOmitsArbitraryDiskStrings()
    {
        using var session = SisrManagedStartup.Create(directory);
        session.RecordStatus("ready", new SisrStatusSnapshot("v0.6.1-secret-suffix", true, true, false, true, true, true, false, 0, "dualsense", false));
        var summary = ManagedSessionDiagnostics.ReadLatest(directory); Assert.NotNull(summary);
        string json = File.ReadAllText(Path.Combine(session.Directory, "summary.json"));
        Assert.DoesNotContain("secret-suffix", json); Assert.DoesNotContain(directory, json);
        var snapshot = new DiagnosticSnapshot("1.0.0.0", "10.0.12", true, 0, true, 2, 1, 0, 5, 2, true, true, true, true, 0, true,
            summary! with { Version = "password-secret", ControllerType = "device-serial-secret", Phase = "C:\\Users\\secret" });
        string report = snapshot.SafeReport();
        foreach (string secret in new[] { "password-secret", "device-serial-secret", "C:\\Users", directory }) Assert.DoesNotContain(secret, report);
        Assert.Contains("historical, not a live API probe", report); Assert.Contains("controller=unknown", report);
    }

    [Fact]
    public void ReadOnlyLatestSummaryDoesNotCreateDataOrAcceptOversizedCorruptState()
    {
        Assert.Null(ManagedSessionDiagnostics.ReadLatest(Path.Combine(directory, "missing")));
        Assert.False(Directory.Exists(Path.Combine(directory, "missing")));
        using var session = SisrManagedStartup.Create(directory);
        File.WriteAllText(Path.Combine(session.Directory, "summary.json"), new string('x', 5000));
        Assert.Null(ManagedSessionDiagnostics.ReadLatest(directory));
        File.WriteAllText(Path.Combine(session.Directory, "summary.json"), "{bad json");
        Assert.Null(ManagedSessionDiagnostics.ReadLatest(directory));
    }

    [Fact]
    public void UnlockedStartingReadyAndIncompleteSessionsAreNotMistakenForCompletedResources()
    {
        var protectedDirectories = new List<string>();
        foreach (string phase in new[] { "starting", "ready", "cleanup-incomplete" })
        {
            using var session = SisrManagedStartup.Create(directory);
            session.RecordStatus(phase, null); protectedDirectories.Add(session.Directory);
        }
        for (int index = 0; index < 12; index++)
        {
            using var session = SisrManagedStartup.Create(directory); session.RecordStatus("ended", null);
        }
        ManagedSessionDiagnostics.Prune(directory);
        Assert.All(protectedDirectories, folder => Assert.True(Directory.Exists(folder)));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
