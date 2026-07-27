using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using System.Text;

partial class Program
{
    // === SteamShortcutItem class ===
    public class SteamShortcutItem
    {
        public string AppName { get; set; }
        public string Exe { get; set; }
        public string LaunchOptions { get; set; }
        public string VdfPath { get; set; }
        public VdfElement ShortcutElement { get; set; }
        public VdfElement RootElement { get; set; }
    }

    // === SteamAccountInfo class ===
    public class SteamAccountInfo
    {
        public string AccountId;   // userdata folder name (32-bit account id, as string)
        public string SteamId64;   // derived, for lookup only
        public string AccountName; // login name
        public string PersonaName; // display name
        public string VdfPath;     // full path to this account's shortcuts.vdf (may not exist yet)
    }

    // === Steam path & shortcut methods ===
    public static string FindSteamPath()
    {
        string steamPath = null;
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                if (key != null)
                {
                    var val = key.GetValue("SteamPath");
                    if (val != null) steamPath = val.ToString();
                }
            }

            if (string.IsNullOrEmpty(steamPath))
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\Valve\Steam"))
                {
                    if (key != null)
                    {
                        var val = key.GetValue("InstallPath");
                        if (val != null) steamPath = val.ToString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("Error resolving Steam install path: " + ex.Message);
        }
        return steamPath;
    }

    public static List<string> FindShortcutsVdfFiles()
    {
        var paths = new List<string>();
        try
        {
            string steamPath = FindSteamPath();
            if (!string.IsNullOrEmpty(steamPath))
            {
                string userdataPath = Path.Combine(steamPath, "userdata");
                if (Directory.Exists(userdataPath))
                {
                    var vdfFiles = Directory.GetFiles(userdataPath, "shortcuts.vdf", SearchOption.AllDirectories);
                    paths.AddRange(vdfFiles);
                }
            }
        }
        catch (Exception ex)
        {
            Log("Error searching for Steam shortcuts: " + ex.Message);
        }

        if (paths.Count == 0)
        {
            string[] fallbacks = {
                @"C:\Program Files (x86)\Steam\userdata",
                @"C:\Program Files\Steam\userdata"
            };
            foreach (var fb in fallbacks)
            {
                try
                {
                    if (Directory.Exists(fb))
                    {
                        var vdfFiles = Directory.GetFiles(fb, "shortcuts.vdf", SearchOption.AllDirectories);
                        paths.AddRange(vdfFiles);
                    }
                }
                catch { }
            }
        }

        return paths;
    }

    public static List<string> FindShortcutsVdfFiles(bool selectedOnly)
    {
        if (!selectedOnly || selectedSteamAccountIds.Count == 0)
        {
            return FindShortcutsVdfFiles();
        }

        // Derive the filtered list from the account list itself (via each account's computed
        // VdfPath) rather than from the set of already-existing shortcuts.vdf files. Steam
        // doesn't create shortcuts.vdf for an account until it has its first non-Steam
        // shortcut, so a freshly-selected account with no prior shortcuts would otherwise be
        // filtered out entirely even though its (not-yet-existing) path is well-defined.
        var filtered = new List<string>();
        foreach (var account in FindSteamAccounts())
        {
            if (selectedSteamAccountIds.Contains(account.AccountId))
            {
                filtered.Add(account.VdfPath);
            }
        }
        return filtered;
    }

    private class LoginUserEntry
    {
        public string AccountName;
        public string PersonaName;
    }

