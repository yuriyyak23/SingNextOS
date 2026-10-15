# SingCap-M Refactoring Roadmap

**Baseline:** SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`, 2026-09-19.

This roadmap implements `../SINGCAP_M_TECHNICAL_SPEC.md` by **strengthening the existing unified authority / Region / SIP / admission architecture**. It is intentionally not a green-field capability subsystem.

## Dependency graph

```text
P00 Baseline + ADR freeze
  |
  +--> P01 Toolchain/profile reconciliation
  |
  +--> P02 Single authority ledger hardening
          |
          +--> P03 Constraints + quota algebra
                  |
                  +--> P04 Derivation/revocation/effect admission
                          + [Authority Composition Protocol gate]
                          |
                          +--> P05 Opaque V2 compatibility surface
                                  |
                                  +--> P06 Software sealing pilot -----+
                                  |                                    |
                                  +--> P07 Region subrange/borrow -----+--> P08 Generated SIP sentries
                                                  |
                                                  +--> P09 ManagedCap admission
                                                          |
                                                          +--> P10 Manifest/audit/supply chain
                                                                  |
                                                                  +--> P11 Service migration
                                                                  |
                                                                  +--> P12 HybridCPU provider alignment
                                                                          |
                                                                          +--> P13 Qualification/perf/claims

Deferred after v1: CapRef<T>, SharedArena.
```

P01 can run partly in parallel with P02 after P00. P08 has explicit join dependencies on both P06 and P07: generated sentries cannot claim exact sealed-object or Region authority before those owners are qualified. P12 has preparation gates in P00/P10 but final cross-project qualification occurs after the local authority semantics and the Authority Composition Protocol are stable.

## Phase files

| Phase | File | Result |
|---|---|---|
| P00 | `PHASE_00_BASELINE_AND_ARCHITECTURAL_FREEZE.md` | exact baseline, threat model, ADRs and source pins |
| P01 | `PHASE_01_TOOLCHAIN_AND_PROFILE_RECONCILIATION.md` | .NET11/C#15/AOT policy and profile inventory |
| P02 | `PHASE_02_SINGLE_AUTHORITY_LEDGER_HARDENING.md` | one ledger, realm/incarnation, quotas/capacity foundation |
| P03 | `PHASE_03_CONSTRAINT_ALGEBRA_AND_QUOTAS.md` | monotonic typed constraints and non-amplifying quota accounting |
| P04 | `PHASE_04_DERIVATION_REVOCATION_AND_EFFECT_ADMISSION.md` | subtree revoke, races, one-shot and operation leases |
| P05 | `PHASE_05_OPAQUE_V2_COMPATIBILITY_SURFACE.md` | V2 handle without second authority store |
| P06 | `PHASE_06_SOFTWARE_SEALING_AND_SOCKET_PILOT.md` | generic software sealing proven on a real service object |
| P07 | `PHASE_07_REGION_SUBRANGE_AND_BORROW_HARDENING.md` | bounded range uses, read/write/async lifetime hardening |
| P08 | `PHASE_08_GENERATED_SIP_SENTRIES_AND_VALUE_SCHEMAS.md` | exact invocation authority and deep value schema closure |
| P09 | `PHASE_09_MANAGEDCAP_ADMISSION_AND_CLOSED_WORLD.md` | strict ManagedCap admission in existing verifier |
| P10 | `PHASE_10_MANIFEST_AUDIT_AND_SUPPLY_CHAIN.md` | V2 manifest extension, deterministic audit, provenance |
| P11 | `PHASE_11_SERVICE_MIGRATION.md` | filesystem/network/process and selected additional services |
| P12 | `PHASE_12_HYBRIDCPU_PROVIDER_ALIGNMENT.md` | qualify current HybridCPU contracts without authority inversion |
| P13 | `PHASE_13_QUALIFICATION_PERFORMANCE_AND_CLAIM_CLOSURE.md` | adversarial/perf/claim closure |
| Deferred | `PHASE_90_DEFERRED_CAPREF_AND_SHARED_ARENA.md` | explicit non-v1 criteria |
| Cross-cutting | `TRACEABILITY_AND_CI_GATES.md` | requirement-to-phase/test matrix |
| Cross-cutting | `AUTHORITY_COMPOSITION_PROTOCOL.md` | prepare/pin/commit, lock order, compensation and provider boundary |
| Audit | `AUDIT_DISPOSITION_2026_09_19.md` | checked disposition of the external audit against live code |

## PR discipline

Every PR MUST:

1. be independently buildable/testable;
2. identify affected security invariants;
3. add positive and negative tests in the same PR;
4. state whether it adds any identity, generation, mutable global state or external effect;
5. update deterministic evidence if the authority surface changes;
6. keep HybridCPU ISE untouched;
7. have a rollback path that never reinterprets a stronger handle as weaker authority;
8. for a composed effect, name every authoritative owner, pin/lease, commit point, lock order and reverse-order compensation;
9. prove that no provider/user call occurs under an authority-registry lock.

No phase is complete because types or metadata exist. Completion requires runtime enforcement and the tests listed in the phase file.
