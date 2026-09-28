# R5 Implementation Evidence — PCI/CXL.IO/LSA Transport Model

Baseline HEAD `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; dirty worktree recorded and preserved.

Implemented bounded PCI extended-capability traversal (alignment, missing node, loop, duplicate unique capability, count budget), deterministic mailbox deadline/maximum payload behavior, optional LSA semantics, and a bounded HBLR parser with version/size/CRC/range validation. Unsupported/invalid/timed-out LSA remains eligible for canonical persistent-capacity fallback. Missing DSN is a valid evidence state.

These are `ModelOnly` transport contracts; no real ECAM/CCI/mailbox/device claim. Hardware transport is `FutureGatedRequiresHardware`, owner platform CXL backend, missing target ECAM/CCI/timeout ownership contract.

Qualification: focused 4/0; adapter regression 47/0; solution build 0 warnings/errors; full tests adapter 47/0, neutral 58/0, platform 60/0, main 1200 passed/0 failed/2 skipped; `git diff --check` exit 0. An initial build caught cross-assembly internal codec use; parser was corrected to explicit `BinaryPrimitives` wire reads before qualification.

Claim level: `ModelValidated`. Physical identifiers remain evidence only.

Changed: `Boot/PciCxlTransportModel.cs`, `PciCxlTransportModelTests.cs`. No HybridCPU core/ISE/ISA/compiler/runtime implementation changed.
