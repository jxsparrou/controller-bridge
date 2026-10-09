using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using SBridge.Configuration;
using SBridge.Core;
using SBridge.Launching;
using SBridge.Providers;
using SBridge.Sessions;
using Xunit;

namespace SBridge.Tests;

[SupportedOSPlatform("windows")]
[Collection("Windows integration")]
public class WindowsEpicTests
{
    private static byte[] Manifest(Action<JsonObject>? edit = null)
    {
        var data = new JsonObject
        {
            ["AppName"] = "GameApp", ["CatalogNamespace"] = "ns", ["CatalogItemId"] = "item", ["DisplayName"] = "遊戲 | Café 🎮",
            ["InstallLocation"] = @"C:\Epic Games\Game", ["LaunchExecutable"] = "Game/Binaries/Game.exe", ["bIsApplication"] = true,
            ["bIsExecutable"] = true, ["bIsIncompleteInstall"] = false, ["MainGameAppName"] = "GameApp", ["LaunchCommand"] = "--launcher-owned-default"
        };
        edit?.Invoke(data); return Encoding.UTF8.GetBytes(data.ToJsonString());
    }

    [WindowsFact]
    public void ManifestPreservesUnicodeFullIdentityAndContainedHintWithoutDuplicatingLauncherArgs()
    {
        var game = EpicManifestCodec.Decode(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Manifest()).ToArray())!;
        Assert.Equal("遊戲 | Café 🎮", game.Name);
        Assert.Equal("ns:item:GameApp", game.ProviderId);
        Assert.Equal(@"C:\Epic Games\Game\Game\Binaries\Game.exe", game.ProcessHint);
        Assert.Equal(@"C:\Epic Games\Game", game.InstallDirectory);
        Assert.Empty(game.Arguments);
        Assert.Equal(GameLaunchKind.EpicLauncher, game.LaunchKind);
        Assert.Equal(EpicLaunchIdentity.Target(game.ProviderId), game.Target);
        Assert.Equal(@"D:\Game.exe", EpicManifestCodec.Decode(Manifest(data =>
        { data["InstallLocation"] = @"D:\"; data["LaunchExecutable"] = "Game.exe"; }))!.ProcessHint);
    }

    [WindowsFact]
    public void NonLaunchableDlcAndIncompleteInstallsAreOmitted()
    {
        Assert.Null(EpicManifestCodec.Decode(Manifest(data => data["bIsApplication"] = false)));
        Assert.Null(EpicManifestCodec.Decode(Manifest(data => data["bIsExecutable"] = false)));
        Assert.Null(EpicManifestCodec.Decode(Manifest(data => data["bIsIncompleteInstall"] = true)));
        Assert.Null(EpicManifestCodec.Decode(Manifest(data => data["MainGameAppName"] = "OtherGame")));
    }

    [WindowsFact]
    public void InvalidTypedJsonDuplicatesAndEscapingPathsAreRejected()
    {
        foreach (string field in new[] { "AppName", "DisplayName", "CatalogNamespace", "InstallLocation", "LaunchExecutable" })
            Assert.Throws<InvalidDataException>(() => EpicManifestCodec.Decode(Manifest(data => data[field] = null)));
        Assert.Throws<InvalidDataException>(() => EpicManifestCodec.Decode(Manifest(data => data["bIsExecutable"] = "true")));
        Assert.Throws<InvalidDataException>(() => EpicManifestCodec.Decode(Encoding.UTF8.GetBytes("{\"AppName\":\"x\",\"AppName\":\"y\"}")));
        foreach (string exe in new[] { "../../Other.exe", @"C:\Other.exe", @"\Other.exe", "Game.exe:stream", "Game.txt" })
            Assert.Throws<InvalidDataException>(() => EpicManifestCodec.Decode(Manifest(data => data["LaunchExecutable"] = exe)));
        Assert.Throws<InvalidDataException>(() => EpicManifestCodec.Decode(Manifest(data => data["InstallLocation"] = "relative")));
        Assert.Throws<InvalidDataException>(() => EpicManifestCodec.Decode(new byte[EpicManifestCodec.MaxManifestBytes + 1]));
    }

    [WindowsFact]
    public async Task DiscoveryIsReadOnlyWarnsOnBadManifestsAndOmitsConflictingInstallations()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sBridge Epic " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] good = Manifest();
            await File.WriteAllBytesAsync(Path.Combine(directory, "a.item"), good);
            await File.WriteAllBytesAsync(Path.Combine(directory, "duplicate.item"), good);
            await File.WriteAllTextAsync(Path.Combine(directory, "broken.item"), "{bad json");
            await File.WriteAllBytesAsync(Path.Combine(directory, "dlc.item"), Manifest(data => data["bIsExecutable"] = false));
            var discovery = new WindowsEpicDiscovery(directory, directory, Path.Combine(directory, "missing"));
            var result = await discovery.DiscoverAsync(CancellationToken.None);
            Assert.Single(result.Games); Assert.Single(result.Warnings);
            Assert.Equal(good, await File.ReadAllBytesAsync(Path.Combine(directory, "a.item")));
            await File.WriteAllBytesAsync(Path.Combine(directory, "conflict.item"), Manifest(data => data["InstallLocation"] = @"D:\Other"));
            result = await discovery.DiscoverAsync(CancellationToken.None);
            Assert.Empty(result.Games); Assert.Contains(result.Warnings, message => message.Contains("Conflicting"));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.DiscoverAsync(cancellation.Token));
        }
        finally { Directory.Delete(directory, true); }
    }

