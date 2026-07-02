# UWPHook-SISR Bridge

**Play Xbox Game Pass and Windows Store games on Steam with full controller support — automatically.**

UWPHook-SISR Bridge is a lightweight Windows utility that connects two great open-source tools — [UWPHook](https://github.com/BrianLima/UWPHook) and [SISR](https://github.com/Alia5/SISR) — so you don't have to manage them manually.

## The Problem

If you play Xbox Game Pass or Windows Store (UWP) games through Steam, you've probably run into this:

- **UWPHook** lets you add UWP games to your Steam library, but it doesn't handle controller remapping.
- **SISR** (Steam Input System Redirector) makes Steam's controller configurations work with UWP games, but you have to start and stop it yourself every time you play.

Without this bridge, you'd need to manually launch SISR before every gaming session and remember to close it afterward. If you forget, your controller input stays redirected system-wide, which can cause issues with other apps.

## The Solution

This bridge does everything for you. When you launch a game from Steam:

1. ✅ **Starts SISR** silently in the background
2. ✅ **Launches your game** through UWPHook with all the right arguments
3. ✅ **Waits** for you to finish playing
4. ✅ **Stops SISR** automatically when you close the game
5. ✅ **Exits cleanly** so Steam knows your session is over

You just click "Play" in Steam and everything works.

## Features

- **Settings GUI** — A clean, dark-themed settings window opens when you run the program. Configure your paths and manage your Steam shortcuts from one place.
- **One-Click Steam Setup** — Automatically detects your UWP games in Steam and lets you redirect them through the bridge with a single click. No need to manually edit Steam shortcut properties.
- **Zero Dependencies** — Built with the C# compiler that's already on your Windows PC. No Visual Studio, no NuGet packages, no .NET SDK required.
- **Silent Operation** — Runs invisibly in the background during gameplay. No console windows, no popups.
- **Auto-Detection** — Finds SISR and UWPHook on your system automatically.
- **Logging** — Writes a clean log file (`uwphook-bridge.log`) for troubleshooting.

## Quick Start

### Step 1: Download or Build
Download `uwphook-bridge.exe` from the [Releases](https://github.com/jxsparrou/controller-bridge/releases) page, or build it yourself (see [Building from Source](#building-from-source) below).

Place it in a permanent folder, for example:
```
C:\Users\<YourUsername>\Documents\uwphook-bridge\
```

### Step 2: Open the Settings GUI
Double-click `uwphook-bridge.exe` (or run it with no arguments). The settings window will open.

1. **Verify your paths** — The program will try to auto-detect where SISR and UWPHook are installed. If the paths are wrong, click **Browse...** to fix them.
2. **Click "Save Path Config & Close"** to save.

### Step 3: Redirect Your Steam Shortcuts
> **Important:** Steam must be closed for this step. The program will warn you if Steam is running and offer to close it for you.

1. Open `uwphook-bridge.exe` again.
2. In the **Steam Shortcut Redirection** section, you'll see a list of your UWP games that were added to Steam via UWPHook.
3. Check the games you want to route through the bridge.
4. Click **"Bridge Selected to EXE"**.

That's it! Your Steam shortcuts now point to the bridge instead of UWPHook directly. The next time you launch one of those games from Steam, SISR will start and stop automatically.

> **To undo:** Select the games and click **"Restore Selected to UWP"** to revert them back to launching through UWPHook directly.

### Manual Setup (Alternative)
If you prefer to configure Steam shortcuts by hand:
1. Right-click a UWP game in Steam → **Properties**.
2. Change the **Target** to point to `uwphook-bridge.exe`.
3. Leave the **Launch Options** (the AUMID string) exactly as they are.

## Building from Source
This project compiles with the C# compiler (`csc.exe`) that ships with every Windows installation. No extra tools needed.

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\build.ps1
```

## Changelog

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
This project coordinates two excellent open-source utilities:
- **UWPHook** by [BrianLima](https://github.com/BrianLima) — Adds Windows Store/UWP games to your Steam library. Licensed under [MIT](https://github.com/BrianLima/UWPHook/blob/master/LICENSE).
- **SISR** (Steam Input System Redirector) by [Alia5](https://github.com/Alia5) — Redirects Steam Input to system-level virtual gamepads for UWP games. Licensed under [GPL-3.0](https://github.com/Alia5/SISR).

## License
This project is licensed under the [GNU General Public License v3.0](LICENSE) — see the [LICENSE](LICENSE) file for the full text.
