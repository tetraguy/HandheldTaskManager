# Handheld Task Manager

<img width="1099" height="733" alt="image" src="https://github.com/user-attachments/assets/7a5e03c3-e399-45ef-bd2f-6f08e5a6bdc4" />

A touch- and controller-first task manager for Windows handhelds (ROG Ally, Legion Go, MSI Claw,
Steam Deck running Windows, or any PC with an Xbox-style pad). WPF, .NET 8, no NuGet dependencies.
Sibling project to Handheld Explorer and built on the same input and theming code.

## Build

Open `HandheldTaskManager.sln` in Visual Studio 2022 or newer (".NET desktop development" workload)
and press F5. Command line: `dotnet build -c Release`.

## What it shows

- **Apps** - programs with an open window, by window title.
- **Processes** - everything running, grouped by executable name (30 browser processes are one row).
- **Performance** - CPU and memory with 60-sample history, battery, network speed, storage, up time.

CPU, RAM and battery are always visible in the top bar.

## Controls

| Input | Action |
|---|---|
| D-pad / left stick | Move |
| A | Actions: switch to, close, end task, priority, file location, details |
| B | Back, cancel, clear the filter |
| X | End task (asks first) |
| Y | Change sort order (memory, CPU, name) |
| LB / RB | Previous / next tab |
| LT / RT | Page up / down |
| View | Refresh now |
| Menu | Main menu |
| Right stick click | Filter by name |
| Touch | Tap a row for actions, tap a column header to sort |
| Keyboard | Arrows, Enter, Del, Ctrl+F, Ctrl+N, Ctrl+Tab, F5, F10, F11 |

## Notes

- Ending some processes (services, apps started as administrator, anti-cheat) needs elevation:
  Menu > Restart as administrator.
- Core Windows processes (csrss, lsass, svchost, dwm, ...) are protected from End task.
- Per-process CPU can't be read for a few protected system processes without elevation; they show 0%.
- No GPU or temperature readings yet.

Settings are stored in `%AppData%\HandheldTaskManager\settings.json`.
