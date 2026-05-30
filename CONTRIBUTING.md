# Contributing

Thanks for taking an interest in GPU VRAM Monitor. This project touches hardware
sensors, fan control, and NVIDIA power-limit settings, so changes should be
small, understandable, and easy to review.

## Development Setup

Requirements:

- Windows 10/11.
- .NET 8 SDK.
- NVIDIA driver with `nvidia-smi` on `PATH`.
- Administrator permissions when running the app with fan or power-limit
  control enabled.

Build:

```powershell
dotnet restore .\GpuVramMonitor.csproj
dotnet build .\GpuVramMonitor.csproj -c Release
```

## Pull Request Guidelines

- Keep hardware-control changes narrowly scoped.
- Explain which GPU and motherboard were used for testing.
- Include before/after behavior for UI or fan-control changes.
- Avoid committing logs, binaries, local publish output, or machine-specific
  settings.
- Prefer clear, conservative defaults over aggressive thermal behavior.

## Hardware Compatibility Reports

When opening an issue, please include:

- GPU model.
- Motherboard model.
- Windows version.
- NVIDIA driver version.
- Whether `nvidia-smi` works from PowerShell.
- Relevant LibreHardwareMonitor sensor names or a screenshot of detected fan
  controls.

## Safety

Do not submit changes that intentionally bypass operating-system permissions,
hide hardware-control failures, or set unsafe default fan speeds or power
limits.
