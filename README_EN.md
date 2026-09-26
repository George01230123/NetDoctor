# NetDoctor (夕颜若雪网络工具)

[中文](README.md) | [English](README_EN.md)

An all-in-one **Windows 10 / 11 system toolkit**: network diagnostics & repair, Windows debloat/tuning, junk cleanup, app/service/startup management, and hardware detection with benchmarks.

Built with .NET 10 + WinForms. Shipped as a **single self-contained executable** — no runtime installation required on the target machine. Custom-drawn dark theme, borderless window.

```
Overview · Network Check · Repair · Network Tuning · Windows Tuning · System Tools · Hardware · Log
```

> **Note:** The UI is currently Chinese-only. This document describes every page so you know what each one does.

---

## Download

Grab `夕颜若雪网络工具.exe` (~47 MB) from the [Releases](../../releases) page and **just double-click it** — no .NET installation needed.

It will request administrator privileges on first run: repair, tuning, service, startup and cleanup operations all require elevation.

---

## Features

### Network

| Page | What it does |
|---|---|
| **Network Check** | Pinpoints *where* the connection breaks, layer by layer: loopback → gateway → public IP → DNS → HTTP. Tests every DNS server configured on your adapters with real UDP queries and reports round-trip time. **Proxy probe**: if the system proxy points at `127.0.0.1` but nothing is listening on that port, it tells you outright that this is why every page fails to load. Also checks hosts, Winsock catalog, network services, firewall, default routes and NIC power saving. Read-only — changes nothing. Export report / copy summary. |
| **Repair** | One-click fix (flush DNS → repair 169.254 addresses → clear ARP → reset proxy → restart network services); 10 individually selectable fixes (including Winsock and TCP/IP stack reset); symptom-based quick actions; deep repair with optional reboot. **Writes a state snapshot before touching anything.** |
| **Network Tuning** | Benchmarks 12 public DNS providers concurrently and switches to the fastest; TCP parameter tuning (latency-first / throughput-first / restore defaults); disable NIC power saving; list top processes by connection count. |

### Windows Tuning

**151 tweaks** across 7 categories (Performance 45 / Privacy 34 / Appearance & Explorer 29 / System 14 / Edge 12 / Update 7 / Security 10).

- Each row shows the **live state** read straight from the registry (applied / not applied) plus the **current value**
- Four operation types: registry writes (including the Wow64 view), service start type (also stops the service when disabling), effect commands (`powercfg` / `net` / `netsh wfp`), and shell change notifications
- "Select recommended only" automatically excludes anything that weakens system protection (disabling the firewall, memory integrity, VBS, setting UAC to never notify, …). Those rows are highlighted in red and unchecked by default
- Snapshots the **original registry values** before applying, supports per-item and full restore to Windows defaults
- Definitions live in `src/Data/Optimizations.xml` — a project-specific format you can edit and extend

### System Tools

| Sub-page | What it does |
|---|---|
| **Junk Cleanup** | 24 cleanup targets with **live size measurement** before you decide. Recycle Bin and the .NET NativeImages cache are unchecked by default (deleting the latter slows the first launch of .NET apps). Locked files are skipped and reported honestly; the tool never deletes its own log directory. |
| **App Management** | Enumerates all Appx packages with **real disk usage**, sorted largest first; batch uninstall including provisioned package removal, then verifies each one actually disappeared. |
| **Services** | Enumerates services and reads the real start type from the `Start` registry value; start/stop/change start type; ships a list of 20 safe-to-disable services with per-service explanations (missing ones are skipped automatically). |
| **Startup** | Registry Run/RunOnce (HKCU + HKLM + WOW6432Node), Startup folders, and scheduled tasks. Disabling uses the official Windows **StartupApproved** mechanism — **the original registry entries are never modified**, so it is always reversible. |
| **Activation** | Ships **no** activation script. It only locates an existing `MAS_AIO_CN.cmd` on your machine and can launch it; also shows current license status via `slmgr /dli`. |

### Hardware

