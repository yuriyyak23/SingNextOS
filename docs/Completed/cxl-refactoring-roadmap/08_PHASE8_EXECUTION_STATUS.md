# Phase 8 — QEMU/Hardware Execution Status

## Current verdict

**Skipped by explicit user scope on 2026-09-13; not complete or claimed as
executed.** The Phase-8 acceptance criteria require an executable QEMU
CXL backend (and later physical-hardware bring-up). The current host has no
`qemu-system-x86_64` executable and no verified CXL platform. Model providers
from Phases 5–7 are not substituted as evidence for this milestone.

## Authoritative environment check

Run on 2026-09-13 in the repository workspace:

```text
Get-Command qemu-system-x86_64 -ErrorAction SilentlyContinue
  QEMU_NOT_INSTALLED

Win32_ComputerSystem
  Manufacturer: Gigabyte Technology Co., Ltd.
  Model: X470 AORUS GAMING 5 WIFI
```

No QEMU process, QMP endpoint, CXL-enabled guest image, firmware inventory or
physical CXL device was available to exercise the required backend behavior.
Repository inspection additionally returned
`NO_BOOTABLE_VM_IMAGES_IN_REPOSITORY`, and the process inventory returned
`NO_RUNNING_QEMU_PROCESS`.

### Alternative runtime audit

A second read-only audit checked WSL, Docker and the Android SDK QEMU copy:

```text
WSL distributions: Ubuntu and docker-desktop-data, both stopped
WSL start: HCS_E_HYPERV_NOT_INSTALLED
VirtualizationFirmwareEnabled: False

Docker CLI: 29.1.3
Docker engine: unavailable (desktop Windows named pipe absent)

Android SDK qemu-system-x86_64.exe:
  process exit: -1073741515 before version/device enumeration
  no usable CXL device-model inventory
```

Thus neither WSL/container execution nor the bundled Android emulator supplies
an executable CXL-capable QEMU alternative on this host. Enabling firmware/OS
virtualization or installing a supported QEMU build is an external machine
configuration change, not a repository-only implementation step.

## Requirement-by-requirement audit

| Phase-8 requirement | Evidence | Status |
|---|---|---|
| Provider-neutral authority model remains unchanged | Phases 4–7 contracts and 812/812 full gate | Proven |
| CXL physical identities remain below public boundary | Phase-4 reflection tests | Proven for current model surface |
| QEMU host bridge/root port/device discovery | No QEMU executable/topology | Missing |
| QEMU Type-3 memory backs normal owned regions | Only model provider exists | Missing |
| QEMU decoder/window reconfiguration | No QEMU/QMP backend | Missing |
| QEMU hotplug/unplug and injected fault behavior | No QEMU/QMP backend | Missing |
| CXL.io resources reach `DeviceResourceSet` against QEMU | Model semantic projection only | Missing |
| Same conformance suite runs against model and QEMU | No QEMU backend fixture | Missing |
| Physical firmware/ACPI/PCIe/CXL negotiation | No verified CXL hardware | Missing |
| Physical reset/link/IOMMU/coherence/RAS evidence | No verified CXL hardware | Missing |

## Inputs required to resume

- a CXL-capable QEMU build available as `qemu-system-x86_64` (or an explicit
  executable path);
- a bootable SingNextOS/QEMU test image and launch configuration with CXL host
  bridge, root port and Type-3 device topology;
- a QMP or equivalent control channel for reconfiguration/fault injection;
- for the physical milestone, access to supported hardware, firmware ownership
  rules and applicable official/vendor specifications.

Installing system software or inventing a successful QEMU transcript would not
constitute repository evidence. Once these inputs exist, the next implementation
step is a provider-private enumeration/QMP adapter followed by the same authority,
Type-3, CXL.io and reset tests used by the model backend.

## Reproducible preflight

`tools/cxl-qemu-qualification.ps1` now checks the requested executable, verifies
that it starts, requires `pxb-cxl`, `cxl-rp` and `cxl-type3` device models, and
requires an existing boot image. It emits JSON for CI and exits with code 2 when
the external environment is incomplete. On the current host it reports every
required check as `false` and exits 2, matching the manual audit.

## Last verified repository gate

The last completed phase (Phase 7) passed a forced uncached restore of all 26
projects and the full 812/812 test suite. This establishes a clean entry point;
it does not satisfy Phase 8.
