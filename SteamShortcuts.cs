// Legacy null contracts are migrated with their subsystem, not the SDK switch.
#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using SBridge.Steam;
using SBridge.Artwork;

partial class Program
{
    public class SteamShortcutItem
    {
        public string AppName { get; set; }
        public string Exe { get; set; }
        public string LaunchOptions { get; set; }
        public string VdfPath { get; set; }
        public VdfElement ShortcutElement { get; set; }
        public VdfElement RootElement { get; set; }
        public SteamShortcutDocument Document { get; set; }
    }

    private static readonly SteamShortcutRepository shortcutRepository = new SteamShortcutRepository(IsSteamRunning);
    internal static readonly ArtworkService Artwork = new ArtworkService(ArtworkHttpClient.CreateShared(), WindowsArtworkImages.Process);

    private static bool IsSteamRunning()
    {
        var processes = Process.GetProcessesByName("steam");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static List<string> FindShortcutsVdfFiles()
    {
        var paths = new List<string>();
        foreach (var account in FindSteamAccounts())
            if (File.Exists(account.ShortcutPath)) paths.Add(account.ShortcutPath);
        return paths;
    }

    public static List<SteamAccount> FindSteamAccounts(bool logWarnings = true)
    {
        try
        {
            string testRoot = Environment.GetEnvironmentVariable("SBRIDGE_TEST_STEAM_DIRECTORY");
            if (!string.IsNullOrEmpty(testRoot))
            {
                string testData = Environment.GetEnvironmentVariable("SBRIDGE_TEST_DATA_DIRECTORY");
                string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
                if (string.IsNullOrEmpty(testData) || !Path.IsPathFullyQualified(testRoot) || !Path.IsPathFullyQualified(testData) ||
                    !Path.GetFullPath(testRoot).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFullPath(testData).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A test Steam directory requires absolute isolated test paths.");
                return new List<SteamAccount>(SteamAccountDiscovery.Discover(testRoot).Accounts);
            }
            string steamPath = null;
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
            if (string.IsNullOrEmpty(steamPath))
                foreach (string fallback in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
                    if (Directory.Exists(fallback)) { steamPath = fallback; break; }
            if (string.IsNullOrEmpty(steamPath)) return new List<SteamAccount>();
            var inventory = SteamAccountDiscovery.Discover(steamPath);
            if (logWarnings) foreach (string warning in inventory.Warnings) Log(warning);
            return new List<SteamAccount>(inventory.Accounts);
        }
        catch (Exception ex) { if (logWarnings) Log("Error discovering Steam accounts: " + ex.Message); }
        return new List<SteamAccount>();
    }

    internal static IReadOnlyList<SteamAccount> SelectedSteamAccounts() => SteamAccountSelection.Resolve(FindSteamAccounts(), Settings.SelectedSteamAccountIds);

    internal static List<SteamShortcutSaveResult> ImportGamesToSteam(IReadOnlyList<SteamAccount> accounts, IReadOnlyList<SBridge.Core.Game> games, Action<ArtworkRequest> queueArtwork)
    {
        var added = new Dictionary<string, List<ArtworkRequest>>(StringComparer.OrdinalIgnoreCase);
        var results = SteamShortcutImporter.Import(shortcutRepository, accounts, games,
            (account, root, game) =>
            {
                var entry = AddShortcutToSteam(account.ShortcutPath, root, game.Name, SBridge.Launching.GameLaunchCommand.Options(game.Id));
                if (!added.ContainsKey(account.ShortcutPath)) added[account.ShortcutPath] = new List<ArtworkRequest>();
                added[account.ShortcutPath].Add(new ArtworkRequest(account, game.Name,
                    unchecked((uint)entry.Children.Find(field => field.Name == "appid").IntValue),
                    entry.Children.Find(field => field.Name == "Exe").StringValue,
                    entry.Children.Find(field => field.Name == "LaunchOptions").StringValue, ""));
            });
        var saves = new List<SteamShortcutSaveResult>();
        foreach (var result in results)
        {
            saves.Add(result.Save);
            Log("Steam import " + result.Save.Path + ": " + (result.Save.Succeeded ? "saved/unchanged, added=" + result.Added : "failed: " + result.Save.Error));
            if (result.Save.Succeeded && !string.IsNullOrEmpty(Settings.SteamGridDbApiKey) && added.TryGetValue(result.Save.Path, out var additions))
                foreach (var request in additions)
                    try { queueArtwork(request); }
                    catch (InvalidOperationException) { Log("Artwork could not be queued; the saved shortcut remains available."); }
        }
        return saves;
    }

    internal static SteamShortcutSaveResult CompleteArtworkIcon(ArtworkRequest request, string iconPath)
    {
        try
        {
            var accounts = SelectedSteamAccounts();
            bool selected = false;
            foreach (var account in accounts)
                if (account.Id == request.Account.Id && string.Equals(account.ShortcutPath, request.Account.ShortcutPath, StringComparison.OrdinalIgnoreCase)) selected = true;
            if (!selected) return new SteamShortcutSaveResult(request.Account.ShortcutPath, false, "Artwork account is no longer selected.");
            return SteamArtworkIconUpdater.Update(shortcutRepository, request, iconPath);
        }
        catch (InvalidOperationException) { return new SteamShortcutSaveResult(request.Account.ShortcutPath, false, "Artwork account selection is unavailable."); }
    }

    public static List<SteamShortcutItem> LoadSteamShortcuts()
    {
        var list = new List<SteamShortcutItem>();
        foreach (var vdfPath in FindShortcutsVdfFiles())
        {
            try
            {
                var document = shortcutRepository.LoadExisting(vdfPath);
                var root = document.Root;
                foreach (var child in root.Children)
                {
                    if (child.Type != 0x00) continue;
                    var exeChild = child.Children.Find(c => c.Name.Equals("Exe", StringComparison.OrdinalIgnoreCase));
                    var nameChild = child.Children.Find(c => c.Name.Equals("AppName", StringComparison.OrdinalIgnoreCase));
                    var launchChild = child.Children.Find(c => c.Name.Equals("LaunchOptions", StringComparison.OrdinalIgnoreCase));
                    if (exeChild != null && exeChild.Type == 0x01 && nameChild != null && nameChild.Type == 0x01)
                        list.Add(new SteamShortcutItem
                        {
                            AppName = nameChild.StringValue, Exe = exeChild.StringValue,
                            LaunchOptions = launchChild != null && launchChild.Type == 0x01 ? launchChild.StringValue : "",
                            VdfPath = vdfPath, ShortcutElement = child, RootElement = root, Document = document
                        });
                }
            }
            catch (Exception ex) { Log("Failed to parse Steam shortcut file: " + vdfPath + ". Error: " + ex.Message); }
        }
        return list;
    }

    public static List<SteamShortcutSaveResult> SaveSteamShortcuts(List<SteamShortcutItem> itemsToSave)
    {
        var grouped = new Dictionary<string, SteamShortcutItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in itemsToSave)
            if (!grouped.ContainsKey(item.VdfPath)) grouped[item.VdfPath] = item;
        var results = new List<SteamShortcutSaveResult>();
        foreach (var pair in grouped)
        {
            var item = pair.Value;
            var result = item.Document == null || !ReferenceEquals(item.RootElement, item.Document.Root) ||
                !string.Equals(Path.GetFullPath(pair.Key), item.Document.Path, StringComparison.OrdinalIgnoreCase)
                ? new SteamShortcutSaveResult(pair.Key, false, "Missing or mismatched loaded shortcut document. Reload before editing.")
                : shortcutRepository.Save(item.Document);
            results.Add(result);
            Log(result.Succeeded ? "Successfully saved shortcuts.vdf to: " + result.Path
                : "Failed to save shortcuts.vdf: " + result.Path + ". Error: " + result.Error);
        }
        return results;
    }

    public static bool TryLoadShortcutDocument(string vdfPath, out SteamShortcutDocument document)
    {
        try { document = shortcutRepository.LoadExisting(vdfPath); return true; }
        catch (Exception ex)
        {
            document = null;
            string msg = "Could not load shortcuts.vdf. The file was not changed.\n\n" + vdfPath + "\n" + ex.Message;
            Log(msg); MessageBox.Show(msg, "Error Loading Shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    public static uint CalculateAppId(string appName, string exePath) => SteamShortcutIdentity.CalculateAppId(appName, exePath);

    public static VdfElement AddShortcutToSteam(string vdfPath, VdfElement root, string appName, string launchOptions)
    {
        string myExe = Process.GetCurrentProcess().MainModule.FileName;
        string myDir = AppDomain.CurrentDomain.BaseDirectory;
        int nextIndex = 0;
        foreach (var child in root.Children)
            if (int.TryParse(child.Name, out int index) && index >= nextIndex) nextIndex = index + 1;
        var shortcut = SteamShortcutBuilder.Create(nextIndex, appName, myExe, myDir, launchOptions, "");
        root.Children.Add(shortcut);
        // Artwork is scheduled only after the corresponding account save succeeds.
        return shortcut;
    }

    public static void RemoveShortcutFromSteam(VdfElement root, VdfElement shortcutToRemove) => root.Children.Remove(shortcutToRemove);
}
