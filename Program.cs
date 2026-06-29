using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

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
                ShowUsage();
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
}
