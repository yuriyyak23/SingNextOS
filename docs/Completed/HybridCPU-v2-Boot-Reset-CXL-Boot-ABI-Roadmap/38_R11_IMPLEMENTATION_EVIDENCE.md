# R11 Implementation Evidence — QEMU Direction Excluded

Baseline HEAD `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; dirty worktree preserved. Local PATH and the existing `tools/cxl-qemu-qualification.ps1` capability probe were audited.

Actual result: neither `qemu-system-x86_64` nor `qemu-system-aarch64` is installed on PATH. The primary probe returned exit 2 with `qemuExecutable=false`, `qemuStarts=false`, all CXL device checks false, and no boot image. The user subsequently explicitly excluded the QEMU direction from scope. Therefore no QEMU process, fixture harness, CXL Type-3 device, LSA, HDM mapping, persistence restart, or boot flow is part of this implementation, and `QemuProtocolValidated` is not claimed.

Classification: `FutureGated` and user-excluded. QEMU would at most prove protocol behavior, never HybridCPU instruction or silicon behavior.

Qualification evidence is limited to the local QEMU probe exit 2 above. No R11 production code or test remains. No HybridCPU core/ISE/ISA/compiler/architecture implementation changed.
