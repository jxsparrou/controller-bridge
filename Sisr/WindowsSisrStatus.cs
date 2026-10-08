using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SBridge.Sisr;

[SupportedOSPlatform("windows")]
internal sealed class WindowsSisrStatus : ISisrStatusSource
{
    private readonly Func<int, IReadOnlyList<IPEndPoint>> listeners;
    public WindowsSisrStatus(Func<int, IReadOnlyList<IPEndPoint>>? listeners = null) => this.listeners = listeners ?? WindowsTcpListeners.ForProcess;

    public async Task<SisrStatusSnapshot?> ProbeAsync(IOwnedSisrProcess process, CancellationToken token)
    {
        if (process.HasExited) return null;
        foreach (var endpoint in listeners(process.Id).Take(8))
        {
            token.ThrowIfCancellationRequested();
            using var client = OwnedSisrHttp.Create(process, endpoint);
            using var version = await GetAsync(client, "version/info", token).ConfigureAwait(false);
            if (version == null) continue;
            string release = RequiredString(version.RootElement, "version");
            if (!SisrApiContract.SupportsQuit(release)) return new SisrStatusSnapshot(release, false, false, false, false, false, false, false, null, null, null);
            using var steam = await GetAsync(client, "steam/status", token).ConfigureAwait(false);
            if (steam == null) continue;
            var state = steam.RootElement;
            bool running = RequiredBool(state, "steam_running"), noSteam = RequiredBool(state, "no_steam_mode"),
                viaSteam = RequiredBool(state, "launched_via_steam"), cef = RequiredBool(state, "cef_debug_reachable"), marker = RequiredBool(state, "marker_shortcut_present");
            bool viiperConnected = false; int? devices = null; string? controller = null; bool? initial = null;
            bool? fullscreen = null, shown = null;
            using var viiper = await GetAsync(client, "viiper/status", token).ConfigureAwait(false);
            if (viiper != null && viiper.RootElement.TryGetProperty("status", out var ping) && ping.ValueKind == JsonValueKind.Object)
                viiperConnected = !string.IsNullOrWhiteSpace(RequiredString(ping, "server")) && !string.IsNullOrWhiteSpace(RequiredString(ping, "version"));
            using var inventory = await GetAsync(client, "devices", token).ConfigureAwait(false);
            if (inventory != null)
            {
                devices = inventory.RootElement.ValueKind switch { JsonValueKind.Null => 0, JsonValueKind.Array => inventory.RootElement.GetArrayLength(),
                    _ => throw new InvalidDataException("Invalid SISR device inventory.") };
                if (devices > 256) throw new InvalidDataException("SISR device inventory exceeds its bound.");
            }
            using var config = await GetAsync(client, "config", token).ConfigureAwait(false);
            if (config != null)
            {
                if (config.RootElement.TryGetProperty("controllerEmulation", out var emulation))
                {
                    string type = RequiredString(emulation, "DefaultControllerType");
                    if (type is "xbox360" or "dualshock4" or "dualsense" or "dualsenseedge" or "ns2pro") controller = type;
                }
                if (config.RootElement.TryGetProperty("runMisc", out var misc)) initial = RequiredBool(misc, "InitialLaunch");
                if (config.RootElement.TryGetProperty("window", out var window))
                { fullscreen = RequiredBool(window, "Fullscreen"); shown = RequiredBool(window, "Show"); }
                // Never retain/log the config's VIIPER password or other arbitrary fields.
            }
            return new SisrStatusSnapshot(release, true, running, noSteam, viaSteam, cef, marker, viiperConnected, devices, controller, initial, fullscreen, shown);
        }
        return null;
    }

    private static async Task<JsonDocument?> GetAsync(HttpClient client, string path, CancellationToken token)
    {
        using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(token); requestBudget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await client.GetAsync("api/v1/" + path, requestBudget.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(requestBudget.Token).ConfigureAwait(false);
            return JsonDocument.Parse(bytes);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    private static bool RequiredBool(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("SISR status has a missing/invalid boolean field.");
        return property.GetBoolean();
    }
    private static string RequiredString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String || property.GetString() is not { } text || text.Length > 128)
            throw new InvalidDataException("SISR status has a missing/invalid string field.");
        return text;
    }
}
