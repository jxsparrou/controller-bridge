using System;
using System.Collections.Generic;
using SBridge.Core;

namespace SBridge.Launching;

internal static class GameLaunchCommand
{
    public static bool TryParse(IReadOnlyList<string> arguments, out Guid id)
    {
        id = Guid.Empty;
        if (arguments.Count == 0 || !string.Equals(arguments[0], "launch", StringComparison.OrdinalIgnoreCase)) return false;
        if (arguments.Count != 2 || !Guid.TryParseExact(arguments[1], "D", out id) || id == Guid.Empty)
            throw new ArgumentException("Usage: sBridge.exe launch <game-id>, using the stored nonempty UUID.");
        return true;
    }

    public static string Options(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A nonempty game ID is required.", nameof(id));
        return "launch " + id.ToString("D");
    }

    public static Game Resolve(AppSettings settings, Guid id) => settings.Games.TryGetValue(id, out var game) ? game :
        throw new KeyNotFoundException("The sBridge game ID is not registered. Reimport the game or restore its configuration.");

    public static string ShortcutProfileKey(string options, AppSettings settings)
    {
        var tokens = WindowsCommandLine.Split(options);
        try
        {
            return TryParse(tokens, out Guid id) ? settings.Games.ContainsKey(id) ? id.ToString("D") : "" :
                WindowsCommandLine.FirstArgument(options);
        }
        catch (ArgumentException) { return ""; } // Do not write overrides to a fake legacy target named 'launch'.
    }

    public static string ShortcutTarget(string options, AppSettings settings)
    {
        var tokens = WindowsCommandLine.Split(options);
        try
        {
            return TryParse(tokens, out Guid id) ? settings.Games.TryGetValue(id, out var game) ? game.Target : "Missing game registration" :
                WindowsCommandLine.FirstArgument(options);
        }
        catch (ArgumentException) { return "Invalid game-ID launch options"; }
    }
}
