// UWPHook-SISR Bridge
// Copyright (C) 2026 jxsparrou
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using System.Drawing;
using System.Text;
using System.Collections.Generic;

class Program
{
    static string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uwphook-bridge.cfg");
    static string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uwphook-bridge.log");

    static string sisrPath = "";
    static string sisrArguments = "";
    static string uwpHookPath = "";
    static bool logEnabled = true;

    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            LoadConfig();

            if (args.Length == 0)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new SettingsForm());
                return;
            }

            // Reconstruct the forwarded arguments string
            string forwardedArgs = "";
            string[] quotedArgs = new string[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                quotedArgs[i] = args[i].Contains(" ") ? "\"" + args[i] + "\"" : args[i];
            }
            forwardedArgs = string.Join(" ", quotedArgs);

            Log(string.Format("Bridge started with arguments: {0}", forwardedArgs));

            // Validate executable paths
            if (!File.Exists(sisrPath))
            {
                string msg = string.Format("SISR executable not found at: {0}\n\nPlease check the path in your config file:\n{1}", sisrPath, configPath);
                Log(msg);
                MessageBox.Show(msg, "UWPHook Bridge Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!File.Exists(uwpHookPath))
            {
                string msg = string.Format("UWPHook executable not found at: {0}\n\nPlease check the path in your config file:\n{1}", uwpHookPath, configPath);
                Log(msg);
                MessageBox.Show(msg, "UWPHook Bridge Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Terminate any existing SISR and VIIPER instances to avoid conflicts and start clean
            KillBackgroundProcesses();

            // Start SISR
            Log(string.Format("Launching SISR: {0} {1}", sisrPath, sisrArguments));
            ProcessStartInfo sisrInfo = new ProcessStartInfo(sisrPath, sisrArguments);
            sisrInfo.UseShellExecute = false;
            sisrInfo.CreateNoWindow = true;
            Process sisrProcess = Process.Start(sisrInfo);

            // Start UWPHook
            Log(string.Format("Launching UWPHook: {0} {1}", uwpHookPath, forwardedArgs));
            ProcessStartInfo uwpHookInfo = new ProcessStartInfo(uwpHookPath, forwardedArgs);
            uwpHookInfo.UseShellExecute = false;
            Process uwpHookProcess = Process.Start(uwpHookInfo);

            if (uwpHookProcess == null)
            {
                string msg = "Failed to launch UWPHook.";
                Log(msg);
                MessageBox.Show(msg, "UWPHook Bridge Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                KillBackgroundProcesses();
                return;
            }

            // Wait for UWPHook to finish (which finishes when the game is closed)
            Log("Waiting for UWPHook to exit...");
            uwpHookProcess.WaitForExit();
            Log(string.Format("UWPHook exited with code {0}", uwpHookProcess.ExitCode));

            // Terminate SISR and VIIPER
            Log("Terminating SISR and VIIPER...");
            KillBackgroundProcesses();

            Log("Bridge exiting successfully.");
        }
        catch (Exception ex)
        {
            string msg = string.Format("An error occurred: {0}\n\nStack Trace:\n{1}", ex.Message, ex.StackTrace);
            Log(msg);
            MessageBox.Show(msg, "UWPHook Bridge Exception", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static void KillBackgroundProcesses()
    {
        KillProcessesByName("SISR");
        KillProcessesByName("viiper");
    }

    static void KillProcessesByName(string name)
    {
        try
        {
            Process[] processes = Process.GetProcessesByName(name);
            foreach (Process p in processes)
            {
                try
                {
                    Log(string.Format("Killing running {0} process (PID: {1})", name, p.Id));
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Log(string.Format("Failed to kill {0} process: {1}", name, ex.Message));
                }
            }
        }
        catch (Exception ex)
        {
            Log(string.Format("Error searching for {0} processes: {1}", name, ex.Message));
        }
    }

    static void LoadConfig()
    {
        // Set default values first
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        // Guesses for SISR
        sisrPath = Path.Combine(localAppData, @"SISR\SISR.exe");
        if (!File.Exists(sisrPath))
        {
            string alternative = Path.Combine(localAppData, @"Programs\SISR\SISR.exe");
            if (File.Exists(alternative)) sisrPath = alternative;
        }

        // Guesses for UWPHook
        uwpHookPath = Path.Combine(localAppData, @"Programs\UWPHook\UWPHook.exe");
        if (!File.Exists(uwpHookPath))
        {
            string alt1 = Path.Combine(localAppData, @"UWPHook\UWPHook.exe");
            if (File.Exists(alt1)) uwpHookPath = alt1;
            else
            {
                string alt2 = Path.Combine(programFilesX86, @"UWPHook\UWPHook.exe");
                if (File.Exists(alt2)) uwpHookPath = alt2;
                else
                {
                    string alt3 = Path.Combine(programFiles, @"UWPHook\UWPHook.exe");
                    if (File.Exists(alt3)) uwpHookPath = alt3;
                }
            }
        }

        sisrArguments = "";
        logEnabled = true;

        if (File.Exists(configPath))
        {
            try
            {
                string[] lines = File.ReadAllLines(configPath);
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("#") || trimmed.StartsWith(";")) continue; // comments

                    int eqIdx = trimmed.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        string key = trimmed.Substring(0, eqIdx).Trim();
                        string val = trimmed.Substring(eqIdx + 1).Trim();

                        if (key.Equals("SisrPath", StringComparison.OrdinalIgnoreCase))
                        {
                            sisrPath = val;
                        }
                        else if (key.Equals("SisrArguments", StringComparison.OrdinalIgnoreCase))
                        {
                            sisrArguments = val;
                        }
                        else if (key.Equals("UwpHookPath", StringComparison.OrdinalIgnoreCase))
                        {
                            uwpHookPath = val;
                        }
                        else if (key.Equals("LogEnabled", StringComparison.OrdinalIgnoreCase))
                        {
                            bool.TryParse(val, out logEnabled);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Fallback to defaults, but we can't log yet since config parsing failed
            }
        }
        else
        {
            // Write default config
            SaveConfig();
        }
    }

    static void SaveConfig()
    {
        try
        {
            using (StreamWriter sw = new StreamWriter(configPath))
            {
                sw.WriteLine("# UWPHook Bridge Configuration");
                sw.WriteLine("# Modify the paths below to match your installation");
                sw.WriteLine();
                sw.WriteLine("SisrPath=" + sisrPath);
                sw.WriteLine("SisrArguments=" + sisrArguments);
                sw.WriteLine("UwpHookPath=" + uwpHookPath);
                sw.WriteLine("LogEnabled=" + logEnabled.ToString().ToLower());
            }
        }
        catch
        {
            // Ignore write errors
        }
    }

    static void ShowUsage()
    {
        string message = string.Format(
            "UWPHook Bridge (Standalone Mode)\n" +
            "=================================\n" +
            "This program bridges SISR and UWPHook. When launched with a UWP App ID (AUMID), it starts SISR, launches the game via UWPHook, and stops SISR when the game closes.\n\n" +
            "Usage:\n" +
            "  uwphook-bridge.exe <AUMID>\n\n" +
            "Config File:\n" +
            "  {0}\n\n" +
            "Current Paths:\n" +
            "  SISR.exe: {1} (Exists: {2})\n" +
            "  UWPHook.exe: {3} (Exists: {4})\n\n" +
            "How to use in Steam:\n" +
            "1. Add your UWP games using UWPHook as normal.\n" +
            "2. In Steam, right-click the game shortcut -> Properties.\n" +
            "3. Change the Target field to point to this 'uwphook-bridge.exe' file.\n" +
            "4. Keep the AUMID argument (the long string of characters) in the Target or Launch Options as-is.\n\n" +
            "Click OK to open the configuration file folder.",
            configPath,
            sisrPath,
            File.Exists(sisrPath) ? "Yes" : "No",
            uwpHookPath,
            File.Exists(uwpHookPath) ? "Yes" : "No"
        );

        MessageBox.Show(message, "UWPHook Bridge Info", MessageBoxButtons.OK, MessageBoxIcon.Information);

        try
        {
            // Open the folder containing the config file
            Process.Start("explorer.exe", "/select,\"" + configPath + "\"");
        }
        catch
        {
            // Ignore if explorer fails to open
        }
    }

    static void Log(string message)
    {
        if (!logEnabled) return;
        try
        {
            string entry = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}\r\n", DateTime.Now, message);
            File.AppendAllText(logPath, entry);
        }
        catch
        {
            // Ignore logging errors
        }
    }

    // ==========================================
    // Binary VDF Parser & Serializer
    // ==========================================

    public class VdfElement
    {
        public byte Type { get; set; } // 0x00: Map, 0x01: String, 0x02: Int32, 0x08: End
        public string Name { get; set; }
        public string StringValue { get; set; }
        public int IntValue { get; set; }
        public List<VdfElement> Children { get; set; }

        public VdfElement()
        {
            Children = new List<VdfElement>();
        }
    }

    public class SteamShortcutItem
    {
        public string AppName { get; set; }
        public string Exe { get; set; }
        public string LaunchOptions { get; set; }
        public string VdfPath { get; set; }
        public VdfElement ShortcutElement { get; set; }
        public VdfElement RootElement { get; set; }
    }

    public static VdfElement ReadMap(BinaryReader reader, string name)
    {
        var element = new VdfElement();
        element.Type = 0x00;
        element.Name = name;
        while (true)
        {
            byte type = reader.ReadByte();
            if (type == 0x08)
            {
                break; // End of map
            }
            string key = ReadNullTerminatedString(reader);
            if (type == 0x01)
            {
                string val = ReadNullTerminatedString(reader);
                var child = new VdfElement();
                child.Type = 0x01;
                child.Name = key;
                child.StringValue = val;
                element.Children.Add(child);
            }
            else if (type == 0x02)
            {
                int val = reader.ReadInt32();
                var child = new VdfElement();
                child.Type = 0x02;
                child.Name = key;
                child.IntValue = val;
                element.Children.Add(child);
            }
            else if (type == 0x00)
            {
                element.Children.Add(ReadMap(reader, key));
            }
        }
        return element;
    }

    public static string ReadNullTerminatedString(BinaryReader reader)
    {
        List<byte> bytes = new List<byte>();
        while (true)
        {
            byte b = reader.ReadByte();
            if (b == 0)
                break;
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public static void WriteNullTerminatedString(BinaryWriter writer, string str)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(str);
        writer.Write(bytes);
        writer.Write((byte)0x00);
    }

    public static void WriteMapContents(BinaryWriter writer, VdfElement element)
    {
        foreach (var child in element.Children)
        {
            writer.Write(child.Type);
            WriteNullTerminatedString(writer, child.Name);
            if (child.Type == 0x01)
            {
                WriteNullTerminatedString(writer, child.StringValue);
            }
            else if (child.Type == 0x02)
            {
                writer.Write(child.IntValue);
            }
            else if (child.Type == 0x00)
            {
                WriteMapContents(writer, child);
                writer.Write((byte)0x08); // Close nested map
            }
        }
    }

    // ==========================================
    // Steam Path & Shortcut Automation
    // ==========================================

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

    // ==========================================
    // Configuration Settings Form
    // ==========================================

    public class SettingsForm : Form
    {
        private TextBox txtSisr;
        private TextBox txtSisrArgs;
        private TextBox txtUwp;
        private ListView lstGames;
        private Label lblSteamStatus;
        private Button btnCloseSteam;
        private Button btnBridgeSelected;
        private Button btnRestoreSelected;
        
        // Colors
        private Color bgDark = Color.FromArgb(28, 28, 30);
        private Color bgPanel = Color.FromArgb(36, 36, 40);
        private Color bgInput = Color.FromArgb(44, 44, 48);
        private Color textLight = Color.FromArgb(240, 240, 240);
        private Color textMuted = Color.FromArgb(170, 170, 170);
        private Color accentBlue = Color.FromArgb(0, 122, 204);
        private Color accentGreen = Color.FromArgb(46, 125, 50);
        private Color accentRed = Color.FromArgb(198, 40, 40);

        public SettingsForm()
        {
            this.Text = "UWPHook-SISR Bridge Settings";
            this.Size = new Size(680, 680);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.BackColor = bgDark;
            this.ForeColor = textLight;

            InitializeComponents();
            LoadPaths();
            RefreshShortcutsList();
        }

        private void InitializeComponents()
        {
            // Title
            Label lblTitle = new Label();
            lblTitle.Text = "UWPHook-SISR Bridge Settings";
            lblTitle.Font = new Font("Segoe UI", 14, FontStyle.Bold);
            lblTitle.ForeColor = Color.White;
            lblTitle.Location = new Point(15, 15);
            lblTitle.Size = new Size(400, 30);
            this.Controls.Add(lblTitle);

            // GroupBox - Path Settings
            GroupBox grpPaths = new GroupBox();
            grpPaths.Text = "Path Configurations";
            grpPaths.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            grpPaths.ForeColor = textLight;
            grpPaths.BackColor = bgPanel;
            grpPaths.Location = new Point(15, 55);
            grpPaths.Size = new Size(635, 190);
            this.Controls.Add(grpPaths);

            // Inside grpPaths: SISR Path
            Label lblSisr = new Label();
            lblSisr.Text = "SISR Path:";
            lblSisr.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblSisr.Location = new Point(15, 25);
            lblSisr.Size = new Size(100, 20);
            grpPaths.Controls.Add(lblSisr);

            txtSisr = new TextBox();
            txtSisr.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            txtSisr.BackColor = bgInput;
            txtSisr.ForeColor = Color.White;
            txtSisr.BorderStyle = BorderStyle.FixedSingle;
            txtSisr.Location = new Point(15, 45);
            txtSisr.Size = new Size(490, 23);
            grpPaths.Controls.Add(txtSisr);

            Button btnBrowseSisr = new Button();
            btnBrowseSisr.Text = "Browse...";
            btnBrowseSisr.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnBrowseSisr.FlatStyle = FlatStyle.Flat;
            btnBrowseSisr.FlatAppearance.BorderSize = 0;
            btnBrowseSisr.BackColor = Color.FromArgb(60, 60, 64);
            btnBrowseSisr.ForeColor = Color.White;
            btnBrowseSisr.Location = new Point(515, 44);
            btnBrowseSisr.Size = new Size(105, 25);
            btnBrowseSisr.Click += (s, e) => BrowseSisr();
            grpPaths.Controls.Add(btnBrowseSisr);

            // SISR Arguments
            Label lblSisrArgs = new Label();
            lblSisrArgs.Text = "SISR Arguments (passed when launching SISR):";
            lblSisrArgs.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblSisrArgs.Location = new Point(15, 75);
            lblSisrArgs.Size = new Size(300, 20);
            grpPaths.Controls.Add(lblSisrArgs);

            txtSisrArgs = new TextBox();
            txtSisrArgs.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            txtSisrArgs.BackColor = bgInput;
            txtSisrArgs.ForeColor = Color.White;
            txtSisrArgs.BorderStyle = BorderStyle.FixedSingle;
            txtSisrArgs.Location = new Point(15, 95);
            txtSisrArgs.Size = new Size(605, 23);
            grpPaths.Controls.Add(txtSisrArgs);

            // UWPHook Path
            Label lblUwp = new Label();
            lblUwp.Text = "UWPHook Path:";
            lblUwp.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblUwp.Location = new Point(15, 125);
            lblUwp.Size = new Size(100, 20);
            grpPaths.Controls.Add(lblUwp);

            txtUwp = new TextBox();
            txtUwp.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            txtUwp.BackColor = bgInput;
            txtUwp.ForeColor = Color.White;
            txtUwp.BorderStyle = BorderStyle.FixedSingle;
            txtUwp.Location = new Point(15, 145);
            txtUwp.Size = new Size(490, 23);
            grpPaths.Controls.Add(txtUwp);

            Button btnBrowseUwp = new Button();
            btnBrowseUwp.Text = "Browse...";
            btnBrowseUwp.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnBrowseUwp.FlatStyle = FlatStyle.Flat;
            btnBrowseUwp.FlatAppearance.BorderSize = 0;
            btnBrowseUwp.BackColor = Color.FromArgb(60, 60, 64);
            btnBrowseUwp.ForeColor = Color.White;
            btnBrowseUwp.Location = new Point(515, 144);
            btnBrowseUwp.Size = new Size(105, 25);
            btnBrowseUwp.Click += (s, e) => BrowseUwp();
            grpPaths.Controls.Add(btnBrowseUwp);

            // GroupBox - Steam Automation
            GroupBox grpSteam = new GroupBox();
            grpSteam.Text = "Steam Shortcut Redirection";
            grpSteam.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            grpSteam.ForeColor = textLight;
            grpSteam.BackColor = bgPanel;
            grpSteam.Location = new Point(15, 260);
            grpSteam.Size = new Size(635, 330);
            this.Controls.Add(grpSteam);

            // Steam status and Close Steam button
            lblSteamStatus = new Label();
            lblSteamStatus.Text = "Checking Steam status...";
            lblSteamStatus.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            lblSteamStatus.Location = new Point(15, 25);
            lblSteamStatus.Size = new Size(450, 20);
            grpSteam.Controls.Add(lblSteamStatus);

            btnCloseSteam = new Button();
            btnCloseSteam.Text = "Close Steam Client";
            btnCloseSteam.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnCloseSteam.FlatStyle = FlatStyle.Flat;
            btnCloseSteam.FlatAppearance.BorderSize = 0;
            btnCloseSteam.BackColor = accentRed;
            btnCloseSteam.ForeColor = Color.White;
            btnCloseSteam.Location = new Point(475, 20);
            btnCloseSteam.Size = new Size(145, 25);
            btnCloseSteam.Click += (s, e) => CloseSteam();
            grpSteam.Controls.Add(btnCloseSteam);

            // ListView
            lstGames = new ListView();
            lstGames.View = View.Details;
            lstGames.CheckBoxes = true;
            lstGames.FullRowSelect = true;
            lstGames.GridLines = false;
            lstGames.BackColor = bgInput;
            lstGames.ForeColor = Color.White;
            lstGames.BorderStyle = BorderStyle.FixedSingle;
            lstGames.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lstGames.Location = new Point(15, 55);
            lstGames.Size = new Size(605, 220);
            lstGames.Columns.Add("Game Name", 220);
            lstGames.Columns.Add("Current Target", 240);
            lstGames.Columns.Add("Status", 120);
            grpSteam.Controls.Add(lstGames);

            // Action Buttons
            btnBridgeSelected = new Button();
            btnBridgeSelected.Text = "Bridge Selected to EXE";
            btnBridgeSelected.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnBridgeSelected.FlatStyle = FlatStyle.Flat;
            btnBridgeSelected.FlatAppearance.BorderSize = 0;
            btnBridgeSelected.BackColor = accentBlue;
            btnBridgeSelected.ForeColor = Color.White;
            btnBridgeSelected.Location = new Point(15, 285);
            btnBridgeSelected.Size = new Size(180, 30);
            btnBridgeSelected.Click += (s, e) => ApplyBridge(true);
            grpSteam.Controls.Add(btnBridgeSelected);

            btnRestoreSelected = new Button();
            btnRestoreSelected.Text = "Restore Selected to UWP";
            btnRestoreSelected.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnRestoreSelected.FlatStyle = FlatStyle.Flat;
            btnRestoreSelected.FlatAppearance.BorderSize = 0;
            btnRestoreSelected.BackColor = Color.FromArgb(60, 60, 64);
            btnRestoreSelected.ForeColor = Color.White;
            btnRestoreSelected.Location = new Point(205, 285);
            btnRestoreSelected.Size = new Size(180, 30);
            btnRestoreSelected.Click += (s, e) => ApplyBridge(false);
            grpSteam.Controls.Add(btnRestoreSelected);

            Button btnRefresh = new Button();
            btnRefresh.Text = "Refresh";
            btnRefresh.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnRefresh.FlatStyle = FlatStyle.Flat;
            btnRefresh.FlatAppearance.BorderSize = 0;
            btnRefresh.BackColor = Color.FromArgb(60, 60, 64);
            btnRefresh.ForeColor = Color.White;
            btnRefresh.Location = new Point(520, 285);
            btnRefresh.Size = new Size(100, 30);
            btnRefresh.Click += (s, e) => RefreshShortcutsList();
            grpSteam.Controls.Add(btnRefresh);

            // Bottom Save Configuration Button
            Button btnSave = new Button();
            btnSave.Text = "Save Path Config & Close";
            btnSave.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            btnSave.FlatStyle = FlatStyle.Flat;
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.BackColor = accentBlue;
            btnSave.ForeColor = Color.White;
            btnSave.Location = new Point(15, 600);
            btnSave.Size = new Size(635, 35);
            btnSave.Click += (s, e) => SavePathsAndClose();
            this.Controls.Add(btnSave);
        }

        private void LoadPaths()
        {
            txtSisr.Text = Program.sisrPath;
            txtSisrArgs.Text = Program.sisrArguments;
            txtUwp.Text = Program.uwpHookPath;
        }

        private void SavePaths()
        {
            Program.sisrPath = txtSisr.Text.Trim();
            Program.sisrArguments = txtSisrArgs.Text.Trim();
            Program.uwpHookPath = txtUwp.Text.Trim();
            Program.SaveConfig();
        }

        private void SavePathsAndClose()
        {
            SavePaths();
            this.Close();
        }

        private void BrowseSisr()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "Executables (*.exe)|*.exe|All Files (*.*)|*.*";
                ofd.Title = "Select SISR.exe";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    txtSisr.Text = ofd.FileName;
                }
            }
        }

        private void BrowseUwp()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "Executables (*.exe)|*.exe|All Files (*.*)|*.*";
                ofd.Title = "Select UWPHook.exe";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    txtUwp.Text = ofd.FileName;
                }
            }
        }

        private void CloseSteam()
        {
            try
            {
                Process[] processes = Process.GetProcessesByName("steam");
                if (processes.Length > 0)
                {
                    foreach (var p in processes)
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                    MessageBox.Show("Steam client has been closed.", "Steam Closed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to close Steam: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            RefreshShortcutsList();
        }

        private List<SteamShortcutItem> currentShortcuts = new List<SteamShortcutItem>();

        private void RefreshShortcutsList()
        {
            // Check Steam status
            bool steamRunning = Process.GetProcessesByName("steam").Length > 0;
            if (steamRunning)
            {
                lblSteamStatus.Text = "⚠️ Warning: Steam is running! Close it before editing shortcuts.";
                lblSteamStatus.ForeColor = accentRed;
                btnCloseSteam.Visible = true;
            }
            else
            {
                lblSteamStatus.Text = "✔️ Steam is not running. Safe to edit shortcuts.";
                lblSteamStatus.ForeColor = accentGreen;
                btnCloseSteam.Visible = false;
            }

            lstGames.Items.Clear();
            
            // Save paths first in memory so we can compare correctly
            Program.sisrPath = txtSisr.Text.Trim();
            Program.uwpHookPath = txtUwp.Text.Trim();

            currentShortcuts = Program.LoadSteamShortcuts();
            string myExe = Process.GetCurrentProcess().MainModule.FileName;

            foreach (var item in currentShortcuts)
            {
                string trimmedExe = item.Exe.Replace("\"", "").Trim();
                bool isUWPHook = trimmedExe.EndsWith("UWPHook.exe", StringComparison.OrdinalIgnoreCase);
                bool isBridge = trimmedExe.EndsWith("uwphook-bridge.exe", StringComparison.OrdinalIgnoreCase);

                // Show only items related to UWP / UWPHook or already bridged
                if (isUWPHook || isBridge)
                {
                    ListViewItem lvItem = new ListViewItem(item.AppName);
                    lvItem.SubItems.Add(Path.GetFileName(item.Exe));
                    lvItem.SubItems.Add(isBridge ? "Bridged via Bridge" : "Direct via UWPHook");
                    lvItem.Tag = item;
                    lvItem.Checked = isUWPHook || isBridge; // Checked by default if relevant
                    lstGames.Items.Add(lvItem);
                }
            }
            
            if (lstGames.Items.Count == 0)
            {
                ListViewItem emptyItem = new ListViewItem("No UWP/UWPHook games found in Steam shortcuts.");
                emptyItem.SubItems.Add("");
                emptyItem.SubItems.Add("");
                lstGames.Items.Add(emptyItem);
            }
        }

        private void ApplyBridge(bool enableBridge)
        {
            bool steamRunning = Process.GetProcessesByName("steam").Length > 0;
            if (steamRunning)
            {
                DialogResult res = MessageBox.Show(
                    "Steam is currently running. If you edit shortcuts while Steam is open, Steam will overwrite your changes when it closes.\n\n" +
                    "Would you like to close Steam and proceed?",
                    "Steam is Running",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (res == DialogResult.Yes)
                {
                    CloseSteam();
                }
                else
                {
                    return;
                }
            }

            SavePaths(); // Save paths before starting modifications

            string myExe = Process.GetCurrentProcess().MainModule.FileName;
            string myDir = AppDomain.CurrentDomain.BaseDirectory;
            string uwpHookDir = string.IsNullOrEmpty(Program.uwpHookPath) ? "" : Path.GetDirectoryName(Program.uwpHookPath);

            List<SteamShortcutItem> modifiedItems = new List<SteamShortcutItem>();
            int count = 0;

            foreach (ListViewItem lvItem in lstGames.Items)
            {
                var item = lvItem.Tag as SteamShortcutItem;
                if (lvItem.Checked && item != null)
                {
                    var exeChild = item.ShortcutElement.Children.Find(c => c.Name.Equals("Exe", StringComparison.OrdinalIgnoreCase));
                    var startDirChild = item.ShortcutElement.Children.Find(c => c.Name.Equals("StartDir", StringComparison.OrdinalIgnoreCase));

                    string targetExe = enableBridge ? myExe : Program.uwpHookPath;
                    string targetStartDir = enableBridge ? myDir : uwpHookDir;

                    if (exeChild != null && !exeChild.StringValue.Equals(targetExe, StringComparison.OrdinalIgnoreCase))
                    {
                        exeChild.StringValue = targetExe;
                        if (startDirChild != null)
                        {
                            startDirChild.StringValue = targetStartDir;
                        }
                        modifiedItems.Add(item);
                        count++;
                    }
                }
            }

            if (count > 0)
            {
                Program.SaveSteamShortcuts(modifiedItems);
                MessageBox.Show(string.Format("Successfully updated {0} Steam shortcuts!", count), "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("No changes were needed for the selected games.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            RefreshShortcutsList();
        }
    }
}
