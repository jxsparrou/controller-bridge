using System.Text;
using System.Text.Json.Nodes;
using SBridge.Configuration;
using SBridge.Core;
using SBridge.Launching;
using SBridge.Steam;
using Xunit;

namespace SBridge.Tests;

[Collection("Windows integration")]
public sealed class SteamAccountTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sBridge-accounts-" + Guid.NewGuid().ToString("N"));
    public SteamAccountTests() => Directory.CreateDirectory(directory);
    private SteamAccount Account(string id)
    {
        var account = new SteamAccount(directory, id, "Account " + id);
        Directory.CreateDirectory(account.AccountDirectory); return account;
    }
    private static byte[] EmptyVdf() => Program.SerializeShortcuts(new Program.VdfElement { Type = 0, Name = "shortcuts" });
    private static Game Game() => new(Guid.NewGuid(), "遊戲 Café", "win32", @"C:\Game.exe", GameLaunchKind.Executable, @"C:\Game.exe", [], "", null);
    private static void Add(SteamAccount account, Program.VdfElement root, Game game) =>
        root.Children.Add(SteamShortcutBuilder.Create(root.Children.Count, game.Name, @"C:\Bridge\sBridge.exe", @"C:\Bridge", GameLaunchCommand.Options(game.Id), ""));

    [Fact]
    public void TextVdfSupportsUnicodeEscapesCommentsAndNestedUnknownFields()
    {
        const string text = """
        // Synthetic Steam text VDF
        "users" {
          "76561197960265729" { "AccountName" "login" "PersonaName" "遊戲 \"Café\" \\ Player" "Unknown" { "k" "v" } }
          "76561197960265730" { "AccountName" "fallback" }
        }
        """;
        var names = SteamLoginUsersCodec.Decode(Encoding.UTF8.GetBytes("\uFEFF" + text));
        Assert.Equal("遊戲 \"Café\" \\ Player", names["76561197960265729"]);
        Assert.Equal("fallback", names["76561197960265730"]);
    }

    [Theory]
    [InlineData("\"users\" { \"1\" { \"PersonaName\" \"a\" \"PersonaName\" \"b\" } }")]
    [InlineData("\"users\" { \"1\" { \"PersonaName\" \"unterminated }")]
    [InlineData("\"users\" {} }")]
    [InlineData("\"users\" {} \"unexpected\" {}")]
    public void MalformedTextVdfIsRejected(string text) => Assert.Throws<InvalidDataException>(() => SteamLoginUsersCodec.Decode(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void DiscoveryIncludesFirstFileAccountsExcludesPlaceholderAndUsesIdFallbackForBadNames()
    {
        var first = Account("1"); var second = Account("2"); Account("0"); Account("ac"); Account("01");
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        string login = Path.Combine(directory, "config", "loginusers.vdf");
        File.WriteAllText(login, "\"users\" { \"76561197960265729\" { \"PersonaName\" \"Café\" } }");
        var inventory = SteamAccountDiscovery.Discover(directory);
        Assert.Equal(new[] { "1", "2" }, inventory.Accounts.Select(account => account.Id));
        Assert.Equal("Café [1]", inventory.Accounts[0].DisplayName);
        Assert.False(File.Exists(first.ShortcutPath)); Assert.False(Directory.Exists(Path.GetDirectoryName(second.ShortcutPath)));
        File.WriteAllText(login, "broken");
        inventory = SteamAccountDiscovery.Discover(directory);
        Assert.Single(inventory.Warnings); Assert.Equal("Account 1", inventory.Accounts[0].DisplayName);
        Assert.Equal("broken", File.ReadAllText(login));
    }

    [Fact]
    public void EmptyUnknownAndAmbiguousSelectionFailInsteadOfBroadening()
    {
        var first = Account("1"); var second = Account("2");
        Assert.Throws<InvalidOperationException>(() => SteamAccountSelection.Resolve([first, second], []));
        Assert.Throws<InvalidOperationException>(() => SteamAccountSelection.Resolve([first, second], ["1", "3"]));
        Assert.Throws<InvalidOperationException>(() => SteamAccountSelection.Resolve([first, first], ["1"]));
        Assert.Equal(new[] { second }, SteamAccountSelection.Resolve([first, second], ["2"]));
    }

    [Fact]
    public void SelectedIdsPersistCloneAndOldJsonStartsWithNoImplicitTargets()
    {
        var settings = new AppSettings(); settings.SelectedSteamAccountIds.Add("1"); settings.SelectedSteamAccountIds.Add("2");
        var protector = new NoSecret();
        byte[] encoded = JsonSettingsCodec.Encode(settings, null, protector);
        Assert.Equal(new[] { "1", "2" }, JsonSettingsCodec.Decode(encoded, protector).Settings.SelectedSteamAccountIds.Order());
        var copy = settings.Clone(); copy.SelectedSteamAccountIds.Clear(); Assert.Equal(2, settings.SelectedSteamAccountIds.Count);
        var json = JsonNode.Parse(encoded)!.AsObject(); json.Remove("selectedSteamAccountIds");
        Assert.Empty(JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(json.ToJsonString()), protector).Settings.SelectedSteamAccountIds);
        foreach (JsonNode? invalid in new JsonNode?[] { null, JsonValue.Create("1"), new JsonArray("0"), new JsonArray("1", "1"), new JsonArray("../outside") })
        {
            json["selectedSteamAccountIds"] = invalid;
            Assert.Throws<InvalidDataException>(() => JsonSettingsCodec.Decode(Encoding.UTF8.GetBytes(json.ToJsonString()), protector));
        }
    }

    [Fact]
    public void FirstCreationIsValidatedHasNoBackupAndTransitionsToExistingSnapshot()
    {
        var account = Account("1");
        var repository = new SteamShortcutRepository(() => false);
        var document = repository.LoadForImport(account);
        Assert.True(document.IsNew); Assert.False(Directory.Exists(Path.GetDirectoryName(account.ShortcutPath)));
        Add(account, document.Root, Game());
        Assert.True(repository.Save(document).Succeeded); Assert.False(document.IsNew);
        byte[] saved = File.ReadAllBytes(account.ShortcutPath);
        Assert.Single(Program.ParseShortcuts(saved).Children); Assert.False(File.Exists(account.ShortcutPath + ".bak"));
        Assert.True(repository.Save(document).Succeeded); Assert.Equal(saved, File.ReadAllBytes(account.ShortcutPath + ".bak"));
        File.Delete(account.ShortcutPath); Assert.False(repository.Save(document).Succeeded);
        Assert.False(File.Exists(account.ShortcutPath));
    }

    [Fact]
    public void FirstCreatorRaceAndSteamRestartLeaveWinningFileUntouched()
    {
        var account = Account("1"); byte[] winner = EmptyVdf();
        var files = new Hooks { BeforeCreate = path => File.WriteAllBytes(path, winner) };
        var repository = new SteamShortcutRepository(() => false, files);
        var document = repository.LoadForImport(account); Add(account, document.Root, Game());
        Assert.False(repository.Save(document).Succeeded); Assert.Equal(winner, File.ReadAllBytes(account.ShortcutPath));
        Assert.False(File.Exists(account.ShortcutPath + ".bak")); Assert.Empty(Directory.GetFiles(account.AccountDirectory, "*.tmp", SearchOption.AllDirectories));
        File.Delete(account.ShortcutPath);
        bool running = false;
        files = new Hooks { AfterWrite = () => running = true };
        repository = new SteamShortcutRepository(() => running, files); document = repository.LoadForImport(account);
        Assert.False(repository.Save(document).Succeeded); Assert.False(File.Exists(account.ShortcutPath));
    }

    [Fact]
    public void DeniedMalformedAndMissingAccountCannotBecomeWritableEmptyLibrary()
    {
        var account = Account("1"); Directory.CreateDirectory(Path.GetDirectoryName(account.ShortcutPath)!);
        File.WriteAllBytes(account.ShortcutPath, new byte[] { 0, 1 });
        var repository = new SteamShortcutRepository(() => false);
        Assert.ThrowsAny<IOException>(() => repository.LoadForImport(account));
        Assert.Equal(new byte[] { 0, 1 }, File.ReadAllBytes(account.ShortcutPath));
        var denied = new SteamShortcutRepository(() => false, new Hooks { BeforeRead = _ => throw new UnauthorizedAccessException() });
        Assert.Throws<UnauthorizedAccessException>(() => denied.LoadForImport(account));
        var absent = new SteamAccount(directory, "99", "Missing");
        var document = repository.LoadForImport(absent);
        Assert.False(repository.Save(document).Succeeded); Assert.False(Directory.Exists(absent.AccountDirectory));
    }

    [Fact]
    public void PartialCreationAndCorruptTempLeaveNoOriginalOrTemporaryFile()
    {
        var account = Account("1");
        var files = new Hooks { BeforeWrite = path => { File.WriteAllBytes(path, new byte[] { 0 }); throw new IOException("partial"); } };
        var repository = new SteamShortcutRepository(() => false, files);
        Assert.False(repository.Save(repository.LoadForImport(account)).Succeeded);
        Assert.False(File.Exists(account.ShortcutPath)); Assert.Empty(Directory.GetFiles(account.AccountDirectory, "*.tmp", SearchOption.AllDirectories));
        files = new Hooks { AfterWrite = () => { foreach (string temp in Directory.GetFiles(account.AccountDirectory, "*.tmp", SearchOption.AllDirectories)) File.WriteAllBytes(temp, new byte[] { 0 }); } };
        repository = new SteamShortcutRepository(() => false, files);
        Assert.False(repository.Save(repository.LoadForImport(account)).Succeeded); Assert.False(File.Exists(account.ShortcutPath));
    }

    [Fact]
    public void PerAccountImportDeduplicationAndPartialFailureNeverTouchUnselectedAccount()
    {
        var first = Account("1"); var malformed = Account("2"); var other = Account("3");
        Directory.CreateDirectory(Path.GetDirectoryName(malformed.ShortcutPath)!);
        File.WriteAllBytes(malformed.ShortcutPath, new byte[] { 0, 1 });
        var repository = new SteamShortcutRepository(() => false); var game = Game();
        var results = SteamShortcutImporter.Import(repository, [first, malformed], [game], Add);
        Assert.True(results[0].Save.Succeeded); Assert.Equal(1, results[0].Added); Assert.False(results[1].Save.Succeeded);
        Assert.False(File.Exists(other.ShortcutPath)); Assert.Equal(new byte[] { 0, 1 }, File.ReadAllBytes(malformed.ShortcutPath));
        byte[] firstSaved = File.ReadAllBytes(first.ShortcutPath);
        results = SteamShortcutImporter.Import(repository, [first, other], [game], Add);
        Assert.Equal(0, results[0].Added); Assert.Equal(1, results[1].Added);
        Assert.Equal(firstSaved, File.ReadAllBytes(first.ShortcutPath)); Assert.False(File.Exists(first.ShortcutPath + ".bak"));
        Assert.Single(Program.ParseShortcuts(File.ReadAllBytes(other.ShortcutPath)).Children);
    }

    [Fact]
    public void MalformedConcurrentWinnerAndPriorBackupRemainUntouched()
    {
        var account = Account("1"); var repository = new SteamShortcutRepository(() => false);
        var document = repository.LoadForImport(account);
        Directory.CreateDirectory(Path.GetDirectoryName(account.ShortcutPath)!);
        byte[] backup = { 8, 7, 6 }; File.WriteAllBytes(account.ShortcutPath + ".bak", backup);
        Assert.True(repository.Save(document).Succeeded); Assert.Equal(backup, File.ReadAllBytes(account.ShortcutPath + ".bak"));
        File.Delete(account.ShortcutPath);
        document = repository.LoadForImport(account);
        byte[] winner = { 0, 1 }; File.WriteAllBytes(account.ShortcutPath, winner);
        Assert.False(repository.Save(document).Succeeded);
        Assert.Equal(winner, File.ReadAllBytes(account.ShortcutPath)); Assert.Equal(backup, File.ReadAllBytes(account.ShortcutPath + ".bak"));
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void LinkedConfigDirectoryIsRejectedWithoutWritingOutsideSelectedAccount()
    {
        var account = Account("1"); string outside = Path.Combine(directory, "outside"); Directory.CreateDirectory(outside);
        string config = Path.GetDirectoryName(account.ShortcutPath)!;
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string token in new[] { "/d", "/c", "mklink", "/J", config, outside }) info.ArgumentList.Add(token);
        using var command = System.Diagnostics.Process.Start(info)!;
        try
        {
            Assert.True(command.WaitForExit(5000)); Assert.Equal(0, command.ExitCode);
            var repository = new SteamShortcutRepository(() => false);
            var document = repository.LoadForImport(account);
            Assert.False(repository.Save(document).Succeeded); Assert.Empty(Directory.GetFiles(outside));
            File.WriteAllBytes(Path.Combine(outside, "shortcuts.vdf"), EmptyVdf());
            Assert.Throws<IOException>(() => repository.LoadForImport(account));
            Assert.Empty(SteamAccountDiscovery.Discover(directory).Accounts);
        }
        finally
        {
            if (!command.HasExited) { command.Kill(); command.WaitForExit(); }
            Directory.Delete(config); // Remove only the owned junction, not its target.
        }
    }

    public void Dispose() => Directory.Delete(directory, true);
    private sealed class NoSecret : ISecretProtector
    {
        public string Protect(string value) => throw new InvalidOperationException();
        public string Unprotect(string value) => throw new InvalidOperationException();
    }
    private sealed class Hooks : IShortcutFileOperations
    {
        private readonly ShortcutFileOperations inner = new();
        public Action<string>? BeforeRead { get; init; }
        public Action<string>? BeforeWrite { get; init; }
        public Action? AfterWrite { get; init; }
        public Action<string>? BeforeCreate { get; init; }
        public byte[] Read(string path) { BeforeRead?.Invoke(path); return inner.Read(path); }
        public void WriteNew(string path, byte[] bytes) { BeforeWrite?.Invoke(path); inner.WriteNew(path, bytes); AfterWrite?.Invoke(); }
        public void Backup(string path, string backup) => inner.Backup(path, backup);
        public void Replace(string temporary, string path) => inner.Replace(temporary, path);
        public void Delete(string path) => inner.Delete(path);
        public void PrepareCreation(string account, string config) => inner.PrepareCreation(account, config);
        public void Create(string temporary, string path) { BeforeCreate?.Invoke(path); inner.Create(temporary, path); }
    }
}
