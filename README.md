# GPU VRAM Monitor

Windows desktop overlay for monitoring an RTX 3090's GPU memory junction temperature, core temperature, power draw, core clock, radiator fan speed, and power limit.

The app no longer depends on HWiNFO. It reads GPU and motherboard sensors directly through the open-source `LibreHardwareMonitorLib` package.

## Features

- Real-time desktop overlay for VRAM memory junction temperature
- GPU core temperature, power draw, and core clock display
- PUMP_FAN1 radiator fan control through LibreHardwareMonitor
- Manual fan slider and automatic fan curve
- NVIDIA power limit buttons for 280W and 350W
- System tray temperature icon

## Requirements

- Windows
- .NET 8 Desktop Runtime or .NET 8 SDK
- NVIDIA driver with `nvidia-smi` available on PATH
- Administrator privileges for hardware sensor control and NVIDIA power limit changes

## Build

```powershell
dotnet build .\GpuVramMonitor.csproj -c Release
```

The executable is written to:

```text
bin\Release\net8.0-windows\GpuVramMonitor.exe
```

## Notes

This project was tuned for an RTX 3090 Neptune-style setup with radiator fans connected to motherboard `PUMP_FAN1`. If your motherboard sensor IDs differ, the fan-control sensor selection in `Program.cs` may need adjustment.
