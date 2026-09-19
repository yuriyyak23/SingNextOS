# Phase 00 — Architectural Decisions and Live Inventory

**Status:** frozen for SingCap-M v1 implementation

**SingNextOS baseline:** `a67eea1aafc72054d22f1586b62c6883cdc71681`

**HybridCPU-v2 source baseline:** `794c4a53494f503855ac8cf209efab23fde083b2`

This document selects the Phase 00 decisions. It does not claim that the later runtime mechanisms already exist. Code and executable tests remain authoritative over this document.

## Selected decisions

### ADR-P00-01 — one local capability ledger

`CapabilityAuthority` is the only local capability record store. V1 descriptors, V2 handles, inspection, audit and compatibility surfaces resolve to the same records. `RegionAuthority`, `EndpointSessionRegistry`, sealed-object tables and `ExternalOperationAuthority` retain their distinct lifecycle responsibilities and MUST NOT become alternative capability ledgers. A facade may exist, but it owns no authority records.

### ADR-P00-02 — authority realm and runtime incarnation

Every `RuntimeKernel` incarnation creates a cryptographically random 128-bit `AuthorityRealmId`. It is immutable for that incarnation and is carried by every ephemeral V2 handle and authoritative record. A restart creates a new realm and an empty ephemeral ledger; no import based only on an old realm/token is allowed. Random generation failure is fatal to realm creation. Durable authority is out of scope and requires a separate privileged re-authorization protocol.

### ADR-P00-03 — service incarnation

The runtime owns one non-wrapping `ulong` service-incarnation sequence per service identity inside a realm. Successful activation reserves the next value; crash, stop and restart never reuse a value. Exhaustion prevents activation. Sealed records bind realm plus service identity plus service incarnation, so a restart makes prior handles stale. A runtime restart is already separated by ADR-P00-02.

### ADR-P00-04 — opaque identity and exhaustion

V2 uses a 128-bit cryptographically random capability token, separate from `AuthorityRealmId`. Mint checks the single ledger for collision and retries at most eight times; failure returns typed capacity/identity exhaustion and creates no record. Internal monotonic IDs/generations use checked non-wrapping allocation: `MaxValue` is terminal, never reset or wrapped within an incarnation. Public handles contain only schema/version, realm and opaque token, never rights, ranges, quota or a mutable resource reference.

### ADR-P00-05 — delegation depth

The runtime capability policy owns a hard maximum depth of **16** edges for SingCap-M v1. A future manifest may request a smaller limit but cannot raise it. Root depth is zero. Derivation atomically checks `parentDepth + 1` using checked arithmetic. Unknown policy or exhausted depth fails closed.

### ADR-P00-06 — quota lineage

SingCap-M v1 uses a **shared atomic quota account** referenced by a root and all descendants. Derivation may narrow a per-handle operation ceiling but does not copy remaining counters. Consumption linearizes at an atomic debit in the shared account; aggregate successful consumption cannot exceed the root limit. Reservation-transfer and child-local copied counters are not part of v1.

### ADR-P00-07 — revoke/effect linearization

Capability validation is inspection only. Effectful paths acquire an exact internal `OperationAuthorityLease` while holding the capability-ledger gate. The acquisition linearization point is the atomic transition that both validates active ancestry/generations/constraints and registers the in-flight lease. Revocation linearizes when the record/revocation node becomes revoked under the same gate: it blocks later admissions but does not rewrite an already admitted provider result as cancelled, visible, published, released or reclaimed. In-flight work follows the existing external-operation cancellation/publication/closure state machine. An ambiguous post-effect outcome remains quarantined until exact closure or trusted containment is proven.

### ADR-P00-08 — ephemeral serialization

The only allowed wire form is a versioned ephemeral reference containing handle schema version, `AuthorityRealmId` and opaque token. It may cross only an admitted SIP/session boundary and is revalidated against caller, session and current ledger state on every admission. Rights/resource/quota fields are never accepted from the wire. Persistence, configuration use, audit export and restart import are denied; serialization never creates a durable credential.

### ADR-P00-09 — Region epoch roles

