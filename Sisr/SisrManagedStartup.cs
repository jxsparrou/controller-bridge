using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SBridge.Launching;
using SBridge.Diagnostics;

namespace SBridge.Sisr;

internal sealed record SisrManagedStartup(string Directory, string ConfigPath, string LogPath) : IDisposable
{
    private FileStream? activity;
    private SisrStatusSnapshot? lastStatus;
    public static IReadOnlyList<string> Arguments(string raw)
    {
        var tokens = WindowsCommandLine.Split(raw);
        foreach (string token in tokens)
        {
            if (!token.StartsWith('-')) continue;
            string option = token.Split('=')[0].TrimStart('-');
            if (option.Equals("config", StringComparison.OrdinalIgnoreCase) || option.Equals("api.listen-address", StringComparison.OrdinalIgnoreCase) ||
                option.Equals("log.file", StringComparison.OrdinalIgnoreCase) || option.Equals("lf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Managed SISR startup owns --config, --api.listen-address and --log.file. Remove those options or use legacy startup.");
        }
        return tokens;
    }

    public static SisrManagedStartup Create(string dataDirectory)
    {
        if (!Path.IsPathFullyQualified(dataDirectory)) throw new ArgumentException("Managed SISR data directory must be absolute.");
        ManagedSessionDiagnostics.Prune(dataDirectory);
        string directory = Path.Combine(dataDirectory, "sisr", "sessions", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        string config = Path.Combine(directory, "startup.json"), log = Path.Combine(directory, "SISR.log");
        // Kong v1.16.1 JSON resolver uses flag-name keys, not the read-only API
        // config response's controllerEmulation/runMisc PascalCase structure.
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["api.listen_address"] = "127.0.0.1:0", ["log.file"] = log,
            ["window.fullscreen"] = false, ["window.show"] = false
        });
        using (var stream = new FileStream(config, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        var session = new SisrManagedStartup(directory, config, log) { activity = ManagedSessionDiagnostics.MarkActive(directory) };
        session.RecordStatus("starting", null);
        return session;
    }

    public void RecordStatus(string phase, SisrStatusSnapshot? status)
    {
        if (status != null) lastStatus = status;
        try { ManagedSessionDiagnostics.WriteSummary(Directory, phase, lastStatus); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() { activity?.Dispose(); activity = null; }

    public ProcessStartInfo StartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Directory };
        info.ArgumentList.Add("--config=" + ConfigPath);
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        // Prevent inherited SISR_CONFIG/API/log overrides from escaping the owned
        // startup scope. All Steam identity/controller/other environment remains.
        info.Environment["SISR_CONFIG"] = ConfigPath;
        info.Environment["SISR_API_LISTEN_ADDRESS"] = "127.0.0.1:0";
        info.Environment["SISR_LOG_FILE"] = LogPath;
        return info;
    }
}
