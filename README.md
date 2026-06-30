# UWPHook-SISR Bridge

A lightweight, zero-dependency C# bridge that coordinates the lifecycles of [UWPHook](https://github.com/BrianLima/UWPHook) and [SISR](https://github.com/Alia5/SISR) (Steam Input System Redirector). 

This bridge allows you to play Windows Store / Xbox Game Pass UWP games through Steam with Steam Input controller support fully redirected at the system level.

## Why this exists
UWPHook acts as a proxy to launch and monitor UWP games in Steam. SISR redirects Steam Input configurations so you can use controllers (like the Steam Controller or Steam Deck) in those games. 

Normally, running both requires writing custom launch scripts or maintaining multiple processes. This bridge automates the coordination:
1. **Cleans up** any lingering SISR or VIIPER instances at launch.
2. **Starts SISR** silently in the background.
3. **Launches UWPHook** with the game's original AUMID (and forwards all other arguments).
4. **Monitors the game session** and waits for UWPHook to exit.
5. **Kills SISR and VIIPER** immediately when the game closes to return your controller/input state to normal.
6. **Exits cleanly** to notify Steam that you have finished playing.

## Features
- **Windowless Execution:** Compiles as a Windows GUI application (`/target:winexe`), running silently in the background without opening distracting command prompt windows.
- **Auto-Detection:** Automatically scans typical installation paths (in Local AppData and Program Files) to locate `SISR.exe` and `UWPHook.exe`.
- **Easy Configuration:** Creates a `uwphook-bridge.cfg` configuration file on the first run for manual path adjustments.
- **Robust Argument Forwarding:** Pass through complex arguments from Steam directly to UWPHook, preventing `IndexOutOfRangeException` crashes.
- **Troubleshooting Friendly:** Writes clean execution logs to `uwphook-bridge.log`.

## Setup Instructions

### 1. Installation
1. Compile or download `uwphook-bridge.exe` and place it in a folder of your choice (e.g. `C:\Users\<YourUsername>\Documents\uwphook-bridge`).
2. Run `uwphook-bridge.exe` once with no arguments. It will:
   - Create a default `uwphook-bridge.cfg` configuration file.
   - Display a dialog showing the detected paths for SISR and UWPHook.
   - Open the directory containing the config file.

### 2. Verify Config Paths
Open `uwphook-bridge.cfg` in a text editor and ensure the paths to `SISR.exe` and `UWPHook.exe` match your setup. The defaults are:
```ini
SisrPath=C:\Users\<YourUsername>\AppData\Local\SISR\SISR.exe
SisrArguments=
UwpHookPath=C:\Users\<YourUsername>\AppData\Roaming\Briano\UWPHook\UWPHook.exe
LogEnabled=true
```

### 3. Configure Steam Shortcuts
To route a UWP game through the bridge:
1. Open **Steam**.
2. Right-click your UWP game shortcut (created by UWPHook) -> **Properties**.
3. In the **Target** field, replace:
   `"C:\Users\<YourUsername>\AppData\Roaming\Briano\UWPHook\UWPHook.exe"`
   with:
   `"C:\Path\To\uwphook-bridge.exe"`
4. Keep the **Arguments / Launch Options** (the long AUMID string starting with `Microsoft...` or similar) **completely unchanged**.

Now, launching the game from Steam will automatically spin up SISR + VIIPER, start the game, and cleanly close them when you exit!

## Building from Source
This project uses the standard C# compiler (`csc.exe`) built into Windows. You do not need to install Visual Studio, .NET SDKs, or external compilation tools.

To build the executable:
1. Open PowerShell in the project directory.
2. Run the compilation script:
   ```powershell
   powershell.exe -ExecutionPolicy Bypass -File .\build.ps1
   ```
