using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SBridge.Configuration;
using SBridge.Core;
using SBridge.Sisr;
using Xunit;

namespace SBridge.Tests;

public class SisrReadinessTests
{
    private static SisrStatusSnapshot Ready => new("v0.6.1", true, false, true, false, false, false, false, 0, "xbox360", false);

    [Fact]
    public async Task DelayedApiReadinessDoesNotRequireSteamViiperOrControllerPresence()
    {
        int calls = 0; var source = new Source((_, _) => ++calls < 3 ? null : Ready);
        var status = await SisrReadiness.WaitAsync(new Process(), source, CancellationToken.None, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
        Assert.Equal(3, calls); Assert.Equal(0, status.DeviceCount);
        Assert.Contains(SisrReadiness.Describe(status), line => line.Contains("not required"));
        Assert.Contains(SisrReadiness.Describe(status), line => line.Contains("not controller readiness"));
    }

    [Fact]
    public async Task ExitedUnsupportedAndCancelledStartupNeverReturnReady()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => SisrReadiness.WaitAsync(new Process { HasExited = true }, new Source((_, _) => Ready), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SisrReadiness.WaitAsync(new Process(), new Source((_, _) => Ready with { ApiSupported = false }), CancellationToken.None));
        var process = new Process();
        await Assert.ThrowsAsync<InvalidOperationException>(() => SisrReadiness.WaitAsync(process, new Source((_, _) => { process.HasExited = true; return Ready; }), CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SisrReadiness.WaitAsync(new Process(), new Source((_, _) => Ready), cancelled.Token));
    }

    [Fact]
    public async Task UnavailableOrMalformedApiHasTotalStartupBudgetAndCleanupPrecedesFailure()
    {
        var process = new Process(); var lease = new Lease();
        using var manager = SisrProcessManager.Start(() => process, () => lease, () => false, new Quit(), _ => { });
        bool activated = false;
        try
        {
            await manager.WaitForReadyAsync(new Source((_, _) => throw new InvalidDataException("not ready")), CancellationToken.None, TimeSpan.FromMilliseconds(50));
            activated = true;
        }
        catch (TimeoutException) { await manager.StopAsync(); }
        Assert.False(activated); Assert.True(process.Disposed); Assert.True(lease.Disposed); Assert.True(process.HasExited);
    }

    [Fact]
    public void StatusSummaryDoesNotLogServerDeviceOrArbitraryVersionSuffixDetails()
    {
        var status = Ready with { Version = "v0.6.1-private-token", NoSteamMode = false, InitialLaunch = true };
        string text = string.Join("\n", SisrReadiness.Describe(status));
        Assert.DoesNotContain("private-token", text); Assert.Contains("version=0.6.1", text);
        Assert.Contains("marker shortcut is missing", text); Assert.Contains("first", text);
    }

    [Fact]
    public void ManagedPolicyIsOptionalTypedAndClonedWithoutChangingLegacySettings()
    {
        var settings = new AppSettings { SisrArguments = "--viiper.address=example.invalid:3242", ManagedSisrStartup = true };
        var protector = new NoSecret(); byte[] bytes = JsonSettingsCodec.Encode(settings, null, protector);
        var loaded = JsonSettingsCodec.Decode(bytes, protector).Settings;
        Assert.True(loaded.ManagedSisrStartup); Assert.True(loaded.Clone().ManagedSisrStartup);
        Assert.Equal(settings.SisrArguments, loaded.SisrArguments);
        var old = JsonNode.Parse(bytes)!.AsObject(); old.Remove("managedSisrStartup");
        Assert.False(JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(old.ToJsonString()), protector).Settings.ManagedSisrStartup);
        old["managedSisrStartup"] = "true";
        Assert.Throws<InvalidDataException>(() => JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(old.ToJsonString()), protector));
    }

    [Theory]
    [InlineData("--config=custom.json")]
    [InlineData("--api.listen-address 0.0.0.0:6400")]
    [InlineData("--log.file=external.log")]
    [InlineData("--lf external.log")]
    public void ConflictingManagedFlagsFailBeforeStartingAnyResources(string arguments) =>
        Assert.Throws<ArgumentException>(() => SisrManagedStartup.Arguments(arguments));

    [Fact]
    public void ManagedJsonUsesKongFlagKeysAndPreservesDecodedArgumentsAndSteamEnvironment()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sBridge managed " + Guid.NewGuid().ToString("N"));
        try
        {
            using var plan = SisrManagedStartup.Create(Path.GetFullPath(directory));
            var tokens = SisrManagedStartup.Arguments("--no-steam --viiper.password=\"has spaces\" --window.show=true");
            Assert.Equal(new[] { "--no-steam", "--viiper.password=has spaces", "--window.show=true" }, tokens);
            var info = plan.StartInfo(Path.GetFullPath("SISR.exe"), tokens);
            Assert.Equal(plan.Directory, info.WorkingDirectory); Assert.False(info.UseShellExecute);
            Assert.Equal("--config=" + plan.ConfigPath, info.ArgumentList[0]); Assert.Equal(tokens, info.ArgumentList.Skip(1));
            Assert.Equal("127.0.0.1:0", info.Environment["SISR_API_LISTEN_ADDRESS"]); Assert.Equal(plan.LogPath, info.Environment["SISR_LOG_FILE"]);
            using var json = JsonDocument.Parse(File.ReadAllBytes(plan.ConfigPath));
            Assert.Equal("127.0.0.1:0", json.RootElement.GetProperty("api.listen_address").GetString());
            Assert.False(json.RootElement.GetProperty("window.show").GetBoolean());
            Assert.DoesNotContain("has spaces", File.ReadAllText(plan.ConfigPath)); // Advanced credentials are not copied to JSON.
            Assert.Equal(Environment.GetEnvironmentVariable("SteamAppId"), info.Environment.TryGetValue("SteamAppId", out string? value) ? value : null);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class Process : IOwnedSisrProcess
    {
        public int Id => 123; public bool HasExited { get; set; } public bool Disposed { get; private set; }
        public Task WaitForExitAsync(CancellationToken token) => Task.CompletedTask;
        public bool CloseMainWindow() { HasExited = true; return true; } public void Kill() => HasExited = true;
        public void Dispose() => Disposed = true;
    }
    private sealed class Source(Func<IOwnedSisrProcess, CancellationToken, SisrStatusSnapshot?> probe) : ISisrStatusSource
    { public Task<SisrStatusSnapshot?> ProbeAsync(IOwnedSisrProcess process, CancellationToken token) => Task.FromResult(probe(process, token)); }
    private sealed class Lease : IDisposable { public bool Disposed { get; private set; } public void Dispose() => Disposed = true; }
    private sealed class Quit : ISisrShutdown { public Task<bool> RequestQuitAsync(IOwnedSisrProcess process, CancellationToken token) => Task.FromResult(false); }
    private sealed class NoSecret : ISecretProtector
    { public string Protect(string value) => throw new InvalidOperationException(); public string Unprotect(string value) => throw new InvalidOperationException(); }
}
