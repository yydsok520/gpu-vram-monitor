# Security Policy

GPU VRAM Monitor can request Administrator privileges and can change GPU power
limits and motherboard fan-control values. Please report security-sensitive
issues privately where possible.

## Supported Versions

The `main` branch and the latest tagged release are currently supported.

## Reporting a Vulnerability

Please open a GitHub security advisory if available, or create a minimal issue
that avoids publishing exploit details. Include:

- Affected version or commit.
- What hardware or Windows configuration is involved.
- Steps to reproduce.
- Expected impact.

## Scope

Relevant issues include:

- Unsafe command execution around `nvidia-smi`.
- Hardware-control behavior that can unexpectedly disable cooling.
- Privilege or permission handling mistakes.
- Log output that exposes sensitive local paths or machine information.

Hardware incompatibility without a security impact should be reported as a
normal bug.
