namespace SBridge.Steam;

internal static class SteamShortcutBuilder
{
    public static Program.VdfElement Create(int index, string appName, string bridgeExecutable, string bridgeDirectory,
        string launchOptions, string iconPath)
    {
        uint appId = SteamShortcutIdentity.CalculateAppId(appName, bridgeExecutable);
        var shortcut = new Program.VdfElement { Type = 0x00, Name = index.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        shortcut.Children.Add(new Program.VdfElement { Type = 0x02, Name = "appid", IntValue = unchecked((int)appId) });
        AddString(shortcut, "AppName", appName);
        AddString(shortcut, "Exe", "\"" + bridgeExecutable + "\"");
        AddString(shortcut, "StartDir", "\"" + bridgeDirectory.TrimEnd('\\') + "\"");
        AddString(shortcut, "icon", iconPath);
        AddString(shortcut, "ShortcutPath", "");
        AddString(shortcut, "LaunchOptions", launchOptions);
        foreach (var field in new[] { ("IsHidden", 0), ("AllowDesktopConfig", 1), ("AllowOverlay", 1), ("OpenVR", 0), ("LastPlayTime", 0) })
            shortcut.Children.Add(new Program.VdfElement { Type = 0x02, Name = field.Item1, IntValue = field.Item2 });
        shortcut.Children.Add(new Program.VdfElement { Type = 0x00, Name = "tags" });
        return shortcut;
    }

    private static void AddString(Program.VdfElement shortcut, string name, string value) =>
        shortcut.Children.Add(new Program.VdfElement { Type = 0x01, Name = name, StringValue = value });
}
