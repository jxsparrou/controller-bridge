using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SBridge.Launching;
using SBridge.Diagnostics;
using SBridge.Core;

namespace SBridge.Sisr;

internal sealed record SisrManagedStartup(string Directory, string ConfigPath, string LogPath) : IDisposable
{
    private FileStream? activity;
    private SisrStatusSnapshot? lastStatus;
    private SisrControllerProfile? controller;
    public static IReadOnlyList<string> Arguments(string raw, SisrControllerProfile? controller = null)
    {
        controller?.Validate();
        var tokens = WindowsCommandLine.Split(raw);
        foreach (string token in tokens)
        {
            if (!token.StartsWith('-')) continue;
            string option = token.Split('=')[0].TrimStart('-');
            if (option.Equals("config", StringComparison.OrdinalIgnoreCase) || option.Equals("api.listen-address", StringComparison.OrdinalIgnoreCase) ||
                option.Equals("log.file", StringComparison.OrdinalIgnoreCase) || option.Equals("lf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Managed SISR startup owns --config, --api.listen-address and --log.file. Remove those options or use legacy startup.");
            string normalized = option.StartsWith("no-", StringComparison.OrdinalIgnoreCase) ? option[3..] : option;
            if (controller != null && (normalized.Equals("default-controller-type", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("gyro-passthrough", StringComparison.OrdinalIgnoreCase) || normalized.Equals("touchpad-passthrough", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("back-button-passthrough", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("ct", StringComparison.OrdinalIgnoreCase) || normalized.Equals("gp", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("tp", StringComparison.OrdinalIgnoreCase) || normalized.Equals("bbp", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("This game's structured SISR profile conflicts with controller options in global advanced arguments. Remove those options or inherit the controller profile.");
        }
        return tokens;
    }

    public static SisrManagedStartup Create(string dataDirectory, SisrControllerProfile? controller = null)
    {
        controller?.Validate();
        if (!Path.IsPathFullyQualified(dataDirectory)) throw new ArgumentException("Managed SISR data directory must be absolute.");
        ManagedSessionDiagnostics.Prune(dataDirectory);
        string directory = Path.Combine(dataDirectory, "sisr", "sessions", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        string config = Path.Combine(directory, "startup.json"), log = Path.Combine(directory, "SISR.log");
        // Kong v1.16.1 JSON resolver uses flag-name keys, not the read-only API
        // config response's controllerEmulation/runMisc PascalCase structure.
        var values = new Dictionary<string, object>
        {
            ["api.listen_address"] = "127.0.0.1:0", ["log.file"] = log,
            ["window.fullscreen"] = false, ["window.show"] = false
        };
        if (controller != null)
        {
            values["default_controller_type"] = controller.ApiType;
            values["gyro_passthrough"] = controller.GyroPassthrough;
            values["touchpad_passthrough"] = controller.TouchpadPassthrough;
            values["back_button_passthrough"] = controller.BackButtonPassthrough;
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(values);
        using (var stream = new FileStream(config, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        var session = new SisrManagedStartup(directory, config, log) { activity = ManagedSessionDiagnostics.MarkActive(directory), controller = controller };
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
        if (controller != null)
        {
            info.Environment["SISR_DEFAULT_CONTROLLER_TYPE"] = controller.ApiType;
            info.Environment["SISR_GYRO_PASSTHROUGH"] = controller.GyroPassthrough ? "true" : "false";
            info.Environment["SISR_TOUCHPAD_PASSTHROUGH"] = controller.TouchpadPassthrough ? "true" : "false";
            info.Environment["SISR_BACK_BUTTON_PASSTHROUGH"] = controller.BackButtonPassthrough ? "true" : "false";
        }
        return info;
    }

    public static void VerifyEffectiveProfile(SisrControllerProfile? expected, SisrStatusSnapshot status)
    {
        if (expected == null) return;
        expected.Validate();
        if (status.ControllerType != expected.ApiType || status.GyroPassthrough != expected.GyroPassthrough ||
            status.TouchpadPassthrough != expected.TouchpadPassthrough || status.BackButtonPassthrough != expected.BackButtonPassthrough)
            throw new InvalidOperationException("SISR's effective controller configuration does not match this game's requested profile. Game activation was blocked.");
    }
}
