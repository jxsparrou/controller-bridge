using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

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

    /// <summary>
    /// Adds a UWP app as a new non-Steam shortcut pointing to the bridge executable.
    /// The shortcut's Exe = path to this bridge EXE, and LaunchOptions = "AUMID executable".
    /// </summary>
    public static void AddShortcutToSteam(string vdfPath, VdfElement root, string appName, string aumid, string executable)
    {
        string myExe = Process.GetCurrentProcess().MainModule.FileName;
        string myDir = AppDomain.CurrentDomain.BaseDirectory;

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
    }

    /// <summary>
    /// Removes a shortcut entry from the root VDF element.
    /// After calling this, call SaveSteamShortcuts to write changes to disk.
    /// </summary>
    public static void RemoveShortcutFromSteam(VdfElement root, VdfElement shortcutToRemove)
    {
        root.Children.Remove(shortcutToRemove);
    }
}
