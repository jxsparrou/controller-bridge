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

// Legacy null contracts are migrated with their subsystem, not the SDK switch.
#nullable disable

using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using System.Drawing;
using System.Text;
using System.Collections.Generic;
using SBridge.Sisr;
using SBridge.Launching;
using SBridge.Sessions;
using System.Threading;
using SBridge.Core;
using SBridge.Configuration;
using SBridge.Providers;
using SBridge.Diagnostics;

partial class Program
{
    static string configPath = "";
    static string logPath = "";

    internal static AppSettings Settings { get; private set; } = new AppSettings();
    private static readonly JsonSettingsStore configurationStore = new JsonSettingsStore(new WindowsSecretProtector());
    private static JsonSettingsDocument configurationDocument;
    private static string lastSettingsSaveError;
    private static bool isReportingSettingsSaveError;
    private static BoundedLog bridgeLog;
    internal static readonly XboxProvider XboxGames = new XboxProvider(new WindowsXboxDiscovery(new DiscoveryProcessRunner(Log)), new WindowsPackagedActivation());
    internal static readonly EpicProvider EpicGames = new EpicProvider(new WindowsEpicDiscovery(), new WindowsEpicActivation());
    private static readonly GameProviders gameProviders = new GameProviders(XboxGames, EpicGames, new Win32Provider(new WindowsWin32Activation()));

    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            LoadConfig();

