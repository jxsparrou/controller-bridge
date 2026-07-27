using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

partial class Program
{
    // ==========================================
    // COM Interop for UWP App Activation
    // ==========================================

    public enum ActivateOptions
    {
        None = 0x00000000,
        DesignMode = 0x00000001,
        NoErrorUI = 0x00000002,
        NoSplashScreen = 0x00000004,
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IApplicationActivationManager
    {
        IntPtr ActivateApplication([In] String appUserModelId, [In] String arguments,
            [In] ActivateOptions options, [Out] out UInt32 processId);
        IntPtr ActivateForFile([In] String appUserModelId, [In] IntPtr itemArray,
            [In] String verb, [Out] out UInt32 processId);
        IntPtr ActivateForProtocol([In] String appUserModelId, [In] IntPtr itemArray,
            [Out] out UInt32 processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    public class ApplicationActivationManager : IApplicationActivationManager
    {
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern IntPtr ActivateApplication([In] String appUserModelId, [In] String arguments,
            [In] ActivateOptions options, [Out] out UInt32 processId);
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern IntPtr ActivateForFile([In] String appUserModelId, [In] IntPtr itemArray,
            [In] String verb, [Out] out UInt32 processId);
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern IntPtr ActivateForProtocol([In] String appUserModelId, [In] IntPtr itemArray,
            [Out] out UInt32 processId);
    }

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    // ==========================================
    // UWP App Launcher Method
    // ==========================================

    /// <summary>
    /// Launches a UWP app by its AUMID using the COM ApplicationActivationManager.
    /// Returns the process ID of the launched app.
    /// </summary>
    static int LaunchUWPApp(string aumid, string extraArgs)
    {
        Log("Launching UWP app via COM: AUMID=" + aumid + ", Args=" + extraArgs);
        var mgr = new ApplicationActivationManager();
        uint processId;

        try
        {
            mgr.ActivateApplication(aumid, extraArgs, ActivateOptions.None, out processId);
            Log("UWP app launched with PID: " + processId);

            if (processId != 0)
            {
                // Give the app a moment to initialize, then bring it to foreground
                System.Threading.Thread.Sleep(2000);
                try
                {
                    Process proc = Process.GetProcessById((int)processId);
                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        SetForegroundWindow(proc.MainWindowHandle);
                        Log("Brought process to foreground.");
                    }
                }
                catch (Exception ex)
                {
                    Log("Could not bring process to foreground: " + ex.Message);
                }
            }

            return (int)processId;
        }
        catch (Exception e)
        {
            string msg = "Failed to launch UWP app: " + e.Message;
            Log(msg);
            throw new Exception(msg, e);
        }
    }

    // ==========================================
    // Custom Game Launcher Method
    // ==========================================

    /// <summary>
    /// Launches a custom non-UWP game by its executable path.
    /// Returns the process ID of the launched app.
    /// </summary>
    static int LaunchCustomGame(string exePath, string extraArgs)
    {
        Log("Launching custom game: Path=" + exePath + ", Args=" + extraArgs);
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exePath, extraArgs);
            psi.WorkingDirectory = Path.GetDirectoryName(exePath);
            psi.UseShellExecute = true; // Support UAC elevation if needed
            Process proc = Process.Start(psi);
            if (proc != null)
            {
                Log("Custom game launched with PID: " + proc.Id);
                return proc.Id;
            }
            return 0;
        }
        catch (Exception e)
        {
            string msg = "Failed to launch custom game: " + e.Message;
            Log(msg);
            throw new Exception(msg, e);
        }
    }

    // ==========================================
    // Process Monitoring Method
    // ==========================================

    /// <summary>
    /// Extracts the bare executable name (no path, no extension) from a hint that may be
    /// a full path, a bare filename, or already just the name.
    /// </summary>
    internal static string ExtractExeName(string executableHint)
    {
        string exeName = executableHint;
        try
        {
            if (executableHint.Contains("\\") || executableHint.Contains("/"))
            {
                exeName = Path.GetFileNameWithoutExtension(executableHint);
            }
            else if (executableHint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                exeName = executableHint.Substring(0, executableHint.Length - 4);
            }
        }
        catch { }
        return exeName;
    }

    /// <summary>
    /// Searches for a running process matching either the given executable name or,
    /// as a fallback (for EAC/Epic games where the real process name differs from the
    /// launch hint), any running process whose path is under installDir. Retries with a
    /// delay between attempts. Returns 0 if nothing is found within the retry budget.
    /// </summary>
    internal static int FindMatchingProcess(string exeName, string installDir, int maxRetries, int retryDelayMs)
    {
        return FindMatchingProcess(exeName, installDir, maxRetries, retryDelayMs, null);
    }

