# Roadmap

This roadmap tracks practical maintenance work that would make GPU VRAM Monitor
useful to more Windows/NVIDIA users.

## Near Term

- Add a simple configuration file for fan-control sensor IDs, power-limit
  presets, refresh interval, and temperature thresholds.
- Document how to find LibreHardwareMonitor sensor IDs on different
  motherboards.
- Add screenshots of the overlay and tray icon.
- Add a troubleshooting guide for missing VRAM temperature, missing
  `nvidia-smi`, and fan-control permission errors.
- Enable the GitHub Actions build workflow from
  `docs/build-workflow.example.yml`.
- Publish release binaries from GitHub Actions after CI is enabled.

## Compatibility

- Test with more RTX 30-series and RTX 40-series cards.
- Add fallback display when memory-junction temperature is unavailable.
- Make fan-control sensor selection safer by listing detected candidates before
  changing any value.

## Quality

- Split hardware access, NVIDIA power-limit commands, fan curve calculation,
  and overlay rendering into smaller classes.
- Add unit tests for fan-curve and threshold logic.
- Add static analysis and format checks to CI.

## Community

- Maintain clear issue templates for hardware compatibility reports.
- Track verified hardware combinations in the README.
- Add release notes for each tagged version.
