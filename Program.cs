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

partial class Program
{
    static string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sBridge.cfg");
    static string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sBridge.log");

    static string sisrPath = "";
    static string sisrArguments = "";
    static bool logEnabled = true;
    public static bool sisrEnabled = true;
    public static string sgdbApiKey = "";

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

            // Parse arguments: args[0] = AUMID, args[1] = executable (optional), args[2+] = extra args
            string aumid = args[0];
            string executableHint = args.Length > 1 ? args[1] : "";
            string extraArgs = "";
            if (args.Length > 2)
            {
                string[] extraParts = new string[args.Length - 2];
                Array.Copy(args, 2, extraParts, 0, args.Length - 2);
                extraArgs = string.Join(" ", extraParts);
            }

            // Fix forward slashes in executable hint (UWPHook convention)
            if (!string.IsNullOrEmpty(executableHint) && executableHint.Contains("/"))
            {
                executableHint = executableHint.Replace('/', '\\');
            }

            Log(string.Format("Bridge started: AUMID={0}, Executable={1}, ExtraArgs={2}, SISR={3}", aumid, executableHint, extraArgs, sisrEnabled));

            if (sisrEnabled)
            {
                // Validate SISR path
                if (!File.Exists(sisrPath))
                {
                    string msg = string.Format("SISR executable not found at: {0}\n\nPlease check the path in your config file:\n{1}", sisrPath, configPath);
                    Log(msg);
                    MessageBox.Show(msg, "SISR Integration Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Terminate any existing SISR and VIIPER instances to avoid conflicts and start clean
                KillBackgroundProcesses();

                // Start SISR
                Log(string.Format("Launching SISR: {0} {1}", sisrPath, sisrArguments));
                ProcessStartInfo sisrInfo = new ProcessStartInfo(sisrPath, sisrArguments);
                sisrInfo.UseShellExecute = false;
                sisrInfo.CreateNoWindow = true;
                Process.Start(sisrInfo);
            }

            // Launch UWP app directly via COM
            int gamePid = LaunchUWPApp(aumid, extraArgs);

            // Wait for the game to exit
            WaitForUWPAppExit(gamePid, executableHint);

            if (sisrEnabled)
            {
                // Terminate SISR and VIIPER
                Log("Terminating SISR and VIIPER...");
                KillBackgroundProcesses();
            }

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

    public static void LoadConfig()
    {
        // Set default values first
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Guesses for SISR
        sisrPath = Path.Combine(localAppData, @"SISR\SISR.exe");
        if (!File.Exists(sisrPath))
        {
            string alternative = Path.Combine(localAppData, @"Programs\SISR\SISR.exe");
            if (File.Exists(alternative)) sisrPath = alternative;
        }

        sisrArguments = "";
        logEnabled = true;
        sisrEnabled = true;
        sgdbApiKey = "";

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
                        else if (key.Equals("LogEnabled", StringComparison.OrdinalIgnoreCase))
                        {
                            bool.TryParse(val, out logEnabled);
                        }
                        else if (key.Equals("SisrEnabled", StringComparison.OrdinalIgnoreCase))
                        {
                            bool.TryParse(val, out sisrEnabled);
                        }
                        else if (key.Equals("SgdbApiKey", StringComparison.OrdinalIgnoreCase))
                        {
                            sgdbApiKey = val;
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

    public static void SaveConfig()
    {
        try
        {
            using (StreamWriter sw = new StreamWriter(configPath))
            {
                sw.WriteLine("# Controller Bridge Configuration");
                sw.WriteLine("# Modify the paths below to match your installation");
                sw.WriteLine();
                sw.WriteLine("SisrPath=" + sisrPath);
                sw.WriteLine("SisrArguments=" + sisrArguments);
                sw.WriteLine("SisrEnabled=" + sisrEnabled.ToString().ToLower());
                sw.WriteLine("SgdbApiKey=" + sgdbApiKey);
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
            "Controller Bridge (UWP Mode)\n" +
            "=============================\n" +
            "This program launches UWP games directly. When launched with a UWP App ID (AUMID), it optionally runs SISR in the background, starts the game, and cleans up when the game closes.\n\n" +
            "Usage:\n" +
            "  sBridge.exe <AUMID> [executable_hint]\n\n" +
            "Config File:\n" +
            "  {0}\n\n" +
            "Current Paths:\n" +
            "  SISR.exe: {1} (Exists: {2}, Enabled: {3})\n\n" +
            "How to use in Steam:\n" +
            "1. Use the Settings GUI to scan for UWP apps and add them to Steam.\n" +
            "2. Alternatively, manually add a shortcut pointing to this 'sBridge.exe' file, passing the AUMID as an argument.\n\n" +
            "Click OK to open the configuration file folder.",
            configPath,
            sisrPath,
            File.Exists(sisrPath) ? "Yes" : "No",
            sisrEnabled ? "Yes" : "No"
        );

        MessageBox.Show(message, "Controller Bridge Info", MessageBoxButtons.OK, MessageBoxIcon.Information);

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
}
