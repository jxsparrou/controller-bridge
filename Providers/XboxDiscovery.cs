using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SBridge.Core;

namespace SBridge.Providers;

internal static class XboxDiscoveryCodec
{
    public static GameDiscoveryResult Decode(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("games").ValueKind != JsonValueKind.Array || root.GetProperty("warnings").ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Invalid packaged discovery response.");
            var games = new List<DiscoveredGame>();
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var warnings = root.GetProperty("warnings").EnumerateArray().Select(item => item.GetString() ?? "").ToList();
            foreach (var item in root.GetProperty("games").EnumerateArray())
            {
                string name = Text(item, "name"), aumid = Text(item, "aumid"), executable = Text(item, "executable");
                string? install = item.GetProperty("installDirectory").ValueKind == JsonValueKind.Null ? null : Text(item, "installDirectory");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(aumid) || !aumid.Contains('!'))
                    throw new InvalidDataException("Discovery returned an invalid packaged identity/name.");
                if (!identities.Add(aumid)) { warnings.Add("Duplicate packaged identity was ignored: " + aumid); continue; }
                if (games.Count >= 10000) throw new InvalidDataException("Packaged discovery exceeds the bounded app count.");
                games.Add(new DiscoveredGame(name, "xbox", aumid, GameLaunchKind.PackagedApplication, aumid,
                    Array.Empty<string>(), executable.Replace('/', '\\'), install));
            }
            return new GameDiscoveryResult(games.AsReadOnly(), warnings.AsReadOnly());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Malformed packaged discovery JSON.", ex); }
    }

    private static string Text(JsonElement item, string field)
    {
        string? value = item.GetProperty(field).GetString();
        if (value == null || value.Contains('\0')) throw new InvalidDataException("Discovery text is missing or invalid.");
        return value;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsXboxDiscovery : IPackagedDiscovery
{
    private readonly DiscoveryProcessRunner runner;
    public WindowsXboxDiscovery(DiscoveryProcessRunner? runner = null) => this.runner = runner ?? new DiscoveryProcessRunner();
    public async Task<GameDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        string script = Path.Combine(Path.GetTempPath(), "sbridge-xbox-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            await File.WriteAllTextAsync(script, Script, new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"));
            foreach (string token in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }) info.ArgumentList.Add(token);
            var output = await runner.RunAsync(info, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            if (output.ExitCode != 0) throw new InvalidOperationException("Packaged discovery failed with exit code " + output.ExitCode + ": " + output.StandardError);
            var result = XboxDiscoveryCodec.Decode(output.StandardOutput);
            return string.IsNullOrWhiteSpace(output.StandardError) ? result : result with { Warnings = result.Warnings.Concat(new[] { output.StandardError }).ToArray() };
        }
        finally { File.Delete(script); }
    }

    private const string Script = """
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$startApps = @{}
$warnings = New-Object 'System.Collections.Generic.List[string]'
$results = New-Object 'System.Collections.Generic.List[object]'
try {
    Get-StartApps | ForEach-Object {
        if ($_.AppId -and $_.Name) { $startApps[$_.AppId.ToLowerInvariant()] = $_.Name }
    }
} catch { $warnings.Add('Start-menu names unavailable; using resolved manifest names where possible.') }
Get-AppxPackage | Where-Object { -not $_.IsFramework -and $_.InstallLocation } | ForEach-Object {
    $app = $_
    try {
        $manifest = Get-AppxPackageManifest $app
        foreach ($entry in $manifest.Package.Applications.Application) {
            if (-not $entry.Id) { continue }
            $aumid = $app.PackageFamilyName + '!' + $entry.Id
            $name = [string]$manifest.Package.Properties.DisplayName
            if ($startApps.ContainsKey($aumid.ToLowerInvariant())) { $name = $startApps[$aumid.ToLowerInvariant()] }
            elseif ($name -like '*ms-resource*' -or $name -like '*DisplayName*') { continue }
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            $executable = [string]$entry.Executable
            if ([string]::IsNullOrWhiteSpace($executable) -or $executable -eq 'GameLaunchHelper.exe') {
                $config = Join-Path $app.InstallLocation 'MicrosoftGame.Config'
                if (Test-Path -LiteralPath $config) {
                    try {
                        [xml]$gameConfig = Get-Content -LiteralPath $config -Raw
                        $names = @($gameConfig.Game.ExecutableList.Executable | ForEach-Object { [string]$_.Name } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                        if ($names.Count -eq 1) { $executable = $names[0] }
                        elseif ($names.Count -gt 1) { $warnings.Add('Multiple executable hints for ' + $aumid + '; retaining manifest hint.') }
                    } catch { $warnings.Add('Game executable hint unavailable for ' + $aumid) }
                }
            }
            $results.Add([pscustomobject]@{name=$name; aumid=$aumid; executable=$executable; installDirectory=[string]$app.InstallLocation})
        }
    } catch { $warnings.Add('Skipped unreadable manifest for ' + $app.PackageFamilyName) }
}
$response = @{schemaVersion=1; games=@($results.ToArray()); warnings=@($warnings.ToArray())}
[Console]::WriteLine((ConvertTo-Json -InputObject $response -Depth 5 -Compress))
""";
}
