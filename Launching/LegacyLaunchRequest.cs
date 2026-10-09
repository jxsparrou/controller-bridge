using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using SBridge.Core;

namespace SBridge.Launching;

internal enum LegacyLaunchKind { Packaged, Win32, Epic }

// Compatibility adapter, not the future Game/provider identity model.
internal sealed class LegacyLaunchRequest
{
    private LegacyLaunchRequest(string target, LegacyLaunchKind kind, string processHint, IEnumerable<string> arguments)
    {
        Target = target;
        Kind = kind;
        ProcessHint = processHint;
        Arguments = Array.AsReadOnly(arguments.ToArray());
    }

    public string Target { get; }
    public LegacyLaunchKind Kind { get; }
    public string ProcessHint { get; }
    public IReadOnlyList<string> Arguments { get; }

    public static LegacyLaunchRequest FromGame(Game game) => new(game.Target,
        game.LaunchKind switch { GameLaunchKind.Executable => LegacyLaunchKind.Win32,
            GameLaunchKind.EpicLauncher => LegacyLaunchKind.Epic, _ => LegacyLaunchKind.Packaged },
        game.ProcessHint.Length != 0 ? game.ProcessHint : game.LaunchKind == GameLaunchKind.Executable ? game.Target : "",
        game.Arguments);

    public static LegacyLaunchRequest? Parse(IReadOnlyList<string> arguments, Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0) return null; // Settings mode, before resource acquisition.
        foreach (string argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            if (argument.Contains('\0')) throw new ArgumentException("Windows launch arguments cannot contain NUL.", nameof(arguments));
        }
        if (arguments[0].Length == 0) throw new ArgumentException("A launch target is required.", nameof(arguments));

        // Preserve the UWPHook path convention and legacy classification/key rules.
        string target = arguments[0].Replace('/', '\\');
        bool custom = (fileExists ?? File.Exists)(target) || target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || target.Contains('\\');
        string hint = custom ? target : (arguments.Count > 1 ? arguments[1] : "");
        int firstGameArgument = custom ? 1 : 2;
        return new LegacyLaunchRequest(target, custom ? LegacyLaunchKind.Win32 : LegacyLaunchKind.Packaged,
            hint, arguments.Skip(firstGameArgument));
    }

    public string ResolveProcessHint(string? watchOverride) => !string.IsNullOrEmpty(watchOverride)
        ? watchOverride : ProcessHint.Replace('/', '\\');

    [SupportedOSPlatform("windows")]
    public ProcessStartInfo CreateWin32StartInfo()
    {
        if (Kind != LegacyLaunchKind.Win32) throw new InvalidOperationException("A packaged target cannot be launched as a Win32 executable.");
        var info = new ProcessStartInfo(Target)
        {
            UseShellExecute = true, // Retain UAC-capable launching.
            WorkingDirectory = Path.GetDirectoryName(Target) ?? ""
        };
        foreach (string argument in Arguments) info.ArgumentList.Add(argument);
        return info;
    }
}
