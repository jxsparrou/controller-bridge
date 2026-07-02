# Controller Bridge

**Play Xbox Game Pass and Windows Store games on Steam with full controller support — automatically.**

Controller Bridge is a lightweight Windows utility that connects UWP games directly to Steam while coordinating [SISR](https://github.com/Alia5/SISR) (Steam Input System Redirector) in the background so you don't have to manage it manually.

## About This Project
This is a personal hobby project created entirely using AI coding assistants to solve a specific problem I personally encountered. 

I am currently open to suggestions for a new name for this utility! If you have a creative idea, feel free to open an issue or share your thoughts.

## The Problem

If you play Xbox Game Pass or Windows Store (UWP) games through Steam, you've probably run into this:

- Steam cannot natively launch or monitor UWP games (which are sandboxed and lack a standard `.exe` target).
- **SISR** (Steam Input System Redirector) makes Steam's controller configurations work with UWP games, but you have to start and stop it yourself every time you play.

Without this bridge, you'd need to manually launch SISR before every gaming session and remember to close it afterward. If you forget, your controller input stays redirected system-wide, which can cause issues with other apps.

## The Solution

This bridge does everything for you. When you launch a UWP game from Steam:

1. ✅ **Starts SISR** silently in the background
2. ✅ **Launches your game** directly via Windows Shell COM interfaces
3. ✅ **Waits and monitors** the UWP game process (including launcher-spawned processes)
4. ✅ **Stops SISR** automatically when you close the game
5. ✅ **Exits cleanly** so Steam knows your session is over

You just click "Play" in Steam and everything works.

## Features

- **Built-in UWP App Scanner** — Scans all installed UWP/Game Pass games on your system and lets you add them directly to Steam with one click.
- **Settings GUI** — A clean, dark-themed settings window to configure your SISR path, scan UWP apps, and manage your Steam shortcuts from one place.
- **Steam Shortcut Redirection** — Easily direct existing UWP shortcuts to run through this bridge.
- **Zero Dependencies** — Built using native C# and Windows COM interfaces. Compiles with the C# compiler that's already on your Windows PC. No extra SDKs or installers required.
- **Silent Operation** — Runs windowless in the background during gameplay.
- **Logging** — Writes a clean log file (`uwphook-bridge.log`) for troubleshooting.

## Quick Start

### Step 1: Download or Build
Download `uwphook-bridge.exe` from the Releases page, or build it yourself (see [Building from Source](#building-from-source) below).

Place it in a permanent folder, for example:
```
C:\Users\<YourUsername>\Documents\uwphook-bridge\
```

### Step 2: Open the Settings GUI
Double-click `uwphook-bridge.exe` (or run it with no arguments). The settings window will open.

1. **Verify your SISR path** — The program will try to auto-detect where SISR is installed. If the path is wrong, click **Browse...** to fix it.
2. **Click "Save Path Config & Close"** to save.

### Step 3: Add/Bridge Your Games
> **Important:** Steam must be closed for these steps. The program will warn you if Steam is running and offer to close it for you.

1. Open `uwphook-bridge.exe` again.
2. **To Add New Games:** Click **"Scan & Add UWP Apps"**. In the window that opens, click **"Scan for UWP Apps"**, select the games you want to add, and click **"Add Selected to Steam"**.
3. **To Bridge Existing Games:** Select the games in the list and click **"Bridge Selected"**.
4. **To Remove Games:** Select games in the list and click **"Remove from Steam"**.

Restart Steam, and your games are ready to play!

## Building from Source
This project compiles with the C# compiler (`csc.exe`) that ships with every Windows installation. No extra tools needed.

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\build.ps1
```

## Changelog

### v0.3.0
- **Embedded UWPHook Functionality:** Completely removed the external UWPHook dependency. All UWP launching is now done natively using Windows COM interfaces (`IApplicationActivationManager`).
- **Built-in UWP Scanner:** Added a new GUI form to scan and add UWP games directly to Steam.
- **Code Refactoring:** Split the single-file codebase into manageable C# source files (`Program.cs`, `AppManager.cs`, `VdfParser.cs`, `SteamShortcuts.cs`, `SettingsForm.cs`, `UWPScanForm.cs`).
- **Remove Shortcuts:** Added capability to remove UWP shortcuts directly from Steam.

### v0.2.0
- **Settings GUI:** Dark-themed WinForms interface for configuring paths and managing Steam shortcuts.
- **Steam Shortcut Automation:** Binary VDF parser/serializer to read and modify Steam's `shortcuts.vdf` files programmatically.
- **Steam Process Detection:** Warns when Steam is running and offers to close it before editing shortcuts.
- **Flexible Matching:** Detects shortcuts regardless of where UWPHook or the bridge is installed.

### v0.1.0
- **Initial Release:** Console-mode bridge that coordinates SISR and UWPHook lifecycles.
- **Silent Execution:** Runs windowless alongside UWP games.
- **Auto-Detection:** Scans common installation paths for SISR and UWPHook.
- **Config File:** Settings stored in `uwphook-bridge.cfg`.

---

## Credits
This project coordinates:
- **SISR** (Steam Input System Redirector) by [Alia5](https://github.com/Alia5) — Redirects Steam Input to system-level virtual gamepads for UWP games. Licensed under [GPL-3.0](https://github.com/Alia5/SISR).
- The COM app launching mechanism is based on the design approach utilized in **UWPHook** by [BrianLima](https://github.com/BrianLima) (licensed under MIT).

## License
This project is licensed under the [GNU General Public License v3.0](LICENSE) — see the [LICENSE](LICENSE) file for the full text.
