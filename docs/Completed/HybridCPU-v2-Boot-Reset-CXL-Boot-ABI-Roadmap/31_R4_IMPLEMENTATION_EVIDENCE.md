# R4 Implementation Evidence — Semantic CXL Boot Selection

Baseline HEAD `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; dirty status recorded and preserved.

Implemented an internal provider-neutral `ICxlBootDiscoveryModel` boundary, typed physical evidence, candidate/policy/result records, and deterministic selector. Selection filters semantic `BootVolumeId`, signed/media validity, required properties, protected selected image and rollback floor. Replica tie-break is signed `ReplicaId`; enumeration order, endpoint observation, BDF, route and DSN are not semantic. DSN changes selection only when policy marks it required or preferred. Same-generation conflicting ImageId or signed digest fails as split-brain.

Classification: selector and fixtures are `ModelOnly`; real PCI/CXL discovery is deferred to R5/R12. No candidate/endpoint observation is a capability or provider lease.

Qualification: focused 5/0; adapter regression 43/0; solution build succeeded 0 warnings/errors; full solution tests adapter 43/0, neutral 58/0, platform 60/0, main 1200 passed/0 failed/2 intentionally skipped; `git diff --check` exit 0.

Claim level: `ModelValidated`.

Changed files: `tools/HybridCpu_ExecutableAdapter/Boot/CxlBootSelectionModel.cs`, `tools/HybridCpu_ExecutableAdapter.Tests/CxlBootSelectionModelTests.cs`.

No HybridCPU core/ISE/ISA/compiler/scheduler/fetch/pipeline/replay/memory-controller/retire/register implementation changed.