- `RegionGeneration` identifies allocation/reuse/ownership lifecycle and changes on transfer or reuse.
- `BorrowLeaseGeneration` identifies one exact borrow instance and changes for each new borrow.
- `MutationEpoch` identifies content/coherence/publication snapshots and invalidates incompatible active uses.

None substitutes for another. `RegionAuthority` remains the sole owner of all three and of owner, borrow, use, mapping, quarantine and reclaim truth.

### ADR-P00-10 — NativeIsolated

`NativeIsolated` requires an independently enforced address-space boundary: a separate OS process with restricted authenticated IPC is the minimum; a qualified VM or platform protection domain is also acceptable. Native or unsafe code in the same unrestricted process/address space is `TrustedRuntime` at most and cannot receive the `NativeIsolated` claim.

### ADR-P00-11 — ManagedCap NativeAOT

`QualifiedManaged` production lanes require a reproducible, verifier-bound NativeAOT artifact for the selected component/platform tuple. A JIT-only exception may reach `RuntimeEnforced` only through an explicit versioned policy and cannot inherit `QualifiedManaged` or `ProductionCandidate`. NativeAOT is closed-world evidence and attack-surface reduction, not authority.

### ADR-P00-12 — HybridCPU qualification identity

The locally qualified inputs are:

| Field | Frozen value/evidence |
|---|---|
| Source commit | `794c4a53494f503855ac8cf209efab23fde083b2`; verified with `git rev-parse HEAD` in the local `HybridCPU v2` checkout |
| Contract package | `HybridCPU.ExternalRuntime.Contracts` `[1.14.0]` |
| Actual nupkg SHA-256 | `B96E99BDA066EE585B26A11CBFA7B68CE6BF44FC0006679483CCC1A4EEB678C2` |
| Artifact checked | local `artifacts/h17-audit/packages/HybridCPU.ExternalRuntime.Contracts.1.14.0.nupkg`; its digest equals the restored global-packages artifact |
| NuGet SHA-512/content identity | `wLH8suR5xnj9C0CmqKTvUBSp2fLQsnS18ELZbILGosnGU3KIRJTZykX6bfvL/IWNOFOzGmQVvKI3q7irmjeM/Q==` from lock/metadata |
| External operation schema | `ExternalOperationContract.Version = 1.4.0` |
| Secure-compute schema | `ExternalSecureComputeContract.Version = 1.0.0` |
| Local package source evidence | restored `.nupkg.metadata` names `C:\Users\Yuriy Kurnosov\Desktop\HybridCPU ISE\artifacts\h17-audit\packages`; source identity is additionally bound to the commit above because the nuspec repository URL/commit is empty |

The package/source/digest tuple is indivisible. SemVer or metadata alone is insufficient. The requested `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` was not present in SingNextOS, `HybridCPU v2`, or the local artifact-source tree during P00; its absence is a recorded limitation, not substituted evidence.

Provider split is fixed: CPU guard evidence is not a SingNext capability; provider admission receipt is not a SingNext capability; provider generation is not SingNext resource generation; a HybridCPU domain tag is not a CHERI tag; completion is distinct from visibility, local publication, provider release and local Region reclaim.

## Live repository inventory

`EXISTS` means a live mechanism already implements its stated current contract. `PARTIAL` means a live basis exists but does not yet satisfy the SingCap-M target. `MISSING` means no live basis was found.