    private static Dictionary<string, LoginUserEntry> ParseLoginUsers(string steamPath)
    {
        var result = new Dictionary<string, LoginUserEntry>();
        try
        {
            string loginUsersPath = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(loginUsersPath)) return result;

            string content = File.ReadAllText(loginUsersPath);

            // Match each `"<17-digit SteamID64>" { ... }` block (non-greedy up to the next top-level close brace)
            var blockMatches = System.Text.RegularExpressions.Regex.Matches(
                content, "\"(\\d{17})\"\\s*\\{([^{}]*)\\}");

            foreach (System.Text.RegularExpressions.Match m in blockMatches)
            {
                string steamId64 = m.Groups[1].Value;
                string block = m.Groups[2].Value;

                var entry = new LoginUserEntry();
                var accountNameMatch = System.Text.RegularExpressions.Regex.Match(block, "\"AccountName\"\\s*\"([^\"]*)\"");
                var personaNameMatch = System.Text.RegularExpressions.Regex.Match(block, "\"PersonaName\"\\s*\"([^\"]*)\"");
                entry.AccountName = accountNameMatch.Success ? accountNameMatch.Groups[1].Value : "";
                entry.PersonaName = personaNameMatch.Success ? personaNameMatch.Groups[1].Value : "";

                result[steamId64] = entry;
            }
        }
        catch (Exception ex)
        {
            Log("Failed to parse loginusers.vdf: " + ex.Message);
        }
        return result;
    }

    public static List<SteamAccountInfo> FindSteamAccounts()
    {
        var accounts = new List<SteamAccountInfo>();
        string steamPath = FindSteamPath();
        if (string.IsNullOrEmpty(steamPath))
        {
            steamPath = Directory.Exists(@"C:\Program Files (x86)\Steam") ? @"C:\Program Files (x86)\Steam" : null;
        }
        if (string.IsNullOrEmpty(steamPath)) return accounts;

        string userdataPath = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdataPath)) return accounts;

        var loginUsers = ParseLoginUsers(steamPath);

        try
        {
            foreach (string accountDir in Directory.GetDirectories(userdataPath))
            {
                string accountId = Path.GetFileName(accountDir);
                long accountIdNum;
                if (!long.TryParse(accountId, out accountIdNum)) continue; // skip non-numeric folders (e.g. "0" / "ac")
                if (accountIdNum == 0) continue; // "0" is Steam's shared/anonymous placeholder, not a real account

                string steamId64 = (accountIdNum + 76561197960265728L).ToString();

                var info = new SteamAccountInfo
                {
                    AccountId = accountId,
                    SteamId64 = steamId64,
                    VdfPath = Path.Combine(accountDir, "config", "shortcuts.vdf")
                };

                LoginUserEntry entry;
                if (loginUsers.TryGetValue(steamId64, out entry))
                {
                    info.AccountName = entry.AccountName;
                    info.PersonaName = entry.PersonaName;
                }
                else
                {
                    info.AccountName = accountId;
                    info.PersonaName = accountId;
                }

                accounts.Add(info);
            }
        }
        catch (Exception ex)
        {
            Log("Error enumerating Steam accounts: " + ex.Message);
        }

        return accounts;
    }

    public static List<SteamShortcutItem> LoadSteamShortcuts()
    {
        var list = new List<SteamShortcutItem>();
        var vdfFiles = FindShortcutsVdfFiles();
        foreach (var vdfPath in vdfFiles)
        {
            try
            {
                if (!File.Exists(vdfPath)) continue;
                byte[] bytes = File.ReadAllBytes(vdfPath);
                using (var ms = new MemoryStream(bytes))
                using (var reader = new BinaryReader(ms))
                {
                    byte firstByte = reader.ReadByte();
                    if (firstByte != 0x00) continue;
                    string rootName = ReadNullTerminatedString(reader);
                    var root = ReadMap(reader, rootName);

                    // root is "shortcuts" map.
                    // Children are shortcut maps.
                    foreach (var child in root.Children)
                    {
                        if (child.Type == 0x00) // It's a shortcut map
                        {
                            var exeChild = child.Children.Find(c => c.Name.Equals("Exe", StringComparison.OrdinalIgnoreCase));
                            var nameChild = child.Children.Find(c => c.Name.Equals("AppName", StringComparison.OrdinalIgnoreCase));
                            var launchChild = child.Children.Find(c => c.Name.Equals("LaunchOptions", StringComparison.OrdinalIgnoreCase));
                            
                            if (exeChild != null && nameChild != null)
                            {
                                list.Add(new SteamShortcutItem
                                {
                                    AppName = nameChild.StringValue,
                                    Exe = exeChild.StringValue,
                                    LaunchOptions = launchChild != null ? launchChild.StringValue : "",
                                    VdfPath = vdfPath,
                                    ShortcutElement = child,
                                    RootElement = root
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Failed to parse Steam shortcut file: " + vdfPath + ". Error: " + ex.Message);
            }
        }
        return list;
    }

    public static void SaveSteamShortcuts(List<SteamShortcutItem> itemsToSave)
    {
        var grouped = new Dictionary<string, VdfElement>();
        foreach (var item in itemsToSave)
        {
            if (!grouped.ContainsKey(item.VdfPath))
            {
                grouped[item.VdfPath] = item.RootElement;
            }
        }

        foreach (var pair in grouped)
        {
            string vdfPath = pair.Key;
            VdfElement root = pair.Value;

            try
            {
                // Create backup (only if a prior file exists — a freshly-selected account may
                // not have a shortcuts.vdf yet)
                if (File.Exists(vdfPath))
                {
                    string backupPath = vdfPath + ".bak";
                    File.Copy(vdfPath, backupPath, true);
                }

                // Serialize
                byte[] serializedBytes;
                using (var ms = new MemoryStream())
                using (var writer = new BinaryWriter(ms))
                {
                    writer.Write(root.Type);
                    WriteNullTerminatedString(writer, root.Name);
                    WriteMapContents(writer, root);
                    writer.Write((byte)0x08); // Close root map
                    writer.Write((byte)0x08); // Extra Steam trailing 0x08
                    serializedBytes = ms.ToArray();
                }

                // Ensure the account's config directory exists (fresh accounts have no
                // shortcuts.vdf yet, so their "config" folder may not exist either).
                string vdfDir = Path.GetDirectoryName(vdfPath);
                if (!string.IsNullOrEmpty(vdfDir))
                {
                    Directory.CreateDirectory(vdfDir);
                }

                File.WriteAllBytes(vdfPath, serializedBytes);
                Log("Successfully saved modified shortcuts.vdf to: " + vdfPath);
            }
            catch (Exception ex)
            {
                string msg = "Failed to write shortcuts.vdf to: " + vdfPath + ". Error: " + ex.Message;
                Log(msg);
                MessageBox.Show(msg, "Error Saving Shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    public static uint CalculateAppId(string appName, string exePath)
    {
        string combined = exePath + appName;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(combined);
        return ComputeCRC32(bytes) | 0x80000000;
    }

    private static uint ComputeCRC32(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        uint poly = 0xEDB88320; // Reversed IEEE polynomial
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            }
        }
        return ~crc;
    }

    /// <summary>
    /// Adds a UWP app as a new non-Steam shortcut pointing to the bridge executable.
    /// The shortcut's Exe = path to this bridge EXE, and LaunchOptions = "AUMID executable".
    /// </summary>
    public static void AddShortcutToSteam(string vdfPath, VdfElement root, string appName, string aumid, string executable)
    {
        string myExe = Process.GetCurrentProcess().MainModule.FileName;
        string myDir = AppDomain.CurrentDomain.BaseDirectory;

        // Calculate AppID
        uint appId = CalculateAppId(appName, myExe);

        // Find the next available index key for the new shortcut
        int nextIndex = 0;
        foreach (var child in root.Children)
        {
            int idx;
            if (int.TryParse(child.Name, out idx))
            {
                if (idx >= nextIndex) nextIndex = idx + 1;
            }
        }

        // Build the new shortcut entry
        VdfElement shortcut = new VdfElement();
        shortcut.Type = 0x00; // Map
        shortcut.Name = nextIndex.ToString();

        // appid
        shortcut.Children.Add(new VdfElement { Type = 0x02, Name = "appid", IntValue = (int)appId });

        // AppName
        shortcut.Children.Add(new VdfElement { Type = 0x01, Name = "AppName", StringValue = appName });

        // Exe — points to this bridge
        shortcut.Children.Add(new VdfElement { Type = 0x01, Name = "Exe", StringValue = myExe });

        // StartDir
        shortcut.Children.Add(new VdfElement { Type = 0x01, Name = "StartDir", StringValue = myDir.TrimEnd('\\') });

        // icon — initially empty, will be set by artwork download if available
        shortcut.Children.Add(new VdfElement { Type = 0x01, Name = "icon", StringValue = "" });

        // ShortcutPath — empty
        shortcut.Children.Add(new VdfElement { Type = 0x01, Name = "ShortcutPath", StringValue = "" });

        // LaunchOptions — AUMID followed by executable (UWPHook-compatible format)
        string launchOptions = string.IsNullOrEmpty(executable) ? aumid : aumid + " " + executable;
        shortcut.Children.Add(new VdfElement { Type = 0x01, Name = "LaunchOptions", StringValue = launchOptions });

        // IsHidden = 0
        shortcut.Children.Add(new VdfElement { Type = 0x02, Name = "IsHidden", IntValue = 0 });

        // AllowDesktopConfig = 1
        shortcut.Children.Add(new VdfElement { Type = 0x02, Name = "AllowDesktopConfig", IntValue = 1 });

        // AllowOverlay = 1
        shortcut.Children.Add(new VdfElement { Type = 0x02, Name = "AllowOverlay", IntValue = 1 });

        // OpenVR = 0
        shortcut.Children.Add(new VdfElement { Type = 0x02, Name = "OpenVR", IntValue = 0 });

        // LastPlayTime = 0
        shortcut.Children.Add(new VdfElement { Type = 0x02, Name = "LastPlayTime", IntValue = 0 });

        // tags — empty map
        shortcut.Children.Add(new VdfElement { Type = 0x00, Name = "tags" });

        root.Children.Add(shortcut);

        // Download artwork if API Key is configured
        if (!string.IsNullOrEmpty(Program.sgdbApiKey))
        {
            DownloadSteamGridArtwork(vdfPath, appName, appId, root);
        }
    }

    /// <summary>
    /// Removes a shortcut entry from the root VDF element.
    /// After calling this, call SaveSteamShortcuts to write changes to disk.
    /// </summary>
    public static void RemoveShortcutFromSteam(VdfElement root, VdfElement shortcutToRemove)
    {
        root.Children.Remove(shortcutToRemove);
    }

    /// <summary>
    /// Triggers SteamGridDB artwork download for existing shortcuts that have an empty icon field.
    /// Called on startup so existing shortcuts get proper icons without needing to be re-added.
    /// </summary>
    public static void RefreshExistingArtwork(string vdfPath, VdfElement root, List<SteamShortcutItem> shortcuts)
    {
        if (string.IsNullOrEmpty(sgdbApiKey)) return;

        string myExe = Process.GetCurrentProcess().MainModule.FileName;
        foreach (var item in shortcuts)
        {
            if (item.ShortcutElement == null) continue;
            var iconEl = item.ShortcutElement.Children.Find(c => c.Name == "icon");
            if (iconEl != null && !string.IsNullOrEmpty(iconEl.StringValue)) continue;

            string trimmedExe = item.Exe.Replace("\"", "").Trim();
            bool isBridge = trimmedExe.EndsWith("sBridge.exe", StringComparison.OrdinalIgnoreCase) ||
                            trimmedExe.Equals(myExe, StringComparison.OrdinalIgnoreCase);
            bool isEpicGame = !string.IsNullOrEmpty(item.LaunchOptions) && item.LaunchOptions.StartsWith("epic:", StringComparison.OrdinalIgnoreCase);
            if (!isBridge && !isEpicGame) continue;

            string appName = item.AppName;
            uint appId = CalculateAppId(appName, myExe);
            DownloadSteamGridArtwork(vdfPath, appName, appId, root);
        }
    }

    private static readonly object _sgdbLock = new object();

    public static void DownloadSteamGridArtwork(string vdfPath, string appName, uint appId, VdfElement root)
    {
        System.Threading.ThreadPool.QueueUserWorkItem((state) =>
        {
            try
            {
                Log(string.Format("SteamGridDB download started for: {0} (AppID: {1})", appName, appId));
                string gridDir = Path.Combine(Path.GetDirectoryName(vdfPath), "grid");
                if (!Directory.Exists(gridDir))
                {
                    Directory.CreateDirectory(gridDir);
                }

                using (var client = new System.Net.WebClient())
                {
                    client.Headers.Add("Authorization", "Bearer " + Program.sgdbApiKey);
                    client.Headers.Add("User-Agent", "sBridge/1.0");
                    client.Encoding = Encoding.UTF8;

                    string gameId = null;

                    // 1. Search for game to get SteamGridDB Game ID (with retry)
                    lock (_sgdbLock)
                    {
                        for (int retry = 0; retry < 3; retry++)
                        {
                            try
                            {
                                string searchUrl = "https://www.steamgriddb.com/api/v2/search/autocomplete/" + Uri.EscapeDataString(appName);
                                string searchJson = client.DownloadString(searchUrl);
                                var idMatch = System.Text.RegularExpressions.Regex.Match(searchJson, @"\""id\""\s*:\s*(\d+)");
                                if (idMatch.Success)
                                {
                                    gameId = idMatch.Groups[1].Value;
                                    break;
                                }
                                else
                                {
                                    Log("No matching game found on SteamGridDB for: " + appName);
                                    return;
                                }
                            }
                            catch (System.Net.WebException wex)
                            {
                                if (wex.Message.Contains("429"))
                                {
                                    Log(string.Format("Search rate limited for {0}, retry {1}/3...", appName, retry + 1));
                                    System.Threading.Thread.Sleep(2000 * (retry + 1));
                                }
                                else
                                {
                                    Log("SteamGridDB search failed for " + appName + ": " + wex.Message);
                                    return;
                                }
                            }
                        }
                    }

                    if (gameId == null) return;
                    Log(string.Format("Found SteamGridDB game ID: {0} for: {1}", gameId, appName));

                    // 2. Fetch & Download Grids (Portrait)
                    string gridBasePath = Path.Combine(gridDir, appId.ToString());
                    lock (_sgdbLock)
                    {
                        DownloadAsset(client, "https://www.steamgriddb.com/api/v2/grids/game/" + gameId + "?dimensions=600x900,342x482,660x930", gridBasePath);
                    }

                    // Copy grid to both {appid}.png and {appid}p.png so Steam finds it
                    string gridNoExt = Path.Combine(gridDir, appId.ToString());
                    string gridPng = gridNoExt + ".png";
                    string gridPPng = gridNoExt + "p.png";
                    if (!File.Exists(gridPng))
                    {
                        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp" })
                        {
                            if (File.Exists(gridNoExt + ext))
                            {
                                File.Copy(gridNoExt + ext, gridPng, true);
                                break;
                            }
                        }
                    }
                    if (File.Exists(gridPng) && !File.Exists(gridPPng))
                    {
                        File.Copy(gridPng, gridPPng, true);
                    }

                    // 3. Update the shortcut's icon field to point to the downloaded grid (fall back to icon later)
                    string gridImage = Path.Combine(gridDir, appId + ".png");
                    if (File.Exists(gridImage))
                    {
                        foreach (var child in root.Children)
                        {
                            var appNameEl = child.Children.Find(c => c.Name == "AppName");
                            if (appNameEl != null && appNameEl.StringValue == appName)
                            {
                                var iconEl = child.Children.Find(c => c.Name == "icon");
                                if (iconEl != null)
                                {
                                    iconEl.StringValue = gridImage;
                                }
                                break;
                            }
                        }
                        var saveItem = new SteamShortcutItem { VdfPath = vdfPath, RootElement = root };
                        SaveSteamShortcuts(new List<SteamShortcutItem> { saveItem });
                        Log("Updated icon field for: " + appName);
                    }

                    // 4. Fetch & Download Heroes (best effort)
                    try
                    {
                        lock (_sgdbLock)
                        {
                            DownloadAsset(client, "https://www.steamgriddb.com/api/v2/heroes/game/" + gameId, Path.Combine(gridDir, appId + "_hero"));
                        }
                    }
                    catch (Exception) { }

                    // 5. Fetch & Download Logos (best effort)
                    try
                    {
                        lock (_sgdbLock)
                        {
                            DownloadAsset(client, "https://www.steamgriddb.com/api/v2/logos/game/" + gameId, Path.Combine(gridDir, appId + "_logo"));
                        }
                    }
                    catch (Exception) { }

                    // 6. Fetch & Download Icons (best effort, update icon field if found)
                    try
                    {
                        lock (_sgdbLock)
                        {
                            DownloadAsset(client, "https://www.steamgriddb.com/api/v2/icons/game/" + gameId, Path.Combine(gridDir, appId + "-icon"), true);
                        }
                        string iconFile = Path.Combine(gridDir, appId + "-icon.ico");
                        if (File.Exists(iconFile))
                        {
                            foreach (var child in root.Children)
                            {
                                var appNameEl = child.Children.Find(c => c.Name == "AppName");
                                if (appNameEl != null && appNameEl.StringValue == appName)
                                {
                                    var iconEl = child.Children.Find(c => c.Name == "icon");
                                    if (iconEl != null)
                                    {
                                        iconEl.StringValue = iconFile;
                                    }
                                    break;
                                }
                            }
                            var saveItem = new SteamShortcutItem { VdfPath = vdfPath, RootElement = root };
                            SaveSteamShortcuts(new List<SteamShortcutItem> { saveItem });
                            Log("Updated icon field to icon for: " + appName);
                        }
                    }
                    catch (Exception) { }

                    Log("SteamGridDB artwork download completed for: " + appName);
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("Failed to download SteamGridDB artwork for {0}: {1}", appName, ex.Message));
            }
        });
    }

    private static void DownloadAsset(System.Net.WebClient client, string apiUrl, string targetPathWithoutExt, bool isIcon = false)
    {
        for (int retry = 0; retry < 3; retry++)
        {
            try
            {
                string json = client.DownloadString(apiUrl);
                var urlMatch = System.Text.RegularExpressions.Regex.Match(json, @"\""url\""\s*:\s*\""([^\""]+)""");
                if (urlMatch.Success)
                {
                    string imageUrl = urlMatch.Groups[1].Value.Replace("\\/", "/");
                    string ext = Path.GetExtension(imageUrl);
                    if (string.IsNullOrEmpty(ext) || ext.Contains("?") || ext.Contains("&"))
                    {
                        ext = ".png";
                    }

                    string destFile = targetPathWithoutExt + (isIcon ? ".ico" : ext);
                    Log("Downloading image: " + imageUrl + " -> " + destFile);
                    if (isIcon)
                    {
                        client.DownloadFile(imageUrl, destFile);
                    }
                    else
                    {
                        client.DownloadFile(imageUrl, destFile);
                    }
                    return;
                }
                return;
            }
            catch (System.Net.WebException wex)
            {
                if (wex.Message.Contains("429"))
                {
                    Log(string.Format("Rate limited on {0}, retry {1}/3...", apiUrl, retry + 1));
                    System.Threading.Thread.Sleep(2000 * (retry + 1));
                }
                else
                {
                    Log(string.Format("Failed to download asset from {0}: {1}", apiUrl, wex.Message));
                    return;
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("Failed to download asset from {0}: {1}", apiUrl, ex.Message));
                return;
            }
        }
        Log(string.Format("Failed to download asset from {0} after 3 retries", apiUrl));
    }
}
