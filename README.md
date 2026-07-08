# AtomManDisplay — Windows C# Port

A Windows daemon that drives the built-in serial display on the **AtomMan X7 Ti** mini PC,
feeding real-time hardware metrics (CPU, GPU, RAM, storage, network, weather, and more)
to each tile of the panel.

This project is a **C# / Windows port** of the original Python implementation by
[@RamSet](https://github.com/RamSet/AtomMan). The original targets Linux; this version
runs natively on Windows using .NET and
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
for hardware sensor access.

> **Tested on:** AtomMan X7 Ti (Intel Core Ultra 9 185H · Intel Arc Graphics · Kingston NVMe)

---

## Features

- CPU usage, temperature, and frequency
- GPU usage (Intel Arc D3D load sensor)
- RAM usage and vendor
- SSD temperature and usage
- Fan RPM via SuperIO / embedded controller
- Network throughput (physical interface, WSL/Hyper-V adapters excluded)
- System volume
- Battery level (shows `177` on desktops — no battery)
- Date, time, and day of week
- Current weather + today's lo/hi via [OpenWeatherMap](https://openweathermap.org) *(optional)*

---

## Known Limitations

| Issue | Details |
|---|---|
| **GPU temperature unavailable** | Intel Arc (and some other iGPUs) do not expose a temperature sensor through LibreHardwareMonitor or any standard Windows API. The value is always 0. |
| **Fan RPM unavailable** | The AtomMan X7 Ti's embedded controller (MTBAC board) is not recognised by LHM and does not expose fan speed through any standard Windows API. The value is always 0. |
| **CPU temperature unavailable without PawnIO** | Intel Core Ultra (Meteor Lake) CPUs need the PawnIO kernel driver to read thermal MSRs. See **PawnIO Setup** below — it's a required third-party dependency, not bundled with this project. |

---

## Requirements

| Requirement | Version |
|---|---|
| Windows | 10 / 11 (x64) |
| .NET SDK | 10.0 or later |
| Privileges | **Run as Administrator** (required for LHM kernel driver and PawnIO) |
| [PawnIO](https://pawnio.eu/) driver | Third-party, installed separately — **not bundled** with this project. Required for CPU temperature on Intel Core Ultra (Meteor Lake). See **PawnIO Setup** below. |

---

## PawnIO Setup

Intel Core Ultra (Meteor Lake) CPUs expose thermal sensors only through model-specific
registers that require a signed kernel driver to read. This project relies on
[PawnIO](https://pawnio.eu/) — a small, GPL-2.0 licensed, digitally-signed I/O driver —
for that access. It cannot be bundled inside this repo or the compiled `.exe`: Windows
only loads kernel drivers that come from their own signed installer, so a one-time
install is unavoidable.

1. Download the official installer from the
   [PawnIO.Setup releases page](https://github.com/namazso/PawnIO.Setup/releases/latest)
   (`PawnIO_setup.exe`), or via [pawnio.eu](https://pawnio.eu/).
   You do **not** need to install the full LibreHardwareMonitor desktop app for this.
2. Run the installer once (admin rights required). This registers the `PawnIO`
   service and driver in the Windows Driver Store.
3. That's it — you never need to run the installer again, even across reboots or
   reinstalls of this project. On every launch, the daemon (`PawnIo.EnsureRunning()`
   in [Program.cs](Program.cs)) checks whether the `PawnIO` service is registered and
   starts it automatically if it isn't already running.

Optional silent install (e.g. scripted setup): `PawnIO_setup.exe -install -silent`.

---

## NuGet Dependencies

Restored automatically by `dotnet build`.

| Package | Purpose |
|---|---|
| `LibreHardwareMonitorLib` 0.9.6 | CPU / GPU / storage sensors |
| `System.IO.Ports` | Serial communication with the display |
| `System.Management` | WMI — RAM vendor, disk model |
| `NAudio` | System volume via Windows Core Audio API |

---

## Configuration

Create a `.env` file in the project root (it is gitignored — never committed):

```ini
# Serial port of the AtomMan display
ATOMMAN_PORT=COM3

# OpenWeatherMap — free tier key from https://openweathermap.org/api
# Leave blank to disable weather; new keys can take up to 2 hours to activate.
OW_API_KEY=
OW_LOCATION=Monterrey,MX
OW_UNITS=metric
```

The daemon reads `.env` first, then falls back to system environment variables,
then to the hardcoded defaults.

---

## Build & Run

```powershell
# Clone
git clone https://github.com/your-username/AtomManDisplay.git
cd AtomManDisplay

# Build
dotnet build -c Release

# Run (reads COM port from .env or defaults to COM3)
dotnet run -c Release

# Override port on the command line
dotnet run -c Release -- COM4

# Console dashboard (live sensor readout)
dotnet run -c Release -- --dashboard

# Dump all LHM sensors + WMI scan (useful for diagnosing missing sensors)
dotnet run -c Release -- --debug

# Publish as a single self-contained .exe
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

> **Important:** Always run as Administrator. LHM and PawnIO require kernel-level
> access to read hardware registers.

---

## Weather Setup

1. Register for a free account at <https://openweathermap.org>
2. Go to **API keys** and copy your key
3. Paste it into `.env` as `OW_API_KEY=your_key_here`
4. Wait up to **2 hours** for a newly created key to become active

The daemon uses the free-tier endpoints (`data/2.5/weather` and `data/2.5/forecast`).
No paid subscription is required.

---

## Serial Protocol (reference)

```
ENQ  (display → PC):  AA 05 <SEQ> CC 33 C3 3C
REPLY (PC → display): AA <TileID> 00 <SEQ> {ASCII payload} CC 33 C3 3C
```

| SEQ | TileID | Tile | Payload format |
|---|---|---|---|
| `'2'` | `0x53` | CPU  | `{CPU:<name>;Tempr:<°C>;Useage:<pct>;Freq:<kHz>;Tempr1:<°C>;}` |
| `'3'` | `0x36` | GPU  | `{GPU:<name>;Tempr:<°C>;Useage:<pct>}` |
| `'4'` | `0x49` | MEM  | `{Memory:<vendor>;Used:<GB>;Available:<GB>;Total:<GB>;Useage:<pct>}` |
| `'5'` | `0x4F` | DISK | `{DiskName:<model>;Tempr:<°C>;UsageSpace:<GB>;AllSpace:<GB>;Usage:<pct>}` |
| `'6'` | `0x6B` | DATE | `{Date:yyyy/MM/dd;Time:HH:mm:ss;Week:<0-6>;Weather:<N>;TemprLo:<°C>,TemprHi:<°C>,Zone:<city>,Desc:<text>}` |
| `'7'` | `0x27` | NET  | `{SPEED:<rpm>;NETWORK:<rx>,<tx>}` |
| `'9'` | `0x10` | VOL  | `{VOLUME:<0-100>}` |
| `'<'` | `0x1A` | BAT  | `{Battery:<0-100>}` — `177` means no battery (desktop) |

---

## Running as a Windows Service (optional)

Use [NSSM](https://nssm.cc/download) to run the daemon at startup:

```powershell
nssm install AtomMan "C:\path\to\AtomManDisplay.exe"
nssm set AtomMan AppDirectory "C:\path\to\"
nssm set AtomMan ObjectName LocalSystem   # grants administrator privileges
nssm start AtomMan
```

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| CPU temperature is 0 | Run as Administrator; install the PawnIO driver — see **PawnIO Setup** above |
| GPU temperature is 0 | Known limitation — Intel Arc does not expose temperature through any standard API |
| Fan RPM is 0 | Known limitation — embedded controller not recognised on some boards (e.g. MTBAC) |
| Port not found | Check Device Manager for the correct COM port number and update `ATOMMAN_PORT` in `.env` |
| Display shows nothing | Check that the unlock phase prints "Display activated" in the console |
| Weather always offline | Verify `OW_API_KEY` in `.env`; new keys take up to 2 h to activate |
| Volume shows -1 | Ensure the Windows Audio service is running |

---

## Credits

- Original Python implementation: [@RamSet/AtomMan](https://github.com/RamSet/AtomMan)
- Hardware monitoring: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
- Weather data: [OpenWeatherMap](https://openweathermap.org)
