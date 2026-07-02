using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
    // Process Monitoring Method
    // ==========================================

    /// <summary>
    /// Waits for the UWP app to exit by polling its process ID.
    /// If the initial PID exits early, attempts to find a replacement process
    /// (handles launcher-style UWP apps that spawn a separate game process).
    /// </summary>
    static void WaitForUWPAppExit(int processId, string executableHint)
    {
        const int pollIntervalMs = 3000;
        const int launcherGracePeriodMs = 15000;

        if (processId == 0)
        {
            Log("Warning: UWP app returned PID 0 (launch may have failed).");
            return;
        }

        Log("Monitoring process PID=" + processId + " for exit...");
        DateTime launchTime = DateTime.Now;
        int currentPid = processId;
        bool foundReplacement = false;

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

            // Process exited — check if it was a launcher that spawned the real game
            double elapsedMs = (DateTime.Now - launchTime).TotalMilliseconds;

            if (!foundReplacement && !string.IsNullOrEmpty(executableHint) && elapsedMs < launcherGracePeriodMs)
            {
                Log("Initial process exited quickly (" + (int)elapsedMs + "ms). Searching for replacement process...");

                // Extract the executable name from the hint (e.g., "game.exe" from a path or name)
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

                // Search for a process matching the executable name
                int replacementPid = 0;
                try
                {
                    Process[] candidates = Process.GetProcessesByName(exeName);
                    if (candidates.Length > 0)
                    {
                        replacementPid = candidates[0].Id;
                        Log("Found replacement process: " + exeName + " PID=" + replacementPid);
                    }
                }
                catch (Exception ex)
                {
                    Log("Error searching for replacement process: " + ex.Message);
                }

                if (replacementPid > 0)
                {
                    currentPid = replacementPid;
                    foundReplacement = true;
                    continue;
                }
                else
                {
                    // No replacement found — wait a bit more before giving up
                    Log("No replacement process found yet, will retry...");
                    System.Threading.Thread.Sleep(3000);

                    // Try one more time
                    try
                    {
                        Process[] candidates2 = Process.GetProcessesByName(exeName);
                        if (candidates2.Length > 0)
                        {
                            currentPid = candidates2[0].Id;
                            foundReplacement = true;
                            Log("Found replacement process on retry: " + exeName + " PID=" + currentPid);
                            continue;
                        }
                    }
                    catch { }

                    Log("No replacement process found. Assuming app has exited.");
                    break;
                }
            }
            else
            {
                Log("Monitored process PID=" + currentPid + " has exited.");
                break;
            }
        }
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
$installedapps = Get-AppxPackage
foreach ($app in $installedapps) {
    try {
        if (-not $app.IsFramework) {
            $manifest = Get-AppxPackageManifest $app
            foreach ($appEntry in $manifest.Package.Applications.Application) {
                $id = $appEntry.Id
                $aumid = $app.PackageFamilyName + '!' + $id
                $name = $manifest.Package.Properties.DisplayName
                $executable = $appEntry.Executable

                # Skip entries with unresolved resource names
                if ($name -like '*ms-resource*' -or $name -like '*DisplayName*') {
                    try {
                        $resolved = (Get-StartApps | Where-Object { $_.AppId -eq $aumid }).Name
                        if ($resolved) { $name = $resolved }
                        else { continue }
                    } catch { continue }
                }

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
}
