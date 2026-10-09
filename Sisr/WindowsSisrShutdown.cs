using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SBridge.Sisr;

[SupportedOSPlatform("windows")]
internal sealed class WindowsSisrShutdown : ISisrShutdown
{
    private readonly Func<int, IReadOnlyList<IPEndPoint>> listeners;

    public WindowsSisrShutdown(Func<int, IReadOnlyList<IPEndPoint>>? listeners = null)
    {
        this.listeners = listeners ?? WindowsTcpListeners.ForProcess;
    }

    public async Task<bool> RequestQuitAsync(IOwnedSisrProcess process, CancellationToken cancellationToken)
    {
        if (process.HasExited) return false;
        foreach (var endpoint in listeners(process.Id))
        {
            if (process.HasExited) return false;
            using var client = OwnedSisrHttp.Create(process, endpoint);
            using var versionResponse = await client.GetAsync("api/v1/version/info", cancellationToken).ConfigureAwait(false);
            if (!versionResponse.IsSuccessStatusCode) continue;
            string json = await versionResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var info = JsonSerializer.Deserialize<SisrVersion>(json);
            if (!SisrApiContract.SupportsQuit(info?.Version)) continue;
            if (process.HasExited) return false;
            using var response = await client.PostAsync("api/v1/quit", content: null, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        return false;
    }

    private sealed class SisrVersion
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }
}

internal static class SisrApiContract
{
    internal static bool SupportsQuit(string? version)
    {
        // Audited v0.6.1 API; admit compatible patch releases, not unknown major/minor contracts.
        if (version == null) return false;
        string release = version.Split('-')[0].TrimStart('v');
        return Version.TryParse(release, out var parsed) && parsed.Major == 0 && parsed.Minor == 6 && parsed.Build >= 1;
    }
}
