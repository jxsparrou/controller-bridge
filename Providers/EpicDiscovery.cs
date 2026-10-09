using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SBridge.Core;

namespace SBridge.Providers;

internal sealed class EpicManifest
{
    public required string AppName { get; init; }
    public required string CatalogNamespace { get; init; }
    public required string CatalogItemId { get; init; }
    public required string DisplayName { get; init; }
    public required string InstallLocation { get; init; }
    public required string LaunchExecutable { get; init; }
    public bool? bIsApplication { get; init; }
    public bool? bIsExecutable { get; init; }
    public bool? bIsIncompleteInstall { get; init; }
    public string? MainGameAppName { get; init; }
}

[SupportedOSPlatform("windows")]
internal static class EpicManifestCodec
{
    public const int MaxManifestBytes = 1024 * 1024;
    public static DiscoveredGame? Decode(byte[] bytes)
    {
        if (bytes.Length > MaxManifestBytes) throw new InvalidDataException("Epic manifest exceeds the 1 MiB limit.");
        try
        {
            // Real .item files may have a UTF-8 BOM; JSON parsing itself is strict.
            ReadOnlySpan<byte> json = bytes;
            if (json.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json[3..];
            using var document = JsonDocument.Parse(json.ToArray());
            ValidateProperties(document.RootElement);
            var manifest = document.RootElement.Deserialize<EpicManifest>() ?? throw new InvalidDataException("Epic manifest must be an object.");
            string identity = EpicLaunchIdentity.Create(manifest.CatalogNamespace, manifest.CatalogItemId, manifest.AppName);
            if (manifest.bIsApplication == false || manifest.bIsExecutable == false || manifest.bIsIncompleteInstall == true ||
                (!string.IsNullOrEmpty(manifest.MainGameAppName) && manifest.MainGameAppName != manifest.AppName)) return null;
            if (string.IsNullOrWhiteSpace(manifest.DisplayName) || manifest.DisplayName.Contains('\0'))
                throw new InvalidDataException("Epic display name is missing or invalid.");
            if (string.IsNullOrWhiteSpace(manifest.InstallLocation) || string.IsNullOrWhiteSpace(manifest.LaunchExecutable))
                throw new InvalidDataException("Epic install directory/executable is missing.");
            string install = manifest.InstallLocation.Replace('/', '\\');
            string executable = manifest.LaunchExecutable.Replace('/', '\\');
            if (!Path.IsPathFullyQualified(install) || install.StartsWith(@"\\?\", StringComparison.Ordinal) || install.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                executable.Length == 0 || executable.StartsWith('\\') || executable.Contains(':') || executable.Contains('\0'))
                throw new InvalidDataException("Epic requires an absolute install directory and a relative launch executable.");
            install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(install));
            string expected = Path.GetFullPath(Path.Combine(install, executable));
            string boundary = install.EndsWith('\\') ? install : install + "\\";
            if (!expected.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
                !expected.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Epic launch executable is outside the install directory or is not an executable.");
            return new DiscoveredGame(manifest.DisplayName, "epic", identity, GameLaunchKind.EpicLauncher,
                EpicLaunchIdentity.Target(identity), Array.Empty<string>(), expected, install);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        { throw new InvalidDataException("Malformed or unsupported Epic manifest.", ex); }
    }

    private static void ValidateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate Epic manifest property.");
                ValidateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateProperties(item);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsEpicDiscovery : IEpicDiscovery
{
    private readonly string[] directories;
    public WindowsEpicDiscovery(params string[] directories) => this.directories = directories.Length != 0 ? directories.ToArray() : new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests")
    };

    public Task<GameDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        var games = new Dictionary<string, DiscoveredGame>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        int fileCount = 0;
        try
        {
            foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                budget.Token.ThrowIfCancellationRequested();
                // Missing launcher installation is a normal empty discovery.
                try
                {
                    foreach (string file in Directory.EnumerateFiles(directory, "*.item", SearchOption.TopDirectoryOnly))
                    {
                        budget.Token.ThrowIfCancellationRequested();
                        if (++fileCount > 4096) throw new InvalidDataException("Epic discovery exceeds the 4096 manifest limit.");
                        try
                        {
                            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            if (stream.Length > EpicManifestCodec.MaxManifestBytes) throw new InvalidDataException("Manifest size limit exceeded.");
                            using var output = new MemoryStream();
                            byte[] buffer = new byte[8192];
                            int read;
                            while ((read = await stream.ReadAsync(buffer, budget.Token).ConfigureAwait(false)) != 0)
                            {
                                if (output.Length + read > EpicManifestCodec.MaxManifestBytes) throw new InvalidDataException("Manifest size limit exceeded.");
                                output.Write(buffer, 0, read);
                            }
                            var game = EpicManifestCodec.Decode(output.ToArray());
                            if (game == null || ambiguous.Contains(game.ProviderId)) continue;
                            if (games.TryGetValue(game.ProviderId, out var existing))
                            {
                                if (!string.Equals(existing.InstallDirectory, game.InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
                                    !string.Equals(existing.ProcessHint, game.ProcessHint, StringComparison.OrdinalIgnoreCase))
                                {
                                    games.Remove(game.ProviderId); ambiguous.Add(game.ProviderId);
                                    warnings.Add("Conflicting installed manifests omitted for " + game.ProviderId);
                                }
                            }
                            else games.Add(game.ProviderId, game);
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                        { warnings.Add("Skipped Epic manifest " + Path.GetFileName(file) + ": " + ex.Message); }
                    }
                }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException && ex is not InvalidDataException)
                { warnings.Add("Epic manifest directory unavailable: " + directory); }
            }
            return new GameDiscoveryResult(games.Values.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ToArray(), warnings.ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Epic discovery exceeded its 30-second budget."); }
    }, cancellationToken);
}
