# HybridCPU Boot Qualification Claim Matrix

This matrix is a human index over `QUALIFICATION_CLAIM_MATRIX.json`. It covers T001–T050 without promoting component/model evidence into CPU, QEMU, firmware, or hardware claims.

Independent sequential audit evidence: R0 — `41_R0_AUDIT_EVIDENCE.md`; R1 — `42_R1_AUDIT_EVIDENCE.md`; R2 — `43_R2_AUDIT_EVIDENCE.md`; R3 — `44_R3_AUDIT_EVIDENCE.md`; R4 — `45_R4_AUDIT_EVIDENCE.md`; R5 — `46_R5_AUDIT_EVIDENCE.md`; R6 — `47_R6_AUDIT_EVIDENCE.md`; R7 — `48_R7_AUDIT_EVIDENCE.md`; R8 — `49_R8_AUDIT_EVIDENCE.md`; R9 — `50_R9_AUDIT_EVIDENCE.md`; R10 — `51_R10_AUDIT_EVIDENCE.md`; R11 — `52_R11_AUDIT_EVIDENCE.md`; R12 — `53_R12_AUDIT_EVIDENCE.md`; R13 — `54_R13_AUDIT_EVIDENCE.md`.

Every T001–T050 JSON entry carries its own classification, claim level, focused-test mapping, audit-evidence path, and production gate. The architecture gate rejects missing evidence files, empty gates, empty executable test lists, and forbidden hardware/QEMU claims.

| Claim | Scenarios | Meaning |
|---|---|---|
| `ContractOnly` | T007, T008, T039, T040 | Versioned wire/parser behavior executed on the host. |
| `ModelValidated` | T002–T006, T009–T010, T013–T028, T031–T037, T043, T045–T050 (except higher claims below) | Deterministic adapter model properties executed; no ROM/core/hardware claim. |
| `AdapterQualified` | T029, T030, T038, T044 | SingNext importer exercised against the local existing provider boundary. |
| `QemuProtocolValidated` | none | QEMU direction explicitly excluded by the user. |
| `HardwareValidated` | none | No platform hardware was available or executed. |
| `FutureGated` | T001, T011, T012, T041, T042 | Requires HybridCPU core owner, ABI-owner decision, or protected hardware store. |

Mandatory property mapping:

- `selection(permutation(topology)) == selection(topology)`: `CxlBootSelectionModelTests`.
- `acceptedImageGeneration >= protectedRollbackFloor`: `TrustAndProtectedStateModelTests`, `CxlBootSelectionModelTests`, `BootEvidencePayloadCodec` importer tests.
- `executedBytesHash == signedDescriptorHash`: `Stage0RecoveryModelTests`, `Stage1LoadModelTests` (model bytes are publishable only after destination hash verification).
- `OSAuthority ∩ FirmwareEvidenceHandles == empty`: `HybridBootInfoImporterTests`, architecture public-surface tests.
- `temporary mapping after reset is stale`: `TemporaryApertureModelTests`, `ResetMemoryMapModelTests`.
- `BootInfo physical evidence cannot authorize OS effects`: `HybridBootInfoImporterTests`; only fresh `ICxlDiscoveryProvider` results cross the importer.

GUI tests and the QEMU direction are excluded by explicit user instruction. Hardware/firmware/core gates remain fail closed and unclaimed.