    /// <summary>
    /// Overload that additionally constrains the installDir fallback match to processes that
    /// started at or after <paramref name="minStartTime"/> (when a candidate's StartTime can be
    /// read at all). This prevents a long-lived, unrelated helper process (anti-cheat service,
    /// crash reporter, updater) still running under the game's install directory from being
    /// mistaken for a genuine launcher handoff once the actual game has already exited.
    /// </summary>
    internal static int FindMatchingProcess(string exeName, string installDir, int maxRetries, int retryDelayMs, DateTime? minStartTime)
    {
        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                Process[] candidates = Process.GetProcessesByName(exeName);
                if (candidates.Length > 0)
                {
                    return candidates[0].Id;
                }
            }
            catch (Exception ex)
            {
                Log("Error searching for process by name: " + ex.Message);
            }

            if (!string.IsNullOrEmpty(installDir))
            {
                try
                {
                    var pathPids = FindProcessesByInstallPath(installDir, minStartTime);
                    if (pathPids.Count > 0)
                    {
                        return pathPids[0];
                    }
                }
                catch { }
            }

            if (retry < maxRetries - 1)
            {
                System.Threading.Thread.Sleep(retryDelayMs);
            }
        }
        return 0;
    }

    /// <summary>
    /// Waits for the game to exit by polling its process ID.
    /// If the tracked process exits, searches for a replacement (handles launcher-style
    /// apps that hand off to a separate game process). This search runs on every exit for
    /// the life of the session, not just the first one — multi-stage launchers (e.g. an
    /// Epic Games launcher stub handing off to an anti-cheat bootstrapper, which then hands
    /// off to the actual game process) can legitimately go through more than one handoff.
    /// installDir is used as a fallback match for Epic games where the actual process name
    /// differs from LaunchExecutable.
    /// </summary>
    static void WaitForGameExit(int processId, string executableHint, string installDir)
    {
        const int pollIntervalMs = 3000;
        string exeName = !string.IsNullOrEmpty(executableHint) ? ExtractExeName(executableHint) : "";

        if (processId == 0)
        {
            if (string.IsNullOrEmpty(executableHint))
            {
                Log("Warning: Process returned PID 0 with no executable hint. Cannot monitor.");
                return;
            }

            Log("Process ID is 0 but executable hint provided. Waiting for game process to appear: " + executableHint);
            processId = FindMatchingProcess(exeName, installDir, 30, 2000);
            if (processId == 0)
            {
                Log("Warning: Game process did not appear within timeout. Bridge will keep running.");
                return;
            }
            Log("Game process appeared: PID=" + processId);
        }

        Log("Monitoring process PID=" + processId + " for exit...");
        int currentPid = processId;
        bool firstExitSearchDone = false;

        while (true)
        {
            System.Threading.Thread.Sleep(pollIntervalMs);

            bool isRunning = false;
            try
            {
                Process p = Process.GetProcessById(currentPid);
                if (!p.HasExited)
                {
                    isRunning = true;
                }
            }
            catch
            {
                // Process not found — it has exited
                isRunning = false;
            }

            if (isRunning)
            {
                continue;
            }

            if (string.IsNullOrEmpty(executableHint))
            {
                Log("Monitored process PID=" + currentPid + " has exited.");
                break;
            }

            Log("Process PID=" + currentPid + " exited. Checking for a launcher handoff...");

            // Multi-stage launchers (e.g. Epic launcher stub -> EAC bootstrapper -> real game)
            // hand off early in the session, so give the FIRST post-exit search a generous
            // budget. Any SUBSEQUENT exit is far more likely to be the genuine end of the
            // session, so use a short budget there to avoid delaying SISR/VIIPER teardown by
            // ~90s on every normal game quit.
            int maxRetries = firstExitSearchDone ? 3 : 45;
            int retryDelayMs = firstExitSearchDone ? 1000 : 2000;
            DateTime exitTime = DateTime.Now;

            // For the first search, look back 120s before exitTime so we can find EAC /
            // multi-stage processes that started BEFORE the launcher stub exited.
            // Subsequent searches use the strict exitTime window (the game is genuinely ending).
            DateTime? minStartTime = firstExitSearchDone ? exitTime : exitTime.AddSeconds(-120);
            firstExitSearchDone = true;

            int replacementPid = FindMatchingProcess(exeName, installDir, maxRetries, retryDelayMs, minStartTime);
            if (replacementPid > 0)
            {
                Log("Found replacement process (handoff): PID=" + replacementPid);
                currentPid = replacementPid;
                continue;
            }

            Log("No replacement process found. Assuming app has exited.");

            // Diagnostic: list all running processes under the install directory to help
            // identify the correct Watch Process name (useful for EAC / multi-stage launchers)
            if (!string.IsNullOrEmpty(installDir))
            {
                LogDiagnosticProcesses(installDir);
            }
            break;
        }
    }

    /// <summary>
    /// Lists every running process whose executable path falls under the given directory.
    /// Useful for diagnosing EAC / multi-stage launcher handoff failures.
    /// </summary>
    static void LogDiagnosticProcesses(string installDir)
    {
        string dirPrefix = installDir.TrimEnd('\\').ToLowerInvariant();
        Log("Diagnostic: listing all running processes under " + installDir);
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (!string.IsNullOrEmpty(proc.MainModule.FileName))
                    {
                        string procPath = proc.MainModule.FileName.ToLowerInvariant();
                        if (procPath.StartsWith(dirPrefix))
                        {
                            Log("  Process: " + proc.ProcessName + " PID=" + proc.Id + " (" + proc.MainModule.FileName + ")");
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    // ==========================================
    // UWP App Scanner (PowerShell-based)
    // ==========================================

    public class UWPAppInfo
    {
        public string Name { get; set; }       // Display name
        public string AUMID { get; set; }      // PackageFamilyName!AppId
        public string Executable { get; set; } // Executable filename from manifest
    }

    /// <summary>
    /// Scans for installed UWP apps using PowerShell Get-AppxPackage.
    /// Returns a list of apps with their display names, AUMIDs, and executable names.
    /// Filters out framework packages and system components.
    /// </summary>
    public static List<UWPAppInfo> ScanInstalledUWPApps()
    {
        var apps = new List<UWPAppInfo>();

        // PowerShell script that outputs pipe-separated: Name|AUMID|Executable
        // One line per app. Filters out framework packages and packages with unresolved display names.
        string script = @"
$startApps = @{}
try {
    Get-StartApps | ForEach-Object {
        if ($_.AppId -and $_.Name) {
            $startApps[$_.AppId.ToLower()] = $_.Name
        }
    }
} catch {}

$installedapps = Get-AppxPackage | Where-Object { -not $_.IsFramework -and $_.InstallLocation }
foreach ($app in $installedapps) {
    try {
        $manifest = Get-AppxPackageManifest $app
        foreach ($appEntry in $manifest.Package.Applications.Application) {
            $id = $appEntry.Id
            $aumid = $app.PackageFamilyName + '!' + $id
            $aumidKey = $aumid.ToLower()

            $name = $manifest.Package.Properties.DisplayName

            # Use pre-resolved name if available, otherwise check resource
            if ($startApps.ContainsKey($aumidKey)) {
                $name = $startApps[$aumidKey]
            } elseif ($name -like '*ms-resource*' -or $name -like '*DisplayName*') {
                continue
            }

            $executable = $appEntry.Executable

            # Handle apps using GameLaunchHelper or missing executable
            if ([string]::IsNullOrWhiteSpace($executable) -or $executable -eq 'GameLaunchHelper.exe') {
                $configPath = Join-Path $app.InstallLocation 'MicrosoftGame.Config'
                if (Test-Path $configPath) {
                    try {
                        [xml]$msconfig = Get-Content $configPath
                        $executable = $msconfig.Game.ExecutableList.Executable.Name
                        if ([string]::IsNullOrWhiteSpace($executable)) {
                            $executable = 'GameLaunchHelper.exe'
                        }
                    } catch {
                        $executable = 'Unknown'
                    }
                } elseif ([string]::IsNullOrWhiteSpace($executable)) {
                    $executable = 'Unknown'
                }
            }

            Write-Output ('{0}|{1}|{2}' -f $name, $aumid, $executable)
        }
    } catch { }
}";

        string tempScriptPath = Path.Combine(Path.GetTempPath(), "uwp_scan.ps1");
        try
        {
            File.WriteAllText(tempScriptPath, script, System.Text.Encoding.UTF8);

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "powershell.exe";
            psi.Arguments = string.Format("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{0}\"", tempScriptPath);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;

            Process proc = Process.Start(psi);
            string output = proc.StandardOutput.ReadToEnd();
            string errors = proc.StandardError.ReadToEnd();
            proc.WaitForExit(60000); // 60 second timeout

            if (!string.IsNullOrEmpty(errors))
            {
                Log("PowerShell scan warnings: " + errors);
            }

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string[] parts = line.Split('|');
                if (parts.Length >= 2)
                {
                    apps.Add(new UWPAppInfo
                    {
                        Name = parts[0].Trim(),
                        AUMID = parts[1].Trim(),
                        Executable = parts.Length >= 3 ? parts[2].Trim() : "Unknown"
                    });
                }
            }

            Log("UWP app scan found " + apps.Count + " apps.");
        }
        catch (Exception ex)
        {
            Log("Failed to scan UWP apps: " + ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(tempScriptPath))
                {
                    File.Delete(tempScriptPath);
                }
            }
            catch { }
        }

            return apps;
    }

    // ==========================================
    // Epic Games Scanner
    // ==========================================

    public class EpicGameInfo
    {
        public string Name { get; set; }           // Display name
        public string AppName { get; set; }        // Epic catalog AppName (used as identifier)
        public string CatalogNamespace { get; set; }
        public string InstallLocation { get; set; }
        public string Executable { get; set; }     // Executable filename
        public string LaunchExecutable { get; set; }
    }

    /// <summary>
    /// Scans for installed Epic Games Store games by reading .item manifest files.
    /// Returns a list of games with their metadata.
    /// </summary>
    public static List<EpicGameInfo> ScanInstalledEpicGames()
    {
        var games = new List<EpicGameInfo>();

        string[] manifestPaths = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Epic\EpicGamesLauncher\Data\Manifests"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Epic\EpicGamesLauncher\Data\Manifests")
        };

        foreach (string manifestDir in manifestPaths)
        {
            if (!Directory.Exists(manifestDir)) continue;

            try
            {
                string[] itemFiles = Directory.GetFiles(manifestDir, "*.item");
                foreach (string itemFile in itemFiles)
                {
                    try
                    {
                        string content = File.ReadAllText(itemFile);
                        EpicGameInfo game = ParseEpicManifest(content);
                        if (game != null && !string.IsNullOrEmpty(game.AppName))
                        {
                            // Avoid duplicates
                            bool duplicate = false;
                            foreach (var existing in games)
                            {
                                if (existing.AppName.Equals(game.AppName, StringComparison.OrdinalIgnoreCase))
                                {
                                    duplicate = true;
                                    break;
                                }
                            }
                            if (!duplicate)
                            {
                                games.Add(game);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Failed to parse Epic manifest: " + itemFile + " - " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Failed to scan Epic manifests directory: " + manifestDir + " - " + ex.Message);
            }
        }

        Log("Epic Games scan found " + games.Count + " games.");
        return games;
    }

    /// <summary>
    /// Finds a specific Epic game's info by its AppName by scanning manifests.
    /// Returns null if not found.
    /// </summary>
    public static EpicGameInfo FindEpicGameInfo(string appName)
    {
        string[] manifestPaths = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Epic\EpicGamesLauncher\Data\Manifests"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Epic\EpicGamesLauncher\Data\Manifests")
        };

        foreach (string manifestDir in manifestPaths)
        {
            if (!Directory.Exists(manifestDir)) continue;
            try
            {
                string[] itemFiles = Directory.GetFiles(manifestDir, "*.item");
                foreach (string itemFile in itemFiles)
                {
                    try
                    {
                        string content = File.ReadAllText(itemFile);
                        EpicGameInfo game = ParseEpicManifest(content);
                        if (game != null && game.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase))
                        {
                            return game;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Finds all running processes whose executable path is within the given directory.
    /// If minStartTime is given, candidates whose StartTime can be determined and predates it
    /// are excluded — this avoids re-adopting a long-lived helper process (anti-cheat service,
    /// crash reporter, updater) that was already running under the install directory before the
    /// previously-tracked process exited.
    ///
    /// When EAC or other anti-cheat software hides process paths (MainModule.FileName is empty),
    /// falls back to scanning the install directory for .exe files and matching by process name.
    /// </summary>
    private static List<int> FindProcessesByInstallPath(string installDir, DateTime? minStartTime = null)
    {
        var pids = new List<int>();
        if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) return pids;

        string dirPrefix = installDir.TrimEnd('\\').ToLowerInvariant();
        HashSet<string> exeProcessNames = null; // lazily built if needed

        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    string procPath = null;
                    try { procPath = proc.MainModule.FileName; } catch { }

                    if (!string.IsNullOrEmpty(procPath))
                    {
                        // Normal path-based match
                        if (procPath.ToLowerInvariant().StartsWith(dirPrefix))
                        {
                            if (minStartTime.HasValue)
                            {
                                try { if (proc.StartTime < minStartTime.Value) continue; }
                                catch { }
                            }

                            pids.Add(proc.Id);
                            Log("Found game process by install path: " + proc.ProcessName + " PID=" + proc.Id + " (" + procPath + ")");
                        }
                    }
                    else
                    {
                        // Path hidden by anti-cheat. Try to match by process name
                        // against .exe files found in the install directory.
                        if (exeProcessNames == null)
                        {
                            exeProcessNames = ScanExeNames(installDir);
                        }

                        if (exeProcessNames.Count > 0 && exeProcessNames.Contains(proc.ProcessName))
                        {
                            if (minStartTime.HasValue)
                            {
                                try { if (proc.StartTime < minStartTime.Value) continue; }
                                catch { }
                            }

                            pids.Add(proc.Id);
                            Log("Found game process by name (path hidden by anti-cheat): " + proc.ProcessName + " PID=" + proc.Id);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        if (pids.Count == 0)
        {
            Log("No processes found under install directory: " + installDir);
        }

        return pids;
    }

    /// <summary>
    /// Scans the install directory (recursively) for .exe files and returns their process names
    /// (filename without extension). Used as a fallback when anti-cheat hides process paths.
    /// </summary>
    private static HashSet<string> ScanExeNames(string installDir)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string file in Directory.GetFiles(installDir, "*.exe", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }
        }
        catch { }
        Log("Scanned " + names.Count + " .exe names from install directory: " + installDir);
        return names;
    }

    /// <summary>
    /// Parses a single Epic .item manifest file using simple regex extraction.
    /// </summary>
    private static EpicGameInfo ParseEpicManifest(string json)
    {
        var game = new EpicGameInfo();
        game.AppName = ExtractJsonValue(json, "AppName");
        game.CatalogNamespace = ExtractJsonValue(json, "CatalogNamespace");
        game.Name = ExtractJsonValue(json, "DisplayName");
        game.InstallLocation = ExtractJsonValue(json, "InstallLocation");
        game.Executable = ExtractJsonValue(json, "Executable");
        game.LaunchExecutable = ExtractJsonValue(json, "LaunchExecutable");

        // Normalize forward slashes to backslashes for Windows paths
        if (!string.IsNullOrEmpty(game.LaunchExecutable))
        {
            game.LaunchExecutable = game.LaunchExecutable.Replace("/", "\\");
        }
        if (!string.IsNullOrEmpty(game.InstallLocation))
        {
            game.InstallLocation = game.InstallLocation.Replace("/", "\\");
        }

        if (string.IsNullOrEmpty(game.Name))
        {
            game.Name = game.AppName;
        }

        // Strip any non-printable characters from display name
        if (!string.IsNullOrEmpty(game.Name))
        {
            game.Name = Regex.Replace(game.Name, @"[^\x20-\x7E]", "");
            game.Name = game.Name.Trim();
        }

        return game;
    }

    /// <summary>
    /// Extracts a string value from a simple JSON object using regex.
    /// Handles escaped backslashes in Windows paths.
    /// </summary>
    private static string ExtractJsonValue(string json, string key)
    {
        // Match "key" : "value" with possible whitespace
        var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        if (match.Success)
        {
            string value = match.Groups[1].Value;
            // Unescape common JSON escapes
            value = value.Replace("\\\\", "\\");
            value = value.Replace("\\/", "/");
            value = value.Replace("\\\"", "\"");
            return value;
        }
        return "";
    }

    // ==========================================
    // Epic Games Launcher Method
    // ==========================================

    /// <summary>
    /// Launches an Epic Games Store game using the com.epicgames.launcher protocol.
    /// Returns the process ID of the launched game (0 if launcher spawns it asynchronously).
    /// </summary>
    static int LaunchEpicGame(string appName, string extraArgs)
    {
        Log("Launching Epic game via protocol: AppName=" + appName + ", Args=" + extraArgs);
        try
        {
            string protocolUri = "com.epicgames.launcher://apps/" + appName + "?action=launch";
            if (!string.IsNullOrEmpty(extraArgs))
            {
                protocolUri += "&args=" + Uri.EscapeDataString(extraArgs);
            }

            ProcessStartInfo psi = new ProcessStartInfo(protocolUri);
            psi.UseShellExecute = true;
            Process proc = Process.Start(psi);
            Log("Epic game launch initiated via protocol: " + protocolUri);

            // The Epic launcher spawns the game process asynchronously.
            // We return 0 here; the bridge will use WaitForGameExit with the executable hint
            // to wait for the game process to appear and then monitor it.
            return 0;
        }
        catch (Exception e)
        {
            string msg = "Failed to launch Epic game: " + e.Message;
            Log(msg);
            throw new Exception(msg, e);
        }
    }
}
