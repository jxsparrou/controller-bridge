using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SBridge.Core;
using SBridge.Launching;

namespace SBridge.Steam;

internal sealed record SteamAccountImportResult(SteamShortcutSaveResult Save, int Added);

internal static class SteamShortcutImporter
{
    public static IReadOnlyList<SteamAccountImportResult> Import(SteamShortcutRepository repository,
        IReadOnlyList<SteamAccount> accounts, IReadOnlyList<Game> games, Action<SteamAccount, Program.VdfElement, Game> add)
    {
        if (accounts.Count == 0) throw new InvalidOperationException("An explicit Steam account selection is required.");
        var results = new List<SteamAccountImportResult>();
        foreach (var account in accounts)
        {
            try
            {
                var document = repository.LoadForImport(account);
                int added = 0;
                foreach (var game in games)
                {
                    if (ContainsGame(document.Root, game)) continue;
                    add(account, document.Root, game); added++;
                }
                var save = added == 0 ? new SteamShortcutSaveResult(account.ShortcutPath, true, null) : repository.Save(document);
                results.Add(new SteamAccountImportResult(save, save.Succeeded ? added : 0));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { results.Add(new SteamAccountImportResult(new SteamShortcutSaveResult(account.ShortcutPath, false, ex.Message), 0)); }
        }
        return results;
    }

    internal static bool ContainsGame(Program.VdfElement root, Game game)
    {
        foreach (var shortcut in root.Children)
        {
            string exe = shortcut.Children.FirstOrDefault(field => field.Name.Equals("Exe", StringComparison.OrdinalIgnoreCase))?.StringValue ?? "";
            string name = exe.Trim('"').Replace('/', '\\').Split('\\').Last();
            if (!name.Equals("sBridge.exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("uwphook-bridge.exe", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("UWPHook.exe", StringComparison.OrdinalIgnoreCase)) continue;
            string options = shortcut.Children.FirstOrDefault(field => field.Name.Equals("LaunchOptions", StringComparison.OrdinalIgnoreCase))?.StringValue ?? "";
            try
            {
                var tokens = WindowsCommandLine.Split(options);
                if (GameLaunchCommand.TryParse(tokens, out Guid id)) { if (id == game.Id) return true; continue; }
                if (game.LaunchKind == GameLaunchKind.EpicLauncher) continue;
                var legacy = LegacyLaunchRequest.Parse(tokens, _ => false);
                if (legacy != null && string.Equals(legacy.Target, game.Target.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) &&
                    legacy.Arguments.SequenceEqual(game.Arguments, StringComparer.Ordinal)) return true;
            }
            catch (ArgumentException) { } // A malformed unrelated entry is not a duplicate.
        }
        return false;
    }
}
