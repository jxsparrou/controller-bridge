# UWPHook-SISR Bridge

A lightweight C# bridge that coordinates the lifecycles of [UWPHook](https://github.com/BrianLima/UWPHook) and [SISR](https://github.com/Alia5/SISR) (Steam Input System Redirector). 

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
- **Sleek Settings GUI:** A clean, dark-themed settings interface that opens when the program is launched without arguments.
- **Steam Shortcut Automation:** Automatically scans your Steam profile userdata directories to locate `shortcuts.vdf` files, listing UWP games and enabling one-click shortcut redirection to `uwphook-bridge.exe` or restoration back to `UWPHook.exe`.
- **Windowless Game Launching:** Compiles as a Windows GUI application (`/target:winexe`), running silently in the background when starting UWP games without open command prompt windows.
- **Auto-Detection:** Automatically scans typical installation paths (in Local AppData and Program Files) to locate `SISR.exe` and `UWPHook.exe` on first run.
- **Easy Configuration:** Creates a `uwphook-bridge.cfg` file for settings persistence.
- **Robust Argument Forwarding:** Passes through complex arguments from Steam directly to UWPHook.
- **Troubleshooting Friendly:** Writes clean execution logs to `uwphook-bridge.log`.

## Setup Instructions

### 1. Installation
1. Compile or download `uwphook-bridge.exe` and place it in a folder of your choice (e.g. `C:\Users\<YourUsername>\Documents\uwphook-bridge`).
2. Run `uwphook-bridge.exe` once with no arguments. The sleek **Settings GUI** will launch automatically.

### 2. Configure Paths
Verify the detected paths in the path configuration boxes:
- **SISR Path**: Path to `SISR.exe`
- **SISR Arguments**: Optional launch arguments for SISR.
- **UWPHook Path**: Path to `UWPHook.exe`
Click **Save Path Config & Close** to persist your settings in `uwphook-bridge.cfg`.

### 3. Automate Steam Shortcuts
To redirect your Steam shortcuts to run through the bridge:
1. Ensure the Steam client is completely closed. (The GUI provides a convenient **Close Steam Client** button if it is running).
2. Check the boxes next to the UWP games you wish to route through the bridge.
3. Click **Bridge Selected to EXE**.
4. Click **Refresh** to verify the status shows "Bridged via Bridge". You can now start Steam and launch your games!

*(Optional: Click **Restore Selected to UWP** to revert shortcuts to launch directly through UWPHook.)*

### 4. Manual Steam Shortcuts Configuration (Alternative)
If you prefer to configure a shortcut manually:
1. Open **Steam**.
2. Right-click your UWP game shortcut (created by UWPHook) -> **Properties**.
3. In the **Target** field, replace the UWPHook path with:
   `"C:\Path\To\uwphook-bridge.exe"`
4. Keep the **Arguments / Launch Options** (the long AUMID string starting with `Microsoft...` or similar) **completely unchanged**.

## Building from Source
This project uses the standard C# compiler (`csc.exe`) built into Windows. You do not need to install Visual Studio, .NET SDKs, or external compilation tools.

To build the executable:
1. Open PowerShell in the project directory.
2. Run the compilation script:
   ```powershell
   powershell.exe -ExecutionPolicy Bypass -File .\build.ps1
   ```

## Changelog

### v0.2.0
- **Added Settings GUI:** Introduced a new, dark-themed WinForms settings interface for configuring paths and managing Steam shortcuts.
- **Added Steam Shortcut Redirection Automation:** Implemented a binary VDF (Valve Data Format) parser and serializer to programmatically read and modify `shortcuts.vdf` profiles.
- **Added Steam Process Detection:** Real-time checking for whether the Steam client is running, preventing data loss by warning users and offering to close Steam before editing shortcuts.
- **Expanded Build Configuration:** Added `System.Drawing` and `System.Core` assembly references to `build.ps1`.
- **Basename Shortcut Matching:** Optimized shortcut matching to support custom installation directories and older bridge executable targets.

### v0.1.0
- **Initial Release:** Lightweight, zero-dependency C# console application acting as a bridge between UWPHook and SISR to coordinate lifecycles.
- **Silent Background Execution:** Runs windowless (`/target:winexe`) alongside UWP games.
- **Auto-Detection:** Automatic path discovery scan for typical Local AppData/Program Files directories.
- **Manual Config Integration:** Configuration parsed from `uwphook-bridge.cfg` on launch.

---

## Credits
This project coordinates two outstanding utilities:
- **UWPHook** by [BrianLima](https://github.com/BrianLima) — The wrapper utility that links Windows Store/UWP applications to Steam.
- **SISR** (Steam Input System Redirector) by [Alia5](https://github.com/Alia5) — The controller redirector that maps Steam Input layouts to system-level virtual gamepads.

## License
This project is licensed under the GNU General Public License version 3 (GPL-3.0) - see the [LICENSE](file:///C:/Users/john/.gemini/antigravity/worktrees/uwphook-bridge/add-gui-path-automation/LICENSE) file for details.

