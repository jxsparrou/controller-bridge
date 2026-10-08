using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using SBridge.Launching;
using Xunit;

namespace SBridge.Tests;

[SupportedOSPlatform("windows")]
[Collection("Windows integration")]
public class WindowsArgumentForwardingTests
{
    private static readonly string[] TrickyArguments =
    {
        "", "Player One", " tab\tvalue ", "quote\"inside", @"C:\Trailing Space\",
        "slashes\\\\\"quote", "遊戲 Café 🎮", "--option=\"embedded text\"", "line\r\nbreak"
    };

    [WindowsFact]
    public async Task Win32ArgumentListReachesRealChildWithExactTokenBoundaries()
    {
        await CaptureAsync(useRawCommandLine: false);
    }

    [WindowsFact]
    public async Task ComArgumentStringEncodingReachesRealChildWithExactTokenBoundaries()
    {
        // This tests Windows decoding of the string passed to COM, not COM
        // activation or the argument parser of an arbitrary packaged game.
        await CaptureAsync(useRawCommandLine: true);
    }

    [WindowsFact]
    public async Task PackagedShortcutKeepsSpaceContainingHintSeparateFromGameArguments()
    {
        await CaptureAsync(useRawCommandLine: true, packagedShortcut: true);
    }

    [WindowsFact]
    public void PackagedRequestsCannotAccidentallyBecomeWin32ShellLaunches()
    {
        var request = LegacyLaunchRequest.Parse(new[] { "Example_123!Game" }, _ => false)!;
        Assert.Throws<InvalidOperationException>(() => request.CreateWin32StartInfo());
    }

    private static async Task CaptureAsync(bool useRawCommandLine, bool packagedShortcut = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), "sBridge argv " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // Exercise Windows shell execution from a local path with spaces, not
        // an interop UNC path that can invoke network-zone/loader behavior.
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        string probe = Path.Combine(directory, "sBridge.Tests.exe");
        string capture = Path.Combine(directory, "Captured Arguments.json");
        Process? process = null;
        try
        {
            string[] expected = packagedShortcut
                ? new[] { "Example_123!Game", "Game Folder/Game.exe" }.Concat(TrickyArguments).ToArray()
                : TrickyArguments;
            string[] childArguments = new[] { "--write-arguments", capture }.Concat(expected).ToArray();
            var request = LegacyLaunchRequest.Parse(new[] { probe }.Concat(childArguments).ToArray())!;
            var info = request.CreateWin32StartInfo();
            Assert.True(info.UseShellExecute);
            Assert.Equal(Path.GetDirectoryName(probe), info.WorkingDirectory);
            Assert.Equal(childArguments, info.ArgumentList);
            if (useRawCommandLine)
            {
                info.ArgumentList.Clear();
                info.Arguments = WindowsCommandLine.Join(childArguments);
                if (packagedShortcut)
                    info.Arguments = WindowsCommandLine.Join(new[] { "--write-arguments", capture }) + " " +
                        LegacyLaunchOptions.ForPackagedGame("Example_123!Game", "Game Folder/Game.exe") + " " +
                        WindowsCommandLine.Join(TrickyArguments);
            }
            info.WindowStyle = ProcessWindowStyle.Hidden;
            process = Process.Start(info)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            string[] captured = JsonSerializer.Deserialize<string[]>(File.ReadAllText(capture))!;
            Assert.Equal(expected, captured);
            Assert.Equal(childArguments, WindowsCommandLine.Split(WindowsCommandLine.Join(childArguments)));
            if (packagedShortcut)
            {
                var packaged = LegacyLaunchRequest.Parse(captured, _ => false)!;
                Assert.Equal(LegacyLaunchKind.Packaged, packaged.Kind);
                Assert.Equal(@"Game Folder\Game.exe", packaged.ResolveProcessHint(null));
                Assert.Equal("Override.exe", packaged.ResolveProcessHint("Override.exe"));
                Assert.Equal(TrickyArguments, packaged.Arguments);
            }
        }
        finally
        {
            if (process != null)
            {
                if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
                process.Dispose();
            }
            Directory.Delete(directory, recursive: true);
        }
    }
}
