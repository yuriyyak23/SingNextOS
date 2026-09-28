# P05 evidence — SIP and manifest resource contracts

## Disposition and live tuple

P05 is closed at `RuntimeEnforced` only for the generated internal host/JIT `ComputeTime/Nanoseconds` contour. `FG-VNX-SIP-RESOURCE` remains OFF because no production SIP contract is migrated and P06 request-scoped donation is absent.

- Normative baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`.
- Phase-entry HEAD: `b86cc33c16a77760c6971cc1a656c34b6cf2a10d`.
- Qualification HEAD: `8c3f55e47555b2db99356b861ee404211d072edc`.
- During P05 an externally-created local merge commit moved HEAD and incorporated the then-current P00–P05 files. This agent did not run commit, fetch, pull, push, checkout, reset or any force operation. The post-merge worktree was clean and the reviewed P05 sources were preserved; the final four P05 negative-path edits are separately dirty.
- Applied invariants: VNX-001–004, VNX-007–008, VNX-010–015, VNX-019–024 and VNX-028.

## Contract and authority chain

`RequiresResourceAttribute` emits a versioned `SipResourceRequirementV1` descriptor into the generated canonical contract, capabilities metadata and operation-specific sentry. The descriptor contains only semantic class/unit/maximum/scope/assurance ceiling/donation policy. It contains no capability, budget account, reservation, lease, Region, operation, provider or publication handle.

The generated runtime wrapper executes this exact chain before the target:

```text
generated immutable descriptor
 -> trusted invocation resolver
 -> exact live effect capability + resource grant generations
 -> P04 reserve/revalidate/commit
 -> operation-specific GeneratedSipResourceAdmission scope
 -> target may cross the P04 submit boundary once
 -> scope disposal compensates any pre-submit return or exception
```

`ServiceManifestV1.ResourceUseRequirements` is canonical declarative intent only. Manifest admission emits `Requested`, never `Granted`, and materializes no authority. `GuaranteedReservation` and other post-P05 assurance claims are rejected from generated/manifest P05 contours.

Existing methods without `RequiresResource` keep their byte-for-byte ordinary generated call shape and do not enter the new resolver. The feature gate is default OFF and no production contract was annotated, so ordinary SIP remains the fallback and semantic oracle.

## Defects and remediation

- The live generator initially had capability/ownership sentries but no semantic resource contract or resolver. Added typed versioned metadata and a generated wrapper that calls P04.
- A cleanup-only scope could prove pre-submit compensation but not an executable success path. The trusted scope now exposes a single-use `Submit` transition to the generated target; duplicate use fails closed.
- Future assurance values could be parsed as if locally supported. P05 now permits only `AccountingOnly`/`RuntimeEnforced` ceilings and rejects `EnforcedUpperBound`/`GuaranteedReservation` declarations.
- Unknown provider error integers are normalized to `PlatformFaulted`; they cannot inject undefined local transition semantics.

## Negative paths and behavior

- Unknown version/class/unit, zero, max sentinel, blank scope and future guarantee fail closed in contract/generator/manifest validation.
- Missing resource grant and missing effect capability stop before target/provider invocation and leave budget usage zero.
- Default invocation context has no resolver; public reflection finds no constructible trusted context or admission object and no public authority field.
- Service exception after local admission but before submit disposes the scope, cancels the operation and restores reversible capacity.
- Successful target submission crosses P04 once and charges the live budget owner.
- Provider failure after possible submit records provider loss and preserves the conservative charge/quarantine; no refund occurs.
- Generated metadata and manifest decisions remain evidence only.

## Qualification

| Lane | Result |
|---|---|
| P05 focused | 10 passed, 0 failed |
| generator + manifest + ordinary sentry + P04 regression | 120 passed, 0 failed |
| architecture/default-gate + P05 | 19 passed, 0 failed |
| full solution build | succeeded, 0 warnings, 0 errors |
| full non-GUI | 1618 passed, 9 failed, 2 skipped |

The nine failures are the same unrelated historical failures: absent SingCap/HybridBoot evidence directories, security-profile inventory drift and the stale user-owned SipJob P14 tuple. No P05 test failed.

## Gates, FutureGated work and exclusions

- Enabled gates: none.
- Exact claim: generated internal host/JIT ComputeTime/Nanoseconds sentry-to-P04 contour only.
- P06 owner: `CapabilityAuthority` + session/invocation + budget owners. Missing prerequisite: request-scoped provenance-preserving donation and nested lineage. Bypass would launder ambient server/caller capacity.
- Production SIP migration remains absent; enabling the gate before mixed ordinary/resource-aware compatibility evidence would change existing service semantics.
- P07 durable ExternalOperation-to-lease reconciliation remains absent; provider-loss evidence is live only within the current process.
- No reflection/dynamic dispatch is used by generated code. No ABI exposes lane/opcode/slot/DSC/L7/VMCS/IOMMU/CXL queue/topology/physical address/provider-private handles.
- Plans, caches, manifests, receipts and telemetry remain non-authoritative.
- No HybridCPU ISA/microarchitecture, NativeAOT, provider package, QEMU, firmware, CXL boot, hardware, guarantee or production claim/work occurred.
