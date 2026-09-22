# P15 Direct SingNext Boot — Updated Implementation Roadmap

**Status:** implementation plan, not implementation evidence.

This roadmap is the corrected implementation plan for Direct SingNext Boot in **SingNextOS**. It incorporates the architectural/security requirements, audit findings, repository-drift corrections, sequencing corrections, and qualification constraints identified during the completed audit.

Audit snapshot observed during the completed audit: `242cb43e1beccdcbd92a398163378c758f4e7a21` on `master`. This SHA is **metadata only**. It MUST NOT become a permanent baseline. Every implementation PR must revalidate the then-current `master` and record drift.

## Evidence doctrine

```text
documentation claim
    MUST be verified against
current source code + project graph + tests + qualification tooling.

"Completed" directory != production readiness.
ModelValidated != AdapterQualified != IseValidated != HardwareValidated.
```

## Claim taxonomy

- `ContractOnly` — contracts/layout/policy compile and architecture rules pass.
- `ModelValidated` — deterministic production-intent semantics pass model/oracle/property tests.
- `AdapterQualified` — SingNext-owned executable adapters run with controlled transports/fault injection.
- `IseValidated` — supported HybridCPU ISE executes the claimed Direct SingNext path end-to-end.
- `QemuProtocolValidated` — protocol/state-machine behavior is validated under QEMU where applicable; not equivalent to HybridCPU ISE or hardware evidence.
- `HardwareValidated` — the exact named hardware/profile has direct evidence for the claimed behaviors.

## Non-negotiable invariants

```text
boot evidence != runtime authority
physical mapping != memory ownership
BootVolumeId != capability
BDF / DSN / HPA / DPA / route / decoder index != authority
same numerical HDM mapping != authority continuity
warm-reset physical survival != generation survival
compiler metadata != runtime authority
HybridCPU/provider receipt != SingNext capability
completion != visibility != publication != release

BootCapsuleGeneration
    != ImageGeneration
    != BootMappingGeneration
    != ProviderGeneration
    != RegionGeneration
```

No P15 code may mint `OwnedRegion`, `RegionUse`, runtime provider authority, or capability authority from `HybridBootInfo` or physical identity alone.

## Target execution chain

```text
HybridCPU architectural reset
        ↓
small immutable ROM
        ↓
signed local SingNext Boot Capsule
        ↓
SingNext.Boot.Capsule
        ↓
SingNext.Boot.Core
        ↓
SingPlus.Platform.HybridCpu.Boot
        ↓
bounded PCIe/CXL discovery
        ↓
CXL Type-3 BootVolume discovery
        ↓
temporary single-target HDM aperture
        ↓
verified copy to normal RAM
        ↓
HybridBootInfo
        ↓
kernel entry
        ↓
fresh liveness/generation admission
        ↓
new runtime provider generations
        ↓
temporary aperture retirement/quarantine
        ↓
RegionAuthority / OwnedRegion / RegionUse
```

## Document map

- `01-DECISION-AND-SCOPE.md` — decisions, invariants, non-goals.
- `02-CURRENT-STATE-BASELINE.md` — audit-grounded current-state matrix and P15-00 revalidation contract.
- `03-TARGET-PROJECT-TREE-AND-DAG.md` — corrected project graph and forbidden edges.
- `04-API-CONTRACTS.md` — contract ownership, layering, failure/generation semantics.
- `05-MIGRATION-MAP.md` — disposition for existing `tools/HybridCpu_ExecutableAdapter/Boot/*` files.
- `06-P15-PR-SLICES.md` — corrected independent/revertible PR sequence.
- `07-BOOT-CAPSULE-SECURITY-PROFILE.md` — static-admission and NativeAOT rules.
- `08-HYBRIDCPU-AOT-AND-ENTRY-ABI.md` — external read-only HybridCPU requirements.
- `09-CXL-BOOT-BACKEND.md` — PCI/CXL/HDM boot backend behavior.
- `10-KERNEL-HANDOFF-AND-FRESH-ADMISSION.md` — BootInfo, fresh admission, authority separation.
- `11-RESET-SEMANTICS.md` — architectural reset vs runtime backend epoch.
- `12-PROTECTED-STATE-AB-ROLLBACK-RECOVERY.md` — durable A/B/rollback/recovery state machine.
- `13-QUALIFICATION-PLAN.md` — test/evidence lanes and mandatory faults.
- `14-EXIT-CRITERIA-AND-CLAIM-LEVELS.md` — legal claim promotion.
- `15-CI-REPOSITORY-INTEGRATION.md` — solution/build/policy/admission/qualification integration.
- `16-EXTERNAL-HYBRIDCPU-FEATURE-GATES.md` — explicit external prerequisites.
- `17-RISKS-NON-GOALS-AND-FAIL-CLOSED.md` — risk controls.
- `18-IMPLEMENTATION-CHECKLIST.md` — maintainer checklist.
- `19-TRACEABILITY-MATRIX.md` — requirement → owner → PR → test → claim.
- `20-FINDINGS-RESOLUTION-MAP.md` — how the audit findings changed this plan.
- `MANIFEST.json` — roadmap metadata and file list.

## Roadmap-use rule

The roadmap becomes the authoritative **implementation plan** only after `P15-00` revalidates repository state and updates any drift. It is never implementation evidence by itself.
