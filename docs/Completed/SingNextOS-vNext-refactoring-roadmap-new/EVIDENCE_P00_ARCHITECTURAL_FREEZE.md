# P00 evidence — architectural freeze and live baseline

## Disposition

P00 is closed at `ModelOnly` for the local host/JIT source contour. No vNext runtime behavior is enabled. All `FG-VNX-*` gates are represented by one closed exact-name registry and remain unconditionally OFF.

## Baseline and drift

- Normative SingNextOS baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`.
- Reviewed live HEAD: `b86cc33c16a77760c6971cc1a656c34b6cf2a10d`.
- Initial `git status --short`: empty.
- Baseline-to-HEAD delta: 78 paths. It consists of the corrected vNext roadmap replacement, pre-existing SipJob P14 remediation/evidence, five runtime paths and eight test paths. The SipJob/P14 work and removal of the superseded roadmap are user-owned baseline drift and were not modified or claimed by this phase.
- P00-owned dirty paths after remediation: `src/Runtime/SingPlus.Runtime/VNext/VNextFeatureGates.cs`, `tests/SingPlus.Tests/Architecture/VNextPhase00ArchitecturalFreezeTests.cs`, and additive files in this roadmap directory.

## Local artifact availability

- Local `HybridCPU.ExternalRuntime.Contracts/1.14.0` nupkg exists at `.packages/HybridCPU.ExternalRuntime.Contracts.1.14.0.nupkg`, SHA-256 `b96e99bda066ee585b26a11cbfa7b68ce6bf44fc0006679483ccc1a4eeb678c2`.
- The runtime project requests exact `[1.14.0]` and its lock file resolves `1.14.0`.
- This is local artifact inventory only. HybridCPU-v2 source SHA `794c4a53494f503855ac8cf209efab23fde083b2` was not locally verified in P00, and no provider/executable claim is made. Re-pinning remains required in P09/P16.

## Owners and linearization boundaries reviewed

| Owner | Live source | Boundary frozen by P00 |
|---|---|---|
| `CapabilityAuthority` | `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs` | sole capability record store under `_gate`; exact realm/subject/resource generation validation |
| `ResourceBudgetAuthority` | `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs` | sole account/reservation store under `_gate`; reservations admit capacity but do not authorize effects |
| `RegionAuthority` | `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs` | Region ownership/use/backing and reclaim transitions |
| session/invocation | `src/Runtime/SingPlus.Runtime/Services/RuntimeKernel.Services.cs` and session registries | session/invocation generation and protocol lifecycle |
| `ExternalOperationAuthority` | `src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs` | operation generation, completion, visibility, publication and release lifecycle |
| publication | `src/Runtime/SingPlus.Runtime/RuntimeKernel.Responses.cs` and ExternalOperation publication transitions | response/OS publication truth, independent of budget |
| platform/provider | platform abstractions/adapters and ExternalRuntime semantic contract | provider admission/evidence only; no SingNext authority minting |

`RuntimeKernel.cs` constructs one instance of each core owner. The production-source detector found no `TemporalResourceAuthority`, one `CapabilityId` record store, and one `BudgetReservationId` store.

## Defect and remediation

Confirmed defect: `VNEXT_FEATURE_GATES.md` declared gates, but no executable closed vocabulary proved that every declared name existed, was canonical, default OFF, and failed closed for unknown spellings.

Remediation: added `VNextFeatureGates` as an internal exact-name registry with no configuration path. It is not an authority and cannot enable a contour. Added focused tests for documentation/code synchronization, default-OFF behavior, malformed names, duplicate-ledger detection, public Contracts/SIP ABI negative space, budget DTO non-authority, and baseline JSON consistency.

The first focused run failed because the test treated semantic `Dsc1ComputeCapability` as forbidden merely due to the substring `Dsc`. The rule was corrected to reject provider-private token vocabulary (`DscToken`, `L7Token`, raw opcode/slot/topology handles) on Contracts/SIP surfaces. No production ABI was changed.

## Requirements and invariant disposition

- VNX-001: executable production-source checks prove no second capability/budget ledger or `TemporalResourceAuthority` class in the reviewed tree.
- VNX-002: existing executable budget tests and the P00 ABI test prove constructible budget DTOs do not authorize effects or materialize authority.
- VNX-008: current exact generation behavior remains owned by existing owner tests; P00 changes no generation semantics.
- VNX-011/VNX-017: gates, plans, snapshots, receipts and caches remain non-authoritative; P00 adds no mutation API.
- VNX-019/VNX-020: public Contracts/SIP negative-space checks are executable; no ISA category is populated.
- VNX-023: claim remains exactly `ModelOnly`.
- VNX-025–028: no checkpoint, replay, correlation, or terminal-policy semantics are changed; their stronger closure remains FutureGated to P14/P16.

## Commands and actual results

| Command | Result |
|---|---|
| `git rev-parse HEAD` | `b86cc33c16a77760c6971cc1a656c34b6cf2a10d` |
| `git status --short` before work | empty |
| `git diff --name-status 6227ea7c..HEAD` | 78 changed paths reviewed and categorized |
| roadmap manifest SHA/byte validator | `MANIFEST_OK` before additive P00 evidence |
| `dotnet --version` | `11.0.100-rc.1.26425.128` |
| focused P00 test, first run | 8 passed, 1 failed due to over-broad `Dsc` substring rule |
| focused P00 test after correction | 9 passed, 0 failed |
| focused owner/architecture/budget regression lane | 52 passed, 0 failed |
| affected runtime build | succeeded, 0 warnings, 0 errors |
| full solution build | succeeded, 0 warnings, 0 errors |
| full non-GUI suite | failed: 1568 passed, 9 failed, 2 skipped across four test assemblies |

The nine full-suite failures are recorded verbatim by identity in the tuple. Eight are missing historical SingCap/HybridBoot evidence or project-profile inventory drift; one is the pre-existing SipJob P14 tuple still naming baseline `6227ea7...` while its test expects current HEAD `b86cc33...`. None runs in `VNextPhase00ArchitecturalFreezeTests`. They are not hidden or relabelled, and user-owned historical/P14 artifacts were not modified. They block any full-suite or production qualification claim, but do not contradict P00's limited `ModelOnly` source freeze.

The remaining build/full-suite/schema/hash results are recorded in `P00_QUALIFICATION_TUPLE.json` after final qualification.

## Gates, claims, and exclusions

- Enabled gates: none.
- Exact claim: `ModelOnly`, local source/owner freeze plus executable static/default-OFF negative checks on host JIT.
- Ordinary SIP/Compute/ExternalOperation fallback remains unchanged.
- Plans, caches, receipts, telemetry, snapshots and the gate registry remain non-authoritative.
- No HybridCPU ISA, VLIW, register, pointer, lane, opcode, pipeline, replay, memory-controller, scheduler, firmware, QEMU, CXL boot, NativeAOT or hardware work was performed or claimed.
- All P01–P17 runtime contours remain FutureGated until their exact owner contract and executable evidence exist.
