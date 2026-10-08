using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using SBridge.Launching;
using SBridge.Sessions;

namespace SBridge.Providers;

[SupportedOSPlatform("windows")]
internal sealed class WindowsWin32Activation : IGameActivation
{
    public InitialGameObservation Activate(LegacyLaunchRequest request, Action<string> log)
    {
        log("Launching custom game: Path=" + request.Target + ", Args=" + WindowsCommandLine.Join(request.Arguments));
        try
        {
            Process? process = Process.Start(request.CreateWin32StartInfo());
            if (process == null) return new InitialGameObservation(null, null);
            log("Custom game launched with PID: " + process.Id);
            var tracked = WindowsTrackedGameProcess.TryRetain(process, ProcessMatcher.ExecutableName(request.Target), Path.GetFullPath(request.Target));
            return new InitialGameObservation(tracked, tracked?.Observation);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException("Failed to launch custom game: " + ex.Message, ex);
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsPackagedActivation : IGameActivation
{
    public InitialGameObservation Activate(LegacyLaunchRequest request, Action<string> log)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Packaged activation must run on the STA launch thread.");
        log("Launching UWP app via COM: AUMID=" + request.Target + ", Args=" + WindowsCommandLine.Join(request.Arguments));
        var manager = (IApplicationActivationManager)new NativeActivationManager();
        WindowsTrackedGameProcess? tracked = null;
        try
        {
            // Native contract returns HRESULT, not an unmanaged pointer/retval.
            int result = manager.ActivateApplication(request.Target, WindowsCommandLine.Join(request.Arguments), 0, out uint id);
            Marshal.ThrowExceptionForHR(result);
            log("Packaged app launched with PID: " + id);
            if (id != 0)
            {
                try { tracked = WindowsTrackedGameProcess.TryRetain(Process.GetProcessById((int)id)); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                { log("Initial packaged process could not be retained: " + ex.Message); }
                // Preserve the existing short foreground initialization delay,
                // after retaining the handle and before returning launch evidence.
                Thread.Sleep(2000);
                try
                {
                    using var process = Process.GetProcessById((int)id);
                    if (process.MainWindowHandle != IntPtr.Zero) SetForegroundWindow(process.MainWindowHandle);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                { log("Could not bring packaged process to foreground: " + ex.Message); }
            }
            return new InitialGameObservation(tracked, tracked?.Observation);
        }
        catch (Exception ex)
        {
            tracked?.Dispose();
            throw new InvalidOperationException("Failed to launch packaged app: " + ex.Message, ex);
        }
        finally { Marshal.FinalReleaseComObject(manager); }
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class NativeActivationManager { }

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr items, out uint processId);
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