    [WindowsFact]
    public void ProviderRoutesEpicAndReturnsPidZeroEvidenceInsteadOfAdoptingLauncher()
    {
        ProcessStartInfo? received = null;
        var activation = new WindowsEpicActivation(info => { received = info; return Process.GetCurrentProcess(); });
        var discovery = new WindowsEpicDiscovery();
        var provider = new EpicProvider(discovery, activation);
        var request = LegacyLaunchRequest.FromGame(EpicManifestCodec.Decode(Manifest())!.CreateRegistration());
        var context = new ProviderLaunchContext(Guid.NewGuid(), DateTimeOffset.UtcNow, [], request.ProcessHint, request.ProcessHint, @"C:\Epic Games\Game");
        Assert.Same(provider, new GameProviders(provider).ForLaunch(request));
        var evidence = provider.Launch(request, context, _ => { });
        Assert.Null(evidence.InitialProcess); Assert.Null(evidence.InitialObservation);
        Assert.Equal(context.ExpectedPath, evidence.ExpectedPath);
        Assert.Equal("Game", evidence.ExpectedName);
        Assert.True(received!.UseShellExecute); Assert.Empty(received.ArgumentList);
        Assert.Equal(request.Target, received.FileName);
        Assert.Throws<ArgumentException>(() => activation.Activate(LegacyLaunchRequest.Parse(["Game.exe"])!, _ => { }));
    }

    [WindowsFact]
    public void EpicGameAndUuidProfileSurviveJsonRoundtrip()
    {
        var settings = new AppSettings();
        var game = GameCatalog.Register(settings, EpicManifestCodec.Decode(Manifest())!.CreateRegistration(),
            new GameProfile(SteamInputMode.Disabled, "RealGame.exe"));
        var protector = new NoSecret();
        var loaded = JsonSettingsCodec.Decode(JsonSettingsCodec.Encode(settings, null, protector), protector).Settings;
        var restored = loaded.Games[game.Id];
        Assert.Equal(GameLaunchKind.EpicLauncher, restored.LaunchKind);
        Assert.Equal(game.Target, restored.Target); Assert.Equal(game.ProviderId, restored.ProviderId);
        Assert.Equal("RealGame.exe", loaded.GetProfile(game.ProfileKey).WatchProcess);
    }

