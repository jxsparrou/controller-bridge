using System;
using System.IO;
using System.Linq;
using SBridge.Artwork;

namespace SBridge.Steam;

internal static class SteamArtworkIconUpdater
{
    public static SteamShortcutSaveResult Update(SteamShortcutRepository repository, ArtworkRequest request, string iconPath)
    {
        try
        {
            string expected = Path.Combine(Path.GetDirectoryName(request.Account.ShortcutPath)!, "grid", request.AppId + "_icon.png");
            if (!string.Equals(Path.GetFullPath(iconPath), expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(iconPath))
                throw new IOException("Validated artwork icon is unavailable or outside the shortcut's grid directory.");
            var document = repository.LoadForImport(request.Account);
            if (document.IsNew) throw new IOException("Artwork cannot recreate a deleted shortcut file.");
            var matches = document.Root.Children.Where(entry =>
                entry.Children.Any(field => field.Name == "appid" && field.Type == 0x02 && unchecked((uint)field.IntValue) == request.AppId) &&
                entry.Children.Any(field => field.Name == "Exe" && field.Type == 0x01 && field.StringValue == request.Exe) &&
                entry.Children.Any(field => field.Name == "LaunchOptions" && field.Type == 0x01 && field.StringValue == request.LaunchOptions) &&
                entry.Children.Any(field => field.Name == "AppName" && field.Type == 0x01 && field.StringValue == request.Name)).ToArray();
            if (matches.Length != 1) throw new IOException("Artwork shortcut identity is missing or ambiguous.");
            var icon = matches[0].Children.SingleOrDefault(field => field.Name == "icon" && field.Type == 0x01);
            if (icon == null || icon.StringValue != request.OriginalIcon) throw new IOException("Shortcut icon changed while artwork was downloading.");
            icon.StringValue = iconPath;
            return repository.Save(document);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { return new SteamShortcutSaveResult(request.Account.ShortcutPath, false, ex.Message); }
    }
}
