# GPU VRAM Monitor

GPU VRAM Monitor is a small Windows desktop overlay and tray utility for
watching NVIDIA GPU memory-junction temperature, core temperature, board power,
core clock, radiator fan RPM, and power-limit state while running local AI,
CUDA, rendering, or other GPU-heavy workloads.

The first supported target is an RTX 3090 Neptune-style water-cooled setup with
the radiator fan connected to the motherboard `PUMP_FAN1` header. The code is
kept intentionally compact so other users can adapt the sensor names, fan
header, and fan curve for their own hardware.

## Why This Exists

GDDR6X memory-junction temperature is easy to miss while the GPU core
temperature still looks safe. This project keeps that number visible on the
desktop, adds a simple fan curve, and exposes two common NVIDIA power-limit
presets for quick thermal control during long local AI runs.

## Features

- Always-on-top desktop overlay with VRAM, core temperature, power, clock, fan,
  and power-limit telemetry.
- System tray icon that changes color based on VRAM temperature.
- Manual fan slider plus automatic fan curve with hysteresis to reduce fan
  speed oscillation.
- Radiator fan RPM and control through `LibreHardwareMonitorLib`.
- NVIDIA power-limit presets through `nvidia-smi`.
- Single-file Windows Forms implementation that is easy to audit and modify.

## Current Hardware Target

This project currently assumes:

- Windows 10/11.
- .NET 8 Desktop Runtime or .NET 8 SDK.
- NVIDIA GPU with `nvidia-smi` available on `PATH`.
- A GPU that exposes memory-junction temperature through LibreHardwareMonitor.
- A motherboard Super I/O controller where the radiator fan is visible as
  `control/1` and `fan/1`.
- Administrator privileges for fan control and NVIDIA power-limit changes.

If your board exposes a different fan control sensor, update the selection logic
in `FanController.Scan` in `Program.cs`.

## Build

```powershell
dotnet restore .\GpuVramMonitor.csproj
dotnet build .\GpuVramMonitor.csproj -c Release
```

The executable is written to:

```text
bin\Release\net8.0-windows\GpuVramMonitor.exe
```

## Run

Run the app as Administrator:

```powershell
.\bin\Release\net8.0-windows\GpuVramMonitor.exe
```

Administrator mode is required because hardware fan controls and NVIDIA power
limits are protected operations.

## Temperature Guide

These thresholds are conservative defaults for GDDR6X monitoring:

| VRAM temperature | Meaning |
| --- | --- |
| `< 70 C` | Cool / quiet range |
| `70-85 C` | Normal long-running AI workload range |
| `85-95 C` | Hot; increase airflow |
| `> 95 C` | Too hot; use aggressive cooling |
| `110 C` | Typical NVIDIA thermal throttling threshold |

## Safety Notes

- The app can change fan speed and GPU power limit. Review the code before using
  it on unsupported hardware.
- On exit, the fan controller is returned to the default firmware control mode.
- If LibreHardwareMonitor cannot find the configured fan sensor, the overlay
  still shows available GPU telemetry but fan control is disabled.
- This project is not affiliated with NVIDIA, LibreHardwareMonitor, or any GPU
  vendor.

## Roadmap

See [ROADMAP.md](ROADMAP.md) for planned work. The main near-term goal is to
make the app easier to configure across more motherboards and GPUs.

## Contributing

Issues and pull requests are welcome. Please include your GPU model, motherboard
model, Windows version, and relevant sensor names when reporting hardware
compatibility problems. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT License. See [LICENSE](LICENSE).