| Sub-page | What it does |
|---|---|
| **Hardware Info** | Everything comes from **WMI/CIM + the registry** — collected locally, nothing uploaded: system/board/BIOS/UUID, CPU (cores, threads, caches, load), memory (auto-detected DDR generation + **single/dual-channel detection**), GPU (**VRAM**), disks (**SMART health / temperature / power-on hours / wear**), partitions, monitors (**EDID panel model, manufacture date, physical size**), NICs, audio devices, battery health, thermal zones. Export as a full report or a compact format. |
| **Tool Launcher** | Scans a locally installed 图吧工具箱 (Tuba Toolbox) `tools` directory, lists the original third-party tools by category and launches them directly — **nothing is copied or modified**. |
| **Benchmarks** | CPU benchmark (SHA256 single/multi-thread throughput + floating point, reports multi-thread scaling factor); sequential disk read/write test (temp file removed automatically). |
| **Screen Test** | Full-screen solid colors for dead pixels / backlight bleed, grayscale ramp for banding, three-level grids for geometry and moiré, multi-size text sharpness test. ESC to exit. |

---

## Build

Requires the **.NET 10 SDK** on Windows.

```powershell
git clone https://github.com/xiyanruoxue/NetDoctor.git
cd NetDoctor

# build and run
dotnet build src/NetDoctor.csproj -c Release
.\src\bin\Release\net10.0-windows\夕颜若雪网络工具.exe

# publish the single-file self-contained exe
.\publish.ps1
```

`publish.ps1` writes the single-file executable to `publish/`.

### Self-test

```powershell
dotnet build selftest/SelfTest.csproj -c Release
.\selftest\bin\Release\net10.0-windows\NetDoctorSelfTest.exe
```

24 groups / 44 assertions, all running against real hardware and network (adapters, connectivity, DNS, services, startup items, Appx, scheduled tasks, hardware, benchmarks, cleanup measurement). A non-zero exit code means something failed.

---

## Project layout

```
NetDoctor/
├── src/
│   ├── Program.cs  Theme.cs  app.manifest
│   ├── Core/            # engine layer
│   │   ├── NetworkDiag.cs        diagnostics
│   │   ├── NetworkRepair.cs      repairs + snapshots
│   │   ├── NetworkOptimizer.cs   DNS benchmarks / TCP tuning
│   │   ├── WindowsOptimizer.cs   151-tweak engine
│   │   ├── JunkCleaner.cs        cleanup
│   │   ├── SystemItems.cs        Appx / services / startup / tasks
│   │   ├── HardwareInfo.cs       hardware inventory
│   │   ├── ToolLauncher.cs       tool launcher + benchmarks
│   │   ├── Cmd.cs                process exec + logging + embedded script extraction
│   │   └── nettop.ps1  sysitems.ps1
│   ├── UI/              # 8 pages + custom-drawn controls
│   └── Data/Optimizations.xml    tweak definitions (own format, editable)
├── selftest/            self-test project (reuses src files)
├── docs/使用说明.md      full Chinese manual
├── publish.ps1          one-command publish
└── .github/workflows/   CI: build + self-test + release
```

---

## Runtime directories

The program creates these next to the executable:

```
logs/        run logs (one file per day)
backup/      pre-change snapshots, for manual rollback
.runtime/    extracted embedded PowerShell scripts (created on first run)
```

**Every write operation** leaves a snapshot under `backup/`: proxy settings, TCP parameters, per-adapter configuration, default routes, hosts, and the original registry values of every applied tweak.

---

## Third-party notice

- This program does **not** bundle, call, or modify any third-party optimizer/tool executables.
- The Windows tweak entries, cleanup targets, and system-tool features are all implemented by this project.
- *Hardware → Tool Launcher* only **launches** original tools from a 图吧工具箱 installation already present on your machine; it copies nothing.
- See [NOTICE.md](NOTICE.md) for details.

## Disclaimer

System tuning modifies the registry and service configuration. **Back up important data before use.**
The author is not responsible for any data loss or system instability caused by this tool. Use at your own risk.

## License

[MIT License](LICENSE)
