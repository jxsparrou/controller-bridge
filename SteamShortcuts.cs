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

    // === Steam path & shortcut methods ===
    public static List<string> FindShortcutsVdfFiles()
    {
        var paths = new List<string>();
        try
        {
            string steamPath = null;
            
            // Try HKCU first
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                if (key != null)
                {
                    var val = key.GetValue("SteamPath");
                    if (val != null) steamPath = val.ToString();
                }
            }

            // Try HKLM if not found
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

        // Fallback: Check typical paths if none found
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
                catch {}
            }
        }

        return paths;
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
                // Create backup
                string backupPath = vdfPath + ".bak";
                File.Copy(vdfPath, backupPath, true);

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
        // Concatenate raw Exe value (with quotes as written to VDF) and AppName
        string combined = "\"" + exePath + "\"" + appName;
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
        shortcut.Children.Add(new VdfElement { Type = 0x01, Name = "Exe", StringValue = "\"" + myExe + "\"" });

        // StartDir
        shortcut.Children.Add(new VdfElement { Type = 0x01, Name = "StartDir", StringValue = "\"" + myDir.TrimEnd('\\') + "\"" });

        // icon — empty
        shortcut.Children.Add(new VdfElement { Type = 0x01, Name = "icon", StringValue = "" });

        // ShortcutPath — empty
        shortcut.Children.Add(new VdfElement { Type = 0x01, Name = "ShortcutPath", StringValue = "" });

        // LaunchOptions — AUMID followed by executable (UWPHook-compatible format)
        string launchOptions = aumid + " " + executable;
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
            DownloadSteamGridArtwork(vdfPath, appName, appId);
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

    public static void DownloadSteamGridArtwork(string vdfPath, string appName, uint appId)
    {
        System.Threading.ThreadPool.QueueUserWorkItem((state) =>
        {
            try
            {
                Log(string.Format("SteamGridDB download started for: {0} (AppID: {1})", appName, appId));
                string gridDir = Path.Combine(Path.Combine(Path.GetDirectoryName(vdfPath)), "grid");
                if (!Directory.Exists(gridDir))
                {
                    Directory.CreateDirectory(gridDir);
                }

                using (var client = new System.Net.WebClient())
                {
                    client.Headers.Add("Authorization", "Bearer " + Program.sgdbApiKey);
                    client.Encoding = Encoding.UTF8;

                    // 1. Search for game to get SteamGridDB Game ID
                    string searchUrl = "https://www.steamgriddb.com/api/v2/search/autocomplete/" + Uri.EscapeDataString(appName);
                    string searchJson = client.DownloadString(searchUrl);
                    
                    var idMatch = System.Text.RegularExpressions.Regex.Match(searchJson, @"\""id\""\s*:\s*(\d+)");
                    if (!idMatch.Success)
                    {
                        Log("No matching game found on SteamGridDB for: " + appName);
                        return;
                    }
                    string gameId = idMatch.Groups[1].Value;
                    Log(string.Format("Found SteamGridDB game ID: {0} for: {1}", gameId, appName));

                    // 2. Fetch & Download Grids (Portrait)
                    DownloadAsset(client, "https://www.steamgriddb.com/api/v2/grids/game/" + gameId + "?dimensions=600x900,342x482,660x930", Path.Combine(gridDir, appId + "p"));

                    // 3. Fetch & Download Heroes
                    DownloadAsset(client, "https://www.steamgriddb.com/api/v2/heroes/game/" + gameId, Path.Combine(gridDir, appId + "_hero"));

                    // 4. Fetch & Download Logos
                    DownloadAsset(client, "https://www.steamgriddb.com/api/v2/logos/game/" + gameId, Path.Combine(gridDir, appId + "_logo"));

                    // 5. Fetch & Download Icons
                    DownloadAsset(client, "https://www.steamgriddb.com/api/v2/icons/game/" + gameId, Path.Combine(gridDir, appId + "_icon"));

                    Log("SteamGridDB artwork download completed for: " + appName);
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("Failed to download SteamGridDB artwork for {0}: {1}", appName, ex.Message));
            }
        });
    }

    private static void DownloadAsset(System.Net.WebClient client, string apiUrl, string targetPathWithoutExt)
    {
        try
        {
            string json = client.DownloadString(apiUrl);
            var urlMatch = System.Text.RegularExpressions.Regex.Match(json, @"\""url\""\s*:\s*\""([^\""]+)""");
            if (urlMatch.Success)
            {
                string imageUrl = urlMatch.Groups[1].Value;
                string ext = Path.GetExtension(imageUrl);
                if (string.IsNullOrEmpty(ext) || ext.Contains("?") || ext.Contains("&"))
                {
                    ext = ".png"; // Default fallback
                }
                
                string destFile = targetPathWithoutExt + ext;
                Log("Downloading image: " + imageUrl + " -> " + destFile);
                client.DownloadFile(imageUrl, destFile);
            }
        }
        catch (Exception ex)
        {
            Log(string.Format("Failed to download asset from {0}: {1}", apiUrl, ex.Message));
        }
    }
}
