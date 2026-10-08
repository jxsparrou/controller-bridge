using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using SBridge.Core;
using SBridge.Launching;

namespace SBridge.Providers;

[SupportedOSPlatform("windows")]
internal sealed class WindowsEpicActivation : IGameActivation
{
    private readonly Func<ProcessStartInfo, Process?> start;
    public WindowsEpicActivation(Func<ProcessStartInfo, Process?>? start = null) => this.start = start ?? Process.Start;
    public InitialGameObservation Activate(LegacyLaunchRequest request, Action<string> log)
    {
        if (request.Kind != LegacyLaunchKind.Epic || request.Arguments.Count != 0)
            throw new ArgumentException("Epic activation requires an Epic target without extra arguments.");
        EpicLaunchIdentity.ValidateTarget(request.Target);
        log("Launching Epic catalog game via launcher: " + request.Target);
        using var launcher = start(new ProcessStartInfo(request.Target) { UseShellExecute = true });
        // A protocol handler may return an already-running launcher. Dispose its
        // handle; never adopt it as the game or end a session when it exits.
        return new InitialGameObservation(null, null);
    }
}