            Game registeredGame = null;
            LegacyLaunchRequest request;
            if (GameLaunchCommand.TryParse(args, out Guid gameId))
            {
                registeredGame = GameLaunchCommand.Resolve(Settings, gameId);
                request = LegacyLaunchRequest.FromGame(registeredGame);
                Log("Resolved game ID=" + registeredGame.Id + ", name=" + registeredGame.Name + ", provider=" + registeredGame.Provider);
            }
            else request = LegacyLaunchRequest.Parse(args);
            if (request == null)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new SettingsForm());
                return;
            }

            string profileKey = registeredGame?.ProfileKey ?? request.Target;
            string watchOverride = Settings.GetProfile(profileKey).WatchProcess;
            string executableHint = request.ResolveProcessHint(watchOverride);
            bool runSisr = Settings.IsSisrEnabledFor(profileKey);
            bool isCustomGame = request.Kind == LegacyLaunchKind.Win32;
            var provider = gameProviders.ForLaunch(request);
            string extraArgs = WindowsCommandLine.Join(request.Arguments);

            Log(string.Format("Bridge started: Path/AUMID={0}, ExecutableHint={1}, ExtraArgs={2}, CustomGame={3}, SISR={4}", request.Target, executableHint, extraArgs, isCustomGame, runSisr));

            SisrProcessManager ownedSisr = null;
            try
            {
                if (runSisr)
                {
                    Log("Launching owned SISR: " + Settings.SisrPath);
                    ownedSisr = Settings.ManagedSisrStartup
                        ? SisrProcessManager.StartWindowsManaged(Settings.SisrPath, Settings.SisrArguments, AppPaths.ForWindows().DataDirectory, Log)
                        : SisrProcessManager.StartWindows(Settings.SisrPath, Settings.SisrArguments, Log);
                    if (Settings.ManagedSisrStartup)
                    {
                        var status = ownedSisr.WaitForReadyAsync(new WindowsSisrStatus(), CancellationToken.None).GetAwaiter().GetResult();
                        foreach (string line in SisrReadiness.Describe(status)) Log(line);
                    }
                }

                var observer = new WindowsProcessObserver();
                var monitor = new GameSessionMonitor(observer);
                var baseline = observer.Capture();
                var started = DateTimeOffset.UtcNow;
                string expectedPath = Path.IsPathFullyQualified(executableHint) ? Path.GetFullPath(executableHint) : null;
                if (isCustomGame && string.IsNullOrEmpty(watchOverride) &&
                    (registeredGame == null || registeredGame.ProcessHint.Length == 0)) expectedPath = Path.GetFullPath(request.Target);
                string installDirectory = registeredGame?.InstallDirectory ?? (isCustomGame ? Path.GetDirectoryName(Path.GetFullPath(request.Target)) : null);
                // Provider launching stays on this STA thread; discovery/monitoring
                // are async. Providers own neither SISR nor session decisions.
                var context = new ProviderLaunchContext(Guid.NewGuid(), started, baseline, executableHint, expectedPath, installDirectory);
                Log("Launch provider=" + provider.Id);
                var evidence = provider.Launch(request, context, Log);
                var result = monitor.MonitorAsync(evidence, Log, CancellationToken.None).GetAwaiter().GetResult();
                if (result.Outcome != GameSessionOutcome.Completed)
                    throw new InvalidOperationException("The game session could not be identified or exceeded a handoff/startup limit. Check the session log and Watch Process hint.");
            }
            finally
            {
                ownedSisr?.Dispose();
            }

            Log("Bridge exiting successfully.");
        }
        catch (Exception ex)
        {
            string msg = string.Format("An error occurred: {0}\n\nStack Trace:\n{1}", ex.Message, ex.StackTrace);
            Log(msg);
            MessageBox.Show(msg, "sBridge Exception", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public static void LoadConfig()
    {
        var paths = AppPaths.ForWindows();
        configPath = paths.ConfigFile;
        logPath = paths.LogFile;
        configurationDocument = null;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string defaultSisrPath = Path.Combine(localAppData, @"SISR\SISR.exe");
        if (!File.Exists(defaultSisrPath))
        {
            string alternative = Path.Combine(localAppData, @"Programs\SISR\SISR.exe");
            if (File.Exists(alternative)) defaultSisrPath = alternative;
        }
        configurationDocument = configurationStore.LoadOrMigrate(configPath, paths.LegacyConfigFile, new AppSettings { SisrPath = defaultSisrPath });
        Settings = configurationDocument.Settings;
        bridgeLog = new BoundedLog(logPath);
        lastSettingsSaveError = null;
    }

    public static bool SaveConfig()
    {
        if (isReportingSettingsSaveError) return false; // No nested commit during a failed-registration dialog.
        var result = configurationDocument == null ? new SettingsSaveResult(false, "Settings were not loaded; no configuration will be overwritten.")
            : configurationStore.Save(configurationDocument);
        if (result.Succeeded)
        {
            lastSettingsSaveError = null;
            return true;
        }
        string message = "Settings were not saved.\n\n" + configPath + "\n" + result.Error;
        Log(message);
        if (lastSettingsSaveError == null && !isReportingSettingsSaveError)
        {
            // Set before the modal dialog: focus-change events may retry auto-save.
            // Temporary filenames vary across retries, so message equality is not
            // a reliable deduplication key. A successful save resets the episode.
            lastSettingsSaveError = message;
            isReportingSettingsSaveError = true;
            try { MessageBox.Show(message, "Error Saving Settings", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { isReportingSettingsSaveError = false; }
        }
        return false;
    }

    internal static bool TryRegisterGames(IReadOnlyList<Game> definitions, IReadOnlyList<GameProfile> profiles, out List<Game> registered)
    {
        registered = new List<Game>();
        try
        {
            return GameCatalog.TryCommit(Settings, definitions, profiles, SaveConfig, out registered);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(ex.Message, "Game Registration Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return false;
    }

    internal static string ShortcutProfileKey(string options) => GameLaunchCommand.ShortcutProfileKey(options, Settings);
    internal static string ShortcutTarget(string options) => GameLaunchCommand.ShortcutTarget(options, Settings);

    static void ShowUsage()
    {
        string message = string.Format(
            "sBridge\n" +
            "=======\n" +
            "This program launches UWP or custom PC games. When launched with a UWP App ID (AUMID) or executable path, it optionally runs SISR in the background, starts the game, and cleans up when the game closes.\n\n" +
            "Usage:\n" +
            "  sBridge.exe launch <game-id>\n" +
            "  sBridge.exe <AUMID_or_Path> [executable_hint_or_arguments]\n\n" +
            "Config File:\n" +
            "  {0}\n\n" +
            "Current Paths:\n" +
            "  SISR.exe: {1} (Exists: {2}, Enabled: {3})\n\n" +
            "How to use in Steam:\n" +
            "1. Use the Settings GUI to scan for UWP apps or add custom games to Steam.\n" +
            "2. Alternatively, manually add a shortcut pointing to this 'sBridge.exe' file, passing the AUMID or game path as an argument.\n\n" +
            "Click OK to open the configuration file folder.",
            configPath,
            Settings.SisrPath,
            File.Exists(Settings.SisrPath) ? "Yes" : "No",
            Settings.SisrEnabled ? "Yes" : "No"
        );

        MessageBox.Show(message, "sBridge Info", MessageBoxButtons.OK, MessageBoxIcon.Information);

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
        if (!Settings.LogEnabled) return;
        bridgeLog?.Write(message, Settings.SteamGridDbApiKey);
    }

    internal static DiagnosticSnapshot CollectDiagnostics(AppSettings settings)
    {
        var accounts = FindSteamAccounts(false);
        var ids = new HashSet<string>(StringComparer.Ordinal); foreach (var account in accounts) ids.Add(account.Id);
        int unavailable = 0; foreach (string id in settings.SelectedSteamAccountIds) if (!ids.Contains(id)) unavailable++;
        var external = new HashSet<int>();
        foreach (string name in new[] { "SISR", Path.GetFileNameWithoutExtension(settings.SisrPath) })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var processes = Process.GetProcessesByName(name);
            try { foreach (var process in processes) external.Add(process.Id); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return new DiagnosticSnapshot(typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown", Environment.Version.ToString(),
            settings.LogEnabled, bridgeLog?.FailedWrites ?? 0, IsSteamRunning(), accounts.Count, settings.SelectedSteamAccountIds.Count, unavailable,
            settings.Games.Count, settings.GameProfiles.Count, settings.SisrEnabled, settings.ManagedSisrStartup,
            !string.IsNullOrWhiteSpace(settings.SisrPath), File.Exists(settings.SisrPath), external.Count, !string.IsNullOrEmpty(settings.SteamGridDbApiKey),
            ManagedSessionDiagnostics.ReadLatest(AppPaths.ForWindows().DataDirectory));
    }

    public static string ParseFirstArgument(string launchOptions)
    {
        return WindowsCommandLine.FirstArgument(launchOptions);
    }
}