| Surface | Status | Live owner / gap |
|---|---|---|
| `CapabilityAuthority` | PARTIAL | Sole `CapabilityId -> CapabilityRecord` store; subject generation, rights, direct/domain revoke and restricted delegation exist. Realm, resource generation, subtree revoke, typed constraints, quota and effect lease are absent. |
| `CapabilityDescriptorV1` | EXISTS | Public compatibility descriptor exists; today it is also returned from validation. It must become a projection of the same hardened record, never caller authority. |
| `RegionAuthority` | PARTIAL | Owns Region/generation/owner/mutation/borrow/backing/use/mapping truth and several no-wrap checks. Exact overlapping-subrange conflict, quarantine and zeroization policy remain incomplete. |
| `OwnedBuffer<T>` | PARTIAL | Logical invalidation, MOVE shape, lexical span and runtime reservation exist. Exact subrange/read-write APIs and measured post-acquire loop guarantees remain. |
| `OwnedRegion<T>` | PARTIAL | Reuses `OwnedBuffer<T>` and MOVE/borrow path; strengthened Region use semantics remain. |
| `BorrowLease<T>` | PARTIAL | Heap-safe lifetime object and stack-materialized `ReadOnlySpan<T>` exist; explicit async revalidation/subrange modes remain. |
| `RegionUseHandle` | EXISTS | Opaque use id/generation contract resolved only through `RegionAuthority`. |
| `RegionBackingLeaseHandle` | EXISTS | Opaque backing id/generation contract resolved only through `RegionAuthority`. |
| `EndpointSessionRegistry` | PARTIAL | Owns caller/service/session generation/state and close/expiry. Allocation exhaustion and generated exact invocation projections remain. |
| `ExternalOperationAuthority` / `RuntimeKernel` lifecycle | EXISTS | Separate Prepare/Admit/Submit/Complete/Visible/Publish/Release states, exact binding/dependency checks and quarantine paths exist. It is effect truth, not capability authority. |
| `HybridCpuExternalOperationProvider` | PARTIAL | Provider-neutral contract adapter maps the existing lifecycle and exact correlation/generation. P12 must qualify new provider admission/publication evidence without authority inversion. |
| `ServiceManifestV1` | EXISTS | Identity, image, contracts, dependencies, platform/resource/budget/lifecycle/telemetry policy exist. V2 SingCap additions remain P10. |
| `SingPlusGenerator` | PARTIAL | Generates SIP protocol/dispatcher/manifest/capability metadata and validates bounded/ownership shapes. Exact sentries and recursive deep-value schemas remain P08. |
| `SingPlusAnalyzer` | PARTIAL | Source diagnostics cover unsafe/interop/ownership/self-mint and deterministic inputs. It is feedback, not final admission proof. |
| `AdmissionVerifier` | PARTIAL | Existing metadata/CIL/dependency/digest pipeline is the sole final static-policy basis. ManagedCap full-module and hostile-closure policy remain P09. |
| `FileObjectHandle` | PARTIAL | Session/id/generation handle and service-private table exist; generic stable sealing and service incarnation do not. |
| `SocketObjectHandle` | PARTIAL | Session/id/generation handle and service-private table exist; selected P06 sealing pilot. |
| `ProcessAuthority` | PARTIAL | Separates process handle from a control `CapabilityId`, but V2 constraints/effect admission remain. |
| representative native service hosts | PARTIAL | File/socket/process hosts consume existing capabilities and sessions; confused-deputy migration remains P11. |
| `CapRef<T>` / `SharedArena` | MISSING by design | Explicitly deferred by Phase 90; must not be added in v1. |

## State-machine linearization ledger for later phases

| State machine | Selected linearization point |
|---|---|
| capability mint/derive | insertion of the fully validated record into the single ledger under its gate |
| capability revoke | active-to-revoked transition of the authoritative record/revocation node under the same gate |
| effect admission | registration of the exact operation lease after all lineage/generation/constraint checks under the ledger gate |
| Region MOVE | owner plus `RegionGeneration` transition inside `RegionAuthority`; the old payload is invalidated as part of the runtime transfer path |
| external submission | `ExternalOperationAuthority.RecordSubmission` transition to `Submitted` after dependency/use revalidation |
| publication | existing `Publish` transition after exact completion/visibility/dependency checks; provider evidence is only an additional prerequisite |
| reclaim | Region release/reclaim transition only after local use/backing/provider closure; ambiguity selects quarantine, never optimistic reuse |

## Non-claims and FutureGated items

P00 is `ModelOnly`. No V2 runtime authority, sealing, ManagedCap, NativeAOT qualification or hardware capability claim is made. Durable credentials, reservation-transfer quota lineage, same-address-space `NativeIsolated`, CapRef, SharedArena and CHERI-equivalent enforcement are FutureGated or prohibited for v1 as described above.
