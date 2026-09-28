# P04 evidence — cross-owner admission commit protocol

## Disposition

P04 is closed at `RuntimeEnforced` only for the internal host/JIT process + effect capability + compute-time resource-use grant + budget lease + Region + ExternalOperation contour. `FG-VNX-CROSSOWNER-ADMISSION` remains OFF because generated SIP sentry resolution and session/invocation donation are P05/P06 prerequisites.

- Normative baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`.
- Reviewed HEAD: `b86cc33c16a77760c6971cc1a656c34b6cf2a10d`.
- Entry dirty worktree contained only the additive P00–P03 work recorded in their evidence. The baseline already contained unrelated user-owned historical/P14 work; none was edited or attributed to P04.
- Applied invariants: VNX-001–004, VNX-007–008, VNX-010–015, VNX-020, VNX-023–024 and VNX-028.

## Owners and linearization

`ResourceAdmissionProtocol` composes existing owners; it is not a new authority or ledger.

1. Resolve the exact process generation and validate independent effect/resource generations.
2. Validate the compute-time grant and prepared ExternalOperation/Region dependency state.
3. Reversibly reserve compute-time capacity in the sole `ResourceBudgetAuthority` ledger.
4. Re-resolve and revalidate live owner state.
5. Acquire a resource-use authority lease under `CapabilityAuthority`'s existing lock. This acquisition is the resource admission linearization point: revoke before it wins; revoke after it cannot retroactively undo the admitted attempt.
6. Acquire the independent effect authority lease, bind the budget lease, and admit the prepared ExternalOperation.
7. Release owner locks before any provider callback.
8. Record possible submission and begin consumption before calling provider code. A provider failure/throw after that point records provider loss and quarantines the budget lease without refund.

Before possible submit, disposal or failure cancels the ExternalOperation and performs the idempotent pre-submit budget cancellation. Effect permission, resource permission, quantitative capacity, Region use, operation lifecycle and publication remain independently owned.

## Defects remediated

- The initial test fixture requested `DirectCoherent` without the required effect classification. Production correctly rejected it; the fixture now uses the supported staged/read-only contour.
- Effect-resource generation and resource-grant generation were initially conflated. They are now separate exact inputs and independently validated.
- A validation-only resource check left a revoke check-then-act window. `AcquireResourceUseAuthority` now establishes a single winner inside existing `CapabilityAuthority`; it creates no second ledger and shares the existing operation-lease identity/liveness set.

## Negative, fault and race coverage

- revoke after initial check and after final validation loses or wins at the explicit resource-authority acquisition point;
- stale independent resource generation fails before reservation/commit;
- Region release between reserve and commit fails and restores capacity;
- injected faults after initial validation, reservation, final validation and local commit unwind reversible state;
- abandoned local commit is compensated;
- duplicate submit invokes provider exactly once;
- provider callback re-enters budget and ExternalOperation query paths, proving it is outside owner locks;
- provider failure after possible submit quarantines and retains the conservative charge;
- 32 concurrent independent admissions complete within the bounded deadlock test.

Session-close/invocation races are not silently modeled as process races: exact session/invocation ownership is absent from this P04 entry point and remains FutureGated to P05/P06.

## Qualification

| Lane | Result |
|---|---|
| P04 focused | 13 passed, 0 failed |
| P02–P04 + ExternalOperation/effect/cross-authority regressions | 48 passed, 0 failed |
| architecture/default-gate + P04 | 22 passed, 0 failed |
| full solution build | succeeded, 0 warnings, 0 errors |
| full non-GUI run | 1604 passed, 9 failed, 2 skipped |

The nine failures are the same pre-existing missing historical SingCap/HybridBoot evidence, project-profile inventory drift and stale user-owned P14 tuple recorded by P00–P03. No P04 test failed.

## Gates, FutureGated work and exclusions

- Enabled gates: none.
- Exact claim: internal host/JIT supported contour above; not public ABI and not production-qualified.
- P05 owner: generated SIP sentry/manifest. Missing prerequisite: live generated resolution into this protocol plus ordinary-SIP oracle; bypass would permit alternate admission semantics.
- P06 owner: process/session/invocation owners. Missing prerequisite: exact request-scoped donation and session/invocation generations; bypass would allow ambient authority or stale-session admission.
- P07 owner: `ExternalOperationAuthority` + `ResourceBudgetAuthority`. Missing prerequisite: durable exact operation-to-lease binding and reconciliation; bypass would permit ambiguous restart/refund behavior.
- Ordinary SIP/Compute/ExternalOperation paths are unchanged and remain the fallback/oracle. Plans, caches, receipts and telemetry remain non-authoritative.
- No HybridCPU ISA, VLIW, pointer/register/opcode, compiler/ISE, pipeline, scheduler legality or microarchitecture change occurred. No provider, NativeAOT, QEMU, firmware, CXL boot, hardware, guarantee or production claim is made.
