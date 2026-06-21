# Performance Overlay

A customizable, always-on-top hardware + FPS overlay for **any** game (Steam, Epic,
non-Steam, anything) — like the Steam overlay's perf counter, but fully configurable.

Shows live readouts such as:

```
FPS 164  GPU 37% 58°  CPU 22% 46°  RAM 61%
```

with optional live mini-graphs, in any combination of metrics you choose.

## Features

- **FPS + frame stats** — current, average, **lowest**, **highest**, **1% low**,
  **0.1% low**, and **frametime (ms)**. Read from frame-present events (the same data
  source PresentMon uses) — **no injection, no anti-cheat risk**. With DLSS / FSR
  **Frame Generation** on, the FPS shown includes the generated frames (the "DLSS FPS").
- **Every hardware sensor** your PC exposes — GPU/CPU/RAM/motherboard/disk/fan
  temperatures, load %, clocks, power, voltages, fan RPM, etc. (via LibreHardwareMonitor).
- **Works over any game** running **Borderless / Windowed** (the vast majority of modern
  games, even ones that look fullscreen). See the caveats below.
- **Modern dashboard** with tabs (Sensors / FPS / Style / General):
  - Tick sensors to add them; **drag ≡ to reorder**; **rename** each with a custom label;
    toggle a graph per item.
  - Live **preview** of the overlay, **color pickers**, font + size, °C/°F, opacity,
    horizontal/vertical layout, update speed, graph length.
- **Optional injection mode** (FPS tab, **no-anti-cheat games only**): injects a small hook
  DLL into the foreground game to read frame-present times directly — adds **Vulkan & OpenGL**
  FPS (which the ETW path can't see) and more precise frametimes. Works with **32-bit and
  64-bit** games, fails soft (falls back to ETW), and never inline-patches game code. For
  safety it **automatically turns itself off** the moment the injected game closes, so it
  can't carry over into a different (possibly anti-cheat) game.
- **1:1 WYSIWYG preview** — the dashboard preview is the *actual* overlay control, so what
  you tweak is exactly what you get in-game (graphs, grouping, colors, fonts and all).
- **Click-through** so the overlay never steals mouse input from your game.
- **Drag to move** (uncheck "Lock position"), then lock it for play.
- **Global hotkey** `Ctrl + Shift + O` to show/hide instantly.
- **System tray** icon — double-click for the dashboard, right-click for quick actions.
- **Portable by default** — settings save to a `config\` folder next to the app. You can
  point them anywhere from General → Config location.

## Requirements

- Windows 10 / 11 (64-bit)
- **Nothing else** — the shipped `PerformanceOverlay.exe` is self-contained (the .NET
  runtime is bundled inside it). Copy that one file to any Win10/11 x64 PC and run it.
- **Run as Administrator** — hardware temperatures and FPS capture need it. The app's
  manifest requests elevation automatically (Windows shows a UAC prompt).

## Standalone distribution

`PerformanceOverlay.exe` in the project root is the **standalone build** — a single
~75 MB file with the .NET runtime and all native helpers embedded. To share it, just
copy that one file; no install, no runtime, no extra DLLs. Settings are written to a
`config\` folder next to the exe (or `%LocalAppData%\PerformanceOverlay` if the exe sits
in a read-only location). Rebuild it any time with `publish.bat`.

## Build & Run

```powershell
dotnet build PerformanceOverlay.csproj -c Release
```
Or use Publish.bat to build Self Contained 

Run it from the **Start Menu** ("Performance Overlay"), double-click
`bin\Release\net10.0-windows\PerformanceOverlay.exe`, or use `Launch Overlay.bat`.

On first run it auto-selects FPS + GPU/CPU/RAM load & temperature. Open the dashboard
(tray icon → Open Dashboard) to customize everything.

### Using it over a game

1. Set the game's display mode to **Borderless** (or Windowed).
2. In the dashboard, add the metrics you want and style them.
3. Position the overlay (uncheck **Lock position**, drag it), then re-check it and leave
   **Click-through** on.
4. Launch the game. Toggle the overlay any time with **Ctrl + Shift + O**.

## Caveats (the honest limits of a no-injection overlay)

- **Exclusive-fullscreen games**: a floating window can't draw over a game that bypasses
  the desktop compositor. Switch the game to **Borderless** and it works. (Drawing inside
  true fullscreen needs render-pipeline injection — the RivaTuner/Afterburner technique —
  which can trip anti-cheat; intentionally not done here.)
- **FPS coverage**: frame stats work for **DirectX 9/10/11/12** games (the overwhelming
  majority). Pure **Vulkan/OpenGL** titles present outside DXGI and don't report FPS in
  this version.

## Project layout

| File | Purpose |
|------|---------|
| `HardwareMonitor.cs` | Enumerates & polls all hardware sensors. |
| `FpsMonitor.cs` | ETW present-event capture → FPS / lows / frametime stats. |
| `Settings.cs` | Config model, JSON load/save, portable config location. |
| `OverlayView.xaml(.cs)` | The shared overlay visual (grouping, graphs) used by both the overlay window and the dashboard preview, so they're 1:1. |
| `OverlayWindow.xaml(.cs)` | Transparent, click-through, topmost window that hosts the OverlayView. |
| `DashboardWindow.xaml(.cs)` | Tabbed dashboard: sensor picker, reorder, style, FPS, general. |
| `Theme.xaml` | Dark UI theme (colors + control styles). |
| `IconFactory.cs` | Draws the speedometer tray icon. |
| `NativeMethods.cs` | Win32 interop for click-through + global hotkey. |
| `Injection.cs` | DLL injector (CreateRemoteThread+LoadLibrary) + shared-memory reader. |
| `native\PerfOverlayHook.cpp` | The injected hook DLL (DXGI/OpenGL/Vulkan present capture). Build with `native\build.bat`. |
| `App.xaml.cs` | Startup, polling timer, tray icon, hotkey, metric registry. |
| `app.ico` | App / taskbar / Start-menu icon. |
