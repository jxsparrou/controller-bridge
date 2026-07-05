# sBridge

**Play Xbox Game Pass and Windows Store games on Steam with full controller support — automatically.**

sBridge is a lightweight Windows utility that connects UWP games directly to Steam while coordinating [SISR](https://github.com/Alia5/SISR) (Steam Input System Redirector) in the background so you don't have to manage it manually.

## About This Project
This is a personal hobby project created entirely using AI coding assistants to solve a specific problem I personally encountered. 

I am currently open to suggestions for a new name for this utility! If you have a creative idea, feel free to open an issue or share your thoughts.

## The Problem

If you play Xbox Game Pass or Windows Store (UWP) games, you've probably run into these issues when trying to add them to Steam:

- **No Native Steam Input**: UWP/Game Pass games are sandboxed and lack standard target `.exe` files, preventing Steam from hooking into them natively. Because of this, custom Steam Input profiles and non-Xbox controllers (PlayStation DualSense, Nintendo Switch Pro, Steam Controller) will not work out of the box.
- **Manual Redirectors**: Tools like **SISR** (Steam Input System Redirector) solve this by translating Steam Input into system-level virtual gamepads, but they require you to manually launch the redirector before starting the game and remember to close it afterward.

Without an automated bridge, forgetting to close the redirector leaves your controller inputs hijacked system-wide, breaking input in other applications.

## The Solution

This bridge does everything for you. When you launch a UWP game from Steam:

1. ✅ **Starts SISR** silently in the background
2. ✅ **Launches your game** directly via Windows Shell COM interfaces
3. ✅ **Waits and monitors** the UWP game process (including launcher-spawned processes)
4. ✅ **Stops SISR** automatically when you close the game
5. ✅ **Exits cleanly** so Steam knows your session is over

You just click "Play" in Steam and everything works.

## Features

- **Built-in UWP App Scanner** — Fast scanning of all UWP/Game Pass games on your system, allowing you to add them directly to Steam.
- **Add Custom non-UWP Games** — Support importing standard `.exe` executables alongside UWP apps, allowing you to wrap any game to coordinate SISR automatically.
- **Settings Tabbed GUI** — A single, consolidated interface to manage existing shortcuts, scan and add new UWP games, add custom games, and configure settings.
- **Per-Game SISR Profiles** — Enable or disable SISR on a per-game basis directly in the UI instead of relying on a global toggle.
- **Watch Process Name Overrides** — Easily override which process name to track, allowing the bridge to support games with complex launch chains, DRM launchers (like EA Desktop / Ubisoft Connect), and slow-loading anti-cheat clients.
- **SteamGridDB Artwork Integration** — Automatically downloads grids, heroes, logos, and list icons from SteamGridDB on import when an API key is configured.
- **One-Click UWPHook Migration** — Effortlessly migrate existing UWPHook shortcuts to use the bridge with a single click.
- **Optional SISR Integration** — Toggle SISR controller redirection on or off. With SISR disabled, the bridge functions as a standalone, lightweight UWP launcher (a pure UWPHook replacement).
- **Zero Dependencies** — Built with native C# and Windows COM interfaces. Compiles with the default Windows C# compiler.
- **Silent & Invisible** — Runs silently in the background during gameplay with zero active overhead.

## Quick Start

### Step 1: Place the Executable
Place `sBridge.exe` in a permanent folder, for example:
```
C:\Users\<YourUsername>\Documents\sBridge\
```

### Step 2: Open the Settings GUI
Double-click `sBridge.exe` (with no arguments).

1. **Configure SISR (optional)** — Auto-detects SISR path. Toggle SISR support on/off.
2. **Configure SteamGridDB (optional)** — Enter your SteamGridDB API key to auto-pull artwork.
3. Click **"Save & Close"** to save configuration.

### Step 3: Manage Your Games
> **Note:** Steam must be closed to edit shortcuts. The GUI will prompt you to close it.

- **To Add UWP Games:** Go to **Add UWP Games** tab, click **Scan for UWP Apps**, select your games, and click **Add Selected to Steam**.
- **To Add Custom Games:** Go to **Add Custom Game** tab, fill out the game details, select the executable, and click **Add Custom Game to Steam**.
- **To Migrate Old Games:** Click **Migrate From UWPHook** in the path configurations panel to convert old UWPHook shortcuts automatically.
- **To Remove Games:** Go to **Steam Shortcuts** tab, select your games, and click **Remove Selected**.

Restart Steam and play!

## Building from Source
Run the build script using PowerShell:
```powershell
powershell.exe -ExecutionPolicy Bypass -File .\build.ps1
```

## Changelog

### v0.4.0
- **UI/UX Cleanup & Compact Window**: Reduced the settings form height from 780px to 540px to create a more compact, screen-friendly design.
- **Global Settings Tab**: Restructured path configurations (SISR path, arguments, SteamGridDB API key) and status warnings into their own tab page, freeing up main window space.
- **Toggle SISR Action**: Added a new equal-width action button row in the Shortcuts tab, featuring a "Toggle SISR" button that strictly toggles SISR support (Enabled/Disabled) on selected or checked items.
- **Auto-Saving Settings**: Path settings and checkbox integrations now auto-save instantly when textboxes lose focus or checkbox states change, removing the need for a manual "Save" button.
- **Dynamic Selection Prompts**: Per-game override controls are now hidden under a placeholder prompt until a game shortcut is selected.
- **Dynamic Color Coding**: Color-coded the status column in the Steam Shortcuts list (soft green for Enabled, soft red for Disabled, orange for old UWPHook shortcuts).
- **Embedded Executable Icon**: Created a custom sBridge icon and embedded it directly into the compiled binary and window title bar.
- **sBridge Branding**: Consistently updated all window titles, labels, configuration headers, and messages to use "sBridge".

### v0.3.0
- **UWPHook-like Functionality**: Added native UWP launching via COM, completely removing the dependency on external launchers.
- **Add Custom Games**: Added support for standard `.exe` executables to easily wrap any PC game alongside UWP/Game Pass apps.
- **Consolidated UI**: Reorganized layout into a single window with a tabbed interface containing shortcut lists, UWP scan tools, and custom game utilities.
- **Per-Game SISR Settings**: Added the ability to enable/disable SISR support on a per-shortcut basis (Force Enable, Force Disable, or Global Default) inside the shortcut details panel.
- **DRM Launcher & Watch Overrides**: Fixed issues launching games with complex start chains (e.g. EA App/Origin DRM for FC26) by introducing configurable Watch Process name overrides.
- **Robust Process Search**: Extended the startup search timeout window to 90 seconds (polling every 2 seconds) to comfortably accommodate slow anti-cheat clients and DRM wrappers.
- **SteamGridDB Integration**: Added background artwork downloader (portrait grids, heroes, logos, and PNG-converted icons).
- **Standalone UWP Mode**: Added toggle for SISR redirection to run the bridge as a pure UWPHook replacement.
- **One-Click Migration**: Added a button to automatically upgrade old UWPHook shortcuts to the bridge.
- **Optimized Scanning**: Cached Start Menu lookups for an 8x+ speedup, scanning in under 1 second.
- **Executable Rename**: Renamed compiled output to `sBridge.exe` (with configs/logs renamed to `sBridge.cfg`/`sBridge.log`).
- **Bug Fixes**: Fixed path exceptions caused by double quotes in Steam paths and PowerShell pipeline syntax errors.

### v0.2.0
- **Settings GUI**: Dark-themed WinForms interface for configuring paths and managing Steam shortcuts.
- **Steam Shortcut Automation:** Binary VDF parser/serializer to read and modify Steam's `shortcuts.vdf` files programmatically.
- **Steam Process Detection:** Warns when Steam is running and offers to close it before editing shortcuts.
- **Flexible Matching:** Detects shortcuts regardless of where UWPHook or the bridge is installed.

### v0.1.0
- **Initial Release:** Console-mode bridge that coordinates SISR and UWPHook lifecycles.
- **Silent Execution:** Runs windowless alongside UWP games.
- **Auto-Detection:** Scans common installation paths for SISR and UWPHook.
- **Config File:** Settings stored in sBridge.cfg.

---

## Credits
This project coordinates:
- **SISR** (Steam Input System Redirector) by [Alia5](https://github.com/Alia5) — Redirects Steam Input to system-level virtual gamepads for UWP games. Licensed under [GPL-3.0](https://github.com/Alia5/SISR).
- The COM app launching mechanism is based on the design approach utilized in **UWPHook** by [BrianLima](https://github.com/BrianLima) (licensed under MIT).

## License
This project is licensed under the [GNU General Public License v3.0](LICENSE) — see the [LICENSE](LICENSE) file for the full text.