    [WindowsFact]
    public async Task WindowsShellProtocolPreservesUriAndPidZeroEvidenceFindsRealGameAfterLauncherExit()
    {
        // Controlled unique test protocol, never replace Epic's real association.
        string scheme = "sbridge-epic-test-" + Guid.NewGuid().ToString("N");
        string keyPath = @"Software\Classes\" + scheme;
        string directory = Path.Combine(Path.GetTempPath(), "sBridge Epic protocol " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        foreach (string name in new[] { "Bootstrap", "Game" }) File.Copy(Path.Combine(directory, "sBridge.Tests.exe"), Path.Combine(directory, name + ".exe"));
        string capture = Path.Combine(directory, "Captured URI.json");
        string events = Path.Combine(directory, "processes.txt");
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Process? observed = null;
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key.SetValue("", "sBridge test protocol"); key.SetValue("URL Protocol", "");
                using var command = key.CreateSubKey(@"shell\open\command");
                command.SetValue("", WindowsCommandLine.Join([Path.Combine(directory, "sBridge.Tests.exe"), "--protocol-chain", capture,
                    events, Path.Combine(directory, "Bootstrap.exe"), Path.Combine(directory, "Game.exe")]) + " \"%1\"");
            }
            var request = LegacyLaunchRequest.FromGame(EpicManifestCodec.Decode(Manifest())!.CreateRegistration());
            string expected = scheme + request.Target[request.Target.IndexOf(':')..];
            var activation = new WindowsEpicActivation(info =>
            {
                Assert.Equal(request.Target, info.FileName); Assert.True(info.UseShellExecute);
                info.FileName = expected;
                var process = Process.Start(info);
                if (process != null) observed = Process.GetProcessById(process.Id);
                return process;
            });
            var observer = new WindowsProcessObserver();
            var baseline = observer.Capture();
            started = DateTimeOffset.UtcNow;
            var context = new ProviderLaunchContext(Guid.NewGuid(), started, baseline, "Game.exe", Path.Combine(directory, "Game.exe"), directory);
            var provider = new EpicProvider(new WindowsEpicDiscovery(directory), activation);
            var evidence = provider.Launch(request, context, _ => { });
            Assert.Null(evidence.InitialProcess);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await new GameSessionMonitor(observer, new GameSessionOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(100), LaunchSearchTimeout = TimeSpan.FromSeconds(8),
                ExitGrace = TimeSpan.FromMilliseconds(300), MaxProvisionalLifetime = TimeSpan.FromSeconds(15)
            }).MonitorAsync(evidence, _ => { }, timeout.Token);
            Assert.Equal(GameSessionOutcome.Completed, result.Outcome);
            int[] ids = File.ReadAllLines(events).Select(int.Parse).ToArray();
            Assert.Equal(3, ids.Length); Assert.Equal(ids[2], result.FinalProcess?.Id);
            if (observed != null) Assert.True(observed.HasExited);
            Assert.Equal(new[] { expected }, JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(capture, timeout.Token)));
        }
        finally
        {
            if (observed != null) { if (!observed.HasExited) { observed.Kill(); observed.WaitForExit(); } observed.Dispose(); }
            if (File.Exists(events))
                foreach (int id in File.ReadAllLines(events).Select(int.Parse))
                {
                    try
                    {
                        using var process = Process.GetProcessById(id);
                        if (process.StartTime.ToUniversalTime() >= started.UtcDateTime &&
                            ProcessMatcher.IsWithinDirectory(WindowsProcessObserver.ImagePath(id), directory))
                        { process.Kill(); process.WaitForExit(); }
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            Directory.Delete(directory, true);
        }
    }

    private sealed class NoSecret : ISecretProtector
    {
        public string Protect(string plaintext) => throw new InvalidOperationException("Test has no credential.");
        public string Unprotect(string ciphertext) => throw new InvalidOperationException("Test has no credential.");
    }
}
