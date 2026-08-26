# PeekDesktop 👀

A lightweight Windows desktop-peek utility based on [shanselman/PeekDesktop](https://github.com/shanselman/PeekDesktop), now maintained here as an independent fork with a native settings window, portable configuration, elevated startup support, and expanded Fly Away behavior.

**Click empty desktop wallpaper or empty taskbar space to reveal the desktop.**

<p align="center">
  <img src="img/demo.gif" alt="PeekDesktop demo" width="900" />
</p>

## About This Fork

This repository has diverged from the original PeekDesktop project and is maintained independently.

The upstream repository remains the origin of the project and may still be reviewed for useful fixes or ideas, but upstream changes are **not synchronized automatically**. Future changes will normally be evaluated and selectively reimplemented in this repository instead of directly merging upstream releases.

Releases, future update checks, and project-specific behavior are intended to use **`shmilyfuu/PeekDesktop`** as the source of truth.

Upstream project: [shanselman/PeekDesktop](https://github.com/shanselman/PeekDesktop)

## Download

📥 **[Download the latest release](https://github.com/shmilyfuu/PeekDesktop/releases/latest)**

Current builds are available for:

| File | Platform |
|------|----------|
| `PeekDesktop-vX.Y.Z-win-x64.zip` | Intel / AMD 64-bit Windows |
| `PeekDesktop-vX.Y.Z-win-arm64.zip` | ARM64 Windows |

Each release ZIP contains only `PeekDesktop.exe`.

- No installer is required.
- No separate .NET installation is required.
- No PDB files are included in release packages.
- The program is portable and keeps its own persistent files under the executable directory.
- Current releases are unsigned, so Windows may display an **Unknown publisher** UAC warning.

## Portable Layout

PeekDesktop keeps its runtime data beside the executable:

```text
PeekDesktop\
├─ PeekDesktop.exe
└─ data\
   ├─ settings.json
   ├─ logs\
   │  ├─ PeekDesktop.log
   │  └─ startup-error.log
   └─ update\
```

Paths are based on the actual directory containing `PeekDesktop.exe`, not the current working directory.

For compatibility with older builds, an existing `%APPDATA%\PeekDesktop\settings.json` may be migrated once into the portable `data` directory. New settings are not written back to AppData.

## Main Features

- **Explorer Show Desktop mode** — uses Windows Explorer's native Show Desktop behavior.
- **Fly Away mode** — animates windows away from the desktop and restores their original placement.
- **Single-monitor Fly Away** — optionally affects only windows assigned to the monitor that was clicked.
- **Configurable Fly Away animation** — separate duration and frame-rate controls.
- **Responsive Fly Away animation** — animation work is performed away from the main message thread with waitable-timer pacing.
- **Desktop click detection** — distinguishes empty wallpaper from desktop icons.
- **Taskbar click detection** — can trigger from empty taskbar space.
- **Double-click option** — optionally require a desktop double-click before peeking.
- **Full-screen / gaming pause** — can suppress peeking while a full-screen application is active.
- **Restore on app switch** — restores hidden windows when switching back to applications.
- **Native Win32 settings window** — all settings can be changed from one persistent window instead of repeatedly reopening the tray menu.
- **Portable configuration and logs** — application-owned files stay under the PeekDesktop directory.
- **Elevated operation** — PeekDesktop requests administrator privileges so it can also manage elevated windows.
- **Elevated Start with Windows** — implemented through Windows Task Scheduler with highest privileges.

## Tray and Settings

PeekDesktop remains a tray application.

### Left-click the tray icon

Opens the native **PeekDesktop Settings** window directly.

### Right-click the tray icon

The tray menu intentionally contains only:

- **Settings**
- **Exit**

The Settings window provides the current options in one place, including:

- Enabled
- Start with Windows
- Require Double-Click
- Peek on Desktop Click
- Peek on Taskbar Click
- Restore All Windows on App Switch
- Pause While Gaming / Full-Screen
- Peek Style
- Fly Away: Only Clicked Monitor
- Animation Duration
- Frame Rate

Update-related controls are currently unavailable while the updater is being redesigned for this fork.

## Fly Away Animation

Fly Away animation timing uses two independent controls:

- **Duration** controls total travel time and therefore perceived animation speed.
- **Frame rate** controls how densely intermediate window positions are updated.

Approximate requested animation frames per direction are:

```text
frames ≈ duration_ms × FPS / 1000
```

The current default is **320 ms / 60 FPS**.

Fly Away can also be limited to the monitor that was clicked. Explorer Show Desktop remains a system-wide Windows behavior.

## Start with Windows and Administrator Privileges

PeekDesktop uses `requireAdministrator`, so manually starting the application requests elevation through UAC.

When **Start with Windows** is enabled, PeekDesktop creates a Windows Task Scheduler entry named:

```text
PeekDesktop Elevated Startup
```

The task runs at user logon with the highest available privileges. Disabling Start with Windows removes the task.

This Task Scheduler entry is the one intentional piece of persistent system state outside the portable PeekDesktop folder.

## Update Status

Automatic update checking is currently **disabled**.

The old upstream updater is intentionally not used because this fork now has its own behavior and release lifecycle. A future updater/checker is expected to inspect releases from:

```text
https://github.com/shmilyfuu/PeekDesktop
```

The exact update strategy has not been finalized yet. See [TODO.md](TODO.md).

## How It Works

PeekDesktop stays lightweight by using native Windows APIs directly:

- `SetWindowsHookEx(WH_MOUSE_LL)` — low-level mouse hook
- `WindowFromPoint` — determines the window under the cursor
- MSAA hit testing (`AccessibleObjectFromPoint`) — distinguishes wallpaper from desktop icons
- UI Automation hit testing — distinguishes empty taskbar space from interactive taskbar controls
- Explorer Show Desktop — native Windows desktop reveal behavior
- `EnumWindows` + `WINDOWPLACEMENT` — captures window placement for Fly Away
- `SetWindowPos` / `SetWindowPlacement` — moves and restores windows
- `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` — observes application switching
- `Shell_NotifyIcon` — system tray integration
- `CreatePopupMenu` / `TrackPopupMenuEx` — tray and selection menus
- Native Win32 window + GDI drawing — Settings UI

The application remains a .NET Native AOT executable and does not depend on WinForms, WPF, or WinUI 3.

## Single-Instance Behavior

PeekDesktop currently allows only one running instance per Windows session through the mutex:

```text
Local\PeekDesktop_SingleInstance
```

This applies even when two copies of `PeekDesktop.exe` are stored in different portable directories.

At present, launching a second copy while another instance is running causes the second process to exit silently. The already-running instance remains active and continues using the `data` directory beside its own executable.

Improving this behavior is tracked in [TODO.md](TODO.md).

## Build from Source

Requirements:

- Windows 10 / 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

The commands below are written for **PowerShell on Windows**.

Clone the repository:

```powershell
git clone https://github.com/shmilyfuu/PeekDesktop.git
cd PeekDesktop
```

### Development build

Use this for normal source builds and local development. It builds the current source tree, including all features present on the checked-out branch or commit.

```powershell
dotnet build src/PeekDesktop/PeekDesktop.csproj
```

Run directly from source:

```powershell
dotnet run --project src/PeekDesktop/PeekDesktop.csproj
```

Run the P/Invoke safety harness:

```powershell
dotnet run --project src/PeekDesktop.InteropHarness/PeekDesktop.InteropHarness.csproj -- 10000
```

### NativeAOT publish build

Use `dotnet publish` to produce the same kind of self-contained NativeAOT executable used by this fork's release workflow. A local publish contains the functionality of the currently checked-out source, but its version metadata and exact binary bytes can differ from an official GitHub Release.

Publish x64:

```powershell
dotnet publish src/PeekDesktop/PeekDesktop.csproj `
  -c Release `
  -r win-x64 `
  --self-contained `
  -p:PublishSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false
```

Publish ARM64:

```powershell
dotnet publish src/PeekDesktop/PeekDesktop.csproj `
  -c Release `
  -r win-arm64 `
  --self-contained `
  -p:PublishSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false
```

The published executable is normally located under:

```text
src\PeekDesktop\bin\Release\net10.0-windows\<runtime>\publish\PeekDesktop.exe
```

For an official GitHub Release, `.github/workflows/build.yml` also supplies explicit semantic-version metadata and packages the resulting `PeekDesktop.exe` into the architecture-specific ZIP files.

## Project Structure

```text
src/PeekDesktop/
├─ Program.cs                 # Entry point and single-instance mutex
├─ DesktopPeek.cs             # Peek state and transitions
├─ MouseHook.cs               # Desktop/taskbar mouse detection
├─ FocusWatcher.cs            # Foreground-window monitoring
├─ WindowTracker.cs           # Fly Away capture, animation, restore
├─ DesktopDetector.cs         # Desktop/taskbar hit testing
├─ NativeSettingsWindow.cs    # Native menu-style settings window
├─ TrayIcon.cs                # Tray behavior and Settings / Exit menu
├─ Win32TrayIcon.cs           # Shell_NotifyIcon wrapper
├─ Win32Menu.cs               # Win32 HMENU wrapper
├─ Win32MessageLoop.cs        # Native message loop
├─ PortablePaths.cs           # Portable data/log/update paths
├─ Settings.cs                # JSON settings persistence
├─ StartupTask.cs             # Elevated Task Scheduler integration
├─ AppUpdater.cs              # Legacy/future updater groundwork; checks disabled
├─ AppDiagnostics.cs          # Logging
└─ NativeMethods.cs           # Win32 P/Invoke declarations
```

## Maintenance Policy

This fork is intended to evolve independently.

- Upstream changes may be reviewed as references.
- Useful upstream fixes can be selectively ported after evaluation.
- Upstream releases are not treated as automatic updates for this fork.
- New functionality and fixes are developed and released from this repository.
- Future update checks should target this repository's Releases rather than the upstream repository.

## Roadmap

See **[TODO.md](TODO.md)** for deferred improvements and ideas.

## Credits

PeekDesktop was originally created by [Scott Hanselman](https://github.com/shanselman) in [shanselman/PeekDesktop](https://github.com/shanselman/PeekDesktop).

This fork retains the original project's core concept and much of its native Windows foundation while maintaining a separate feature and release path.

## License

[MIT](LICENSE)
