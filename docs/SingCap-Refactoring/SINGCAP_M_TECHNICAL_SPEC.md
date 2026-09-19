# SingCap-M Technical Specification

**Status:** normative implementation specification for refactoring planning  
**Research baseline date:** 2026-09-19  
**SingNextOS master:** `a67eea1aafc72054d22f1586b62c6883cdc71681`  
**HybridCPU-v2 master:** `794c4a53494f503855ac8cf209efab23fde083b2`  
**Target:** strengthen the existing SingNextOS authority / Region / SIP / admission ledger without creating a parallel capability or memory-security universe.

## 0. Normative vocabulary and evidence classes

The key words **MUST**, **MUST NOT**, **SHOULD**, **SHOULD NOT**, and **MAY** are normative.

Every architectural statement in implementation reviews MUST be classifiable as one of:

- **[CODE]** — demonstrated by live code and/or tests at the pinned baseline.
- **[DOC]** — documented but not demonstrated by production implementation.
- **[PROPOSAL]** — normative target introduced by this specification.
- **[INFERENCE]** — architecture conclusion derived from code and contracts.
- **[UNSUPPORTED]** — claim that must not be used as a security guarantee.

Live code, current tests, conformance surfaces, and qualification artifacts are authoritative over README/whitebooks/roadmaps when they disagree.

## 1. Baseline and source-of-truth

### 1.1 SingNextOS

Normative source commit:

```text
a67eea1aafc72054d22f1586b62c6883cdc71681
```

Important live surfaces at this commit include:

```text
contracts/SingPlus.Contracts/Capabilities.cs
contracts/SingPlus.Contracts/Regions.cs
contracts/SingPlus.Contracts/ComponentManifests.cs
contracts/SingPlus.Contracts/NativeServiceContracts.cs
src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs
src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs
src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs
src/Runtime/SingPlus.Runtime/Services/EndpointSessionRegistry.cs
src/Runtime/SingPlus.Runtime/NativeServices/RuntimeNativeServiceHosts.cs
src/Sip/SingPlus.Sip/Regions/OwnedBuffer.cs
src/Sip/SingPlus.Sip/Regions/OwnedRegion.cs
src/Sip/SingPlus.Sip/Regions/BorrowLease.cs
sdk/SingPlus.Generators/SingPlusGenerator.cs
sdk/SingPlus.Analyzers/SingPlusAnalyzer.cs
tools/SingPlus.Admission/AdmissionVerifier.cs
tests/SingPlus.Tests/Capabilities/CapabilityAuthorityTests.cs
tests/SingPlus.Tests/Admission/AdmissionVerifierTests.cs
```

The repository already uses the .NET 11 RC1 SDK via `global.json`:

```text
11.0.100-rc.1.26425.128
rollForward = disable
allowPrerelease = true
```

Default `LangVersion` remains C# 13; preview language is opt-in through `SingPlusPreviewLanguage=true`. Therefore this specification does **not** define a future '.NET 11 migration'. It defines a toolchain reconciliation and qualification step.

### 1.2 HybridCPU-v2

Normative source commit:

```text
794c4a53494f503855ac8cf209efab23fde083b2
```

This current baseline adds/strengthens provider-neutral external operation surfaces including:

```text
HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs
HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs
HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs
HybridCPU_ExternalRuntime/ExternalOperationAdapterSession.cs
HybridCPU_ISE/.../ProviderNeutralExternalAcceleratorBackend.cs
HybridCPU_ISE/.../SecureDomainAdmissionPolicy.cs
```

The external contracts package remains versioned `1.14.0`, while the source surface has changed since the previously audited HybridCPU baseline. Therefore SingNextOS qualification MUST bind **package version + package digest + exact HybridCPU source commit**. SemVer alone is not sufficient evidence of binary identity.

### 1.3 External reference model

CHERI / CHERIoT are used only as a source of security principles and comparison vocabulary. SingCap-M is **not** a CHERI ISA implementation and MUST NOT claim hardware-tag equivalence, capability-register equivalence, hardware load/store bounds enforcement, or native-code pointer provenance enforcement.

Current .NET 11 status at this baseline is RC1 / Go-Live. C# 15 is preview. SingCap-M security MUST depend on actual IL/metadata/runtime behavior and admission policy, not on future language semantics.

## 2. Architectural objective

SingCap-M SHALL strengthen the existing SingNextOS architecture into a coherent managed capability-security profile with the following shape:

```text
explicit manifest authority
        +
explicit SIP/session authority
        +
explicit capability arguments
        +
existing Region ownership/borrow state
        |
        v
RuntimeKernel / single local authority ledger
        |
        +-- capability derivation + revocation + quota
        +-- sealed service objects
        +-- Region subrange/use projections
        +-- EndpointSession invocation projection
        +-- external-operation admission/publication lifecycle
        |
        v
PlatformAuthorityBridge
        |
        v
provider-neutral contracts
        |
        v
HybridCPU adapter/runtime/provider
        |
        v
existing HybridCPU-v2 ISE
```

The architecture MUST preserve these existing SingNextOS principles:

```text
discovery != authority
identity != authority
mapping != ownership
completion != publication
evidence != authority
provider authority != SingNext authority
coherence != ownership
intent != authority
```

## 3. Explicit non-goals and hard constraints

### H-001 — no HybridCPU ISE changes

SingCap-M MUST NOT require:

- new opcodes or instructions;
- capability registers;
- tagged memory;
- CHERI pointer encoding;
- hardware sealing instructions;
- per-load/per-store SingCap lookup;
- changed load/store memory semantics;
- changed retire semantics;
- changed compiler-to-ISE contract solely for SingCap-M.

### H-002 — no security guarantee from absence of OOO

The presence or absence of out-of-order execution is not a SingCap-M security property. Backend rename/commit/physical-register machinery MUST NOT be interpreted as capability enforcement or as proof of hidden superscalar OOO semantics.

### H-003 — no parallel local authority universes

SingCap-M MUST NOT create a second authoritative capability store, a second Region owner/generation ledger, or a second external completion/publication truth source.

### H-004 — no software CHERI check on ordinary memory operations

The intended memory path is:

```text
authority / borrow validation
    -> bounded managed view
    -> ordinary CLR/JIT/AOT memory operations
```

No capability table access is allowed inside ordinary `Span<T>` element loops.

## 4. Existing mechanisms that MUST be reused

### 4.1 CapabilityAuthority

[CODE] Current `CapabilityAuthority` already owns live records, subject checks, generation checks, rights checks, direct revocation, domain-wide epoch revocation, and restricted delegation. Mint/Delegate are not exposed as public application APIs.

SingCap-M MUST evolve this into the single capability authority ledger. A class named `CapabilityAuthorityV2` MAY exist only as a compatibility facade or internal refactoring stage if it delegates to the same authoritative records. It MUST NOT maintain a second mint/revoke table.

### 4.2 RegionAuthority

[CODE] `RegionAuthority` already owns `RegionId`, `RegionGeneration`, `RegionOwner`, `MutationEpoch`, borrow generation/lifetime, backing lease, RegionUse records, mapping reservations and external-borrow reservations.

All subrange, read/write, asynchronous-use, MOVE and quarantine changes MUST extend this ledger.

### 4.3 OwnedBuffer / OwnedRegion / BorrowLease

[CODE] `OwnedBuffer<T>` already distinguishes logical validity from backing CLR array lifetime, invalidates the source after MOVE, excludes owner access during active runtime borrow/reservation, and provides lexical `BorrowedSpan<T>` and async-capable `BorrowLease<T>` shapes.

New read/write borrow APIs MUST be evolutions of this path rather than unrelated memory wrappers.

### 4.4 EndpointSession and invocation lifecycle

[CODE] `EndpointSessionRegistry` and RuntimeKernel session flows already provide caller/service/session generation/state checks and explicit receive/accept/cancel/publish paths.

Generated SingCap sentries MUST bind to these sessions; they MUST NOT introduce an ambient `AsyncLocal` authority root.

### 4.5 AdmissionVerifier

[CODE] `tools/SingPlus.Admission/AdmissionVerifier.cs` already scans managed metadata/CIL, finds interop and unsafe memory operations, denies selected BCL surfaces, traverses local dependencies and hashes dependency content.

ManagedCap MUST extend this verifier pipeline. It MUST NOT create an independent final-verdict verifier with separate policy truth.

### 4.6 ServiceManifestV1

[CODE] `ServiceManifestV1` already includes component identity/version, image digest, provided/required contracts, typed dependencies, platform requirements, resource requirements, budget requests and lifecycle/telemetry policies.

A future V2 MUST be an additive evolution and reuse these semantics.

### 4.7 HybridCPU external-operation adapter boundary

[CODE] SingNextOS already contains `HybridCpuExternalOperationProvider`, translating provider-neutral HybridCPU requests into existing `PrepareExternalOperation` / `AdmitExternalOperation` / submission / completion / visibility / publication / release lifecycle.

New HybridCPU H01/H02 publication/admission-binding surfaces may be consumed as additional provider evidence and fail-closed checks. They MUST NOT mint SingNext capabilities or become the SingNext derivation graph.

## 5. Threat model

### 5.1 In-scope attacker

For `ManagedCap`, assume adversarial component code may:

- construct arbitrary public structs/records;
- copy/replay opaque handle bytes it legitimately received;
- invoke any admitted managed code reachable through its dependency closure;
- use concurrency, exceptions, cancellation and task scheduling adversarially;
- attempt reflection/dynamic-loading/native/unsafe escape;
- attempt confused-deputy calls through services;
- send malformed SIP payloads and stale generations;
- exhaust capability/session/seal/Region tables within reachable APIs;
- persist tokens and replay them after service or runtime restart.

### 5.2 Trusted computing base

For the `ManagedCap` claim, the TCB includes at minimum:

```text
SingNextOS privileged kernel / RuntimeKernel
single capability/revocation/quota ledger
RegionAuthority
seal authority / object tables
SIP generated sentries and trusted dispatch glue
AdmissionVerifier and policy implementation
.NET runtime and GC
qualified AOT/JIT backend
trusted platform bridge
trusted native runtime support
provider adapters that translate already-authorized SingNext requests
```

Compromise of the TCB is outside the strong SingCap-M guarantee. This is a deliberate residual gap relative to hardware CHERI.

### 5.3 Out-of-scope claims unless separately qualified

- physical side-channel resistance;
- hard-real-time WCET guarantees;
- protection after arbitrary native-code execution inside the same unrestricted address space;
- CHERI-equivalent spatial/temporal safety;
- cryptographic authenticity of diagnostic/evidence DTO constructors;
- instant cancellation of irreversible external effects.

## 6. Single capability authority ledger

### CAP-001 — public capability is not an address

A public capability MUST be an opaque authority reference. It MUST NOT contain authoritative address/base/length/rights fields that an application can edit to create new authority.

### CAP-002 — descriptor is evidence/inspection, not authority

`CapabilityDescriptorV1` MAY remain as a compatibility/inspection surface during migration. The runtime MUST ignore caller-provided descriptor rights/resource fields as authority. Authorization derives only from the authoritative record found through the opaque token.

### CAP-003 — one authoritative record schema

The internal record SHOULD evolve toward:

```text
CapabilityRecord
  AuthorityRealmId
  CapabilityToken
  SubjectId
  SubjectGeneration
  ResourceIdentity
  ResourceGeneration
  EffectiveConstraints
  ParentRevocationNode
  DelegationDepth
  QuotaAccount / Reservation
  SessionConstraint?
  LifetimeConstraint?
  State { Active, Consumed, Revoked, Retired }
```

The exact CLR shape is implementation detail; the invariants are normative.

### CAP-004 — AuthorityRealm / runtime incarnation

Every ephemeral capability MUST belong to an authority realm/runtime incarnation. Runtime restart MUST NOT cause a previously issued token to alias newly minted authority.

Persisted authority requires a separate privileged re-authorization protocol. Ordinary capability handles are not durable credentials.

### CAP-005 — token identity and entropy

The handle MAY use random 128-bit identity or a slot+nonce/generation design. In either case:

- guessing an ID MUST NOT be sufficient without exact subject/resource/session checks;
- collision MUST fail/remint rather than alias;
- token allocation failure MUST fail closed;
- security-relevant identity counters MUST NOT wrap.

### CAP-006 — table quotas and DoS

Per-subject and global bounded quotas MUST exist for live capabilities, descendants, sessions, sealed objects and operation leases. Exhaustion returns a typed capacity failure and MUST NOT degrade to a weaker path.

## 7. Monotonic authority and constraint algebra

For every valid derivation:

```text
Authority(child) subset-of Authority(parent)
```

This MUST hold across:

- rights;
- resource identity;
- subresource/range;
- permitted operations;
- quota;
- lifetime;
- target subject;
- delegation depth;
- session constraints.

### CON-001 — typed algebra

Each constraint family MUST implement explicit canonical semantics:

```text
Canonicalize(x)
IsSubset(child, parent)
SerializeCanonical(x)
```

`Intersect(a,b)` is allowed only where privileged composition is explicitly specified. Unknown constraint kinds are denied, not ignored.

### CON-002 — no sibling union amplification

Two narrow sibling capabilities MUST NOT be combinable into a new capability wider than either parent lineage unless a privileged operation explicitly owns authority to form that union.

### CON-003 — checked range arithmetic

Range derivation MUST use checked offset+length arithmetic, explicit element-size conversion and alignment/type constraints. Overflow is denial.

### CON-004 — consumable quota lineage

Consumable quotas MUST use either:

1. one shared atomic quota account referenced by descendants; or
2. atomic reservation transfer that reduces parent delegable budget.

Independent child counters are forbidden because they amplify aggregate authority.

## 8. Derivation, revocation and effect admission

### REV-001 — parent-child graph

Derivation MUST atomically establish child linkage while validating the parent. Parent links are immutable after mint. Graph cycles are impossible by construction.

### REV-002 — bounded depth

Maximum delegation depth MUST be finite and policy-controlled. Validation complexity may be `O(depth)` only if depth is bounded and qualified.

### REV-003 — subtree revocation

Revoking a parent MUST invalidate all descendants even if child records still exist. Implementation MAY use ancestry walk, shared revocation nodes or epochs, but stale descendants must never regain authority.

### REV-004 — derive vs revoke linearization

A derive and parent revoke race MUST have one clear linearization order:

- revoke first => derive fails;
- derive first => the new child is included in later subtree invalidation.

### REV-005 — validation is not an effect lease

A successful `Validate()` returning a descriptor MUST NOT authorize a later irreversible effect after revocation could have occurred.

Effectful paths MUST use an atomic operation-admission primitive or lease, e.g.:

```text
AcquireOperationAuthority(...)
    -> OperationAuthorityLease
```

The lease defines the exact authority admitted for that operation attempt. Revocation blocks future acquisition; already-admitted in-flight effects follow explicit cancellation/publication/closure policy.

### REV-006 — one-shot/consume

One-shot capabilities and quota consumption MUST transition authoritative state atomically. Copying the public struct does not clone consumable authority.

### REV-007 — resource and subject generation

Validation MUST observe subject generation and resource generation, not only token identity. A restarted process/service/reused resource cannot inherit stale authority by identifier reuse.

## 9. Software sealing

### SEAL-001 — purpose

Software sealing provides opaque service-owned object references without exposing service-private state or allowing caller enumeration of sibling objects.

### SEAL-002 — public API shape

Preferred public shape:

```csharp
public readonly struct SealedHandle<TSeal>
    where TSeal : ISealedContractMarker
{
    // opaque token only
}
```

A public generic `SealedCapability<TSeal,TResource>` is discouraged because `TResource` exposes implementation coupling and encourages a generic resolver model.

Generated named handles are also acceptable.

### SEAL-003 — authoritative sealed record

Runtime/service-private state MUST include:

```text
SealObjectId/token
SealTypeId
AuthorityRealmId
service incarnation
object generation
owner/subject/session constraints
allowed operation class or associated local capability
state/revocation
internal resource resolver/state
```

### SEAL-004 — stable type identity

`SealTypeId` MUST derive from stable generated/registered contract identity. Runtime `Type.GetHashCode()` is forbidden.

### SEAL-005 — authorized unsealing

Ordinary application code MUST NOT receive a generic `Resolve<T>()`. Unseal/resolve is internal and operation-specific. Wrong type, service, session, generation or unsealer is denied.

### SEAL-006 — service restart

Service restart advances service incarnation and invalidates old sealed handles unless an explicit durable-object recovery protocol exists.

### SEAL-007 — target use cases

Generic sealing SHOULD be demonstrated first on an existing service object, preferably `SocketObjectHandle`, then extended to file/process/GUI/TLS/queue objects as justified.

## 10. Region / ownership / temporal safety

### REG-001 — RegionAuthority remains sole memory authority

Subrange capability state MUST be owned by `RegionAuthority` or an internal helper that shares exactly the same RegionId/generation/owner/use records. No independent Region-capability ledger is permitted.

### REG-002 — generation roles

The design MUST distinguish:

```text
RegionGeneration     allocation/reuse/ownership identity lifecycle
BorrowGeneration     exact borrow instance identity
MutationEpoch        content/coherence/publication snapshot semantics
```

`MutationEpoch` MUST NOT be overloaded as generic object lifetime generation.

### REG-003 — bounded subrange projection

A region projection MUST bind:

```text
RegionHandle
RegionUseRange
access rights/mode
principal
relevant generation/epoch
lifetime/session if applicable
```

### REG-004 — lexical borrow

Read/write lexical borrows SHOULD be `ref struct` views over an authoritative active-use record. `ref struct` is a compiler/lifetime aid, not the authority itself.

### REG-005 — asynchronous lease

Async operations MUST carry a heap-safe lease object/handle and MUST NOT persist a `Span<T>` or `ReadOnlySpan<T>` across suspension. A new span is materialized only after lease revalidation on the current stack.

### REG-006 — borrow conflict policy

At minimum:

- overlapping exclusive writes conflict;
- write conflicts with overlapping active read where policy demands stable read;
- read/read may coexist;
- DevicePrivate/staged/direct-coherent modes follow exact external-operation policy;
- conflict decisions are made by authoritative Region use state, not by caller claims.

### REG-007 — MOVE

MOVE is an ownership state transition, not capability delegation. Concurrent MOVE attempts have at most one winner. The old sender object becomes logically invalid even though CLR storage may remain physically alive.

### REG-008 — reclaim / pool reuse

Safe reuse sequence:

```text
stop new use
 -> close or quarantine outstanding uses
 -> revoke logical handles
 -> advance generation
 -> zero/clear according to confidentiality policy
 -> return to pool/reuse
```

Generation invalidation gives temporal authority safety; it does not erase confidential bytes.

### REG-009 — ambiguous external completion

Cancellation request, transport loss or provider timeout does not prove memory safety. If the provider cannot prove cancellation/closure, affected storage remains quarantined until exact closure or trusted containment is established.

## 11. SIP sentries and object graph rules

### SIP-001 — every cross-compartment call is a security transition

Generated sentry code MUST validate:

- exact caller process/domain generation;
- `EndpointSession` identity/generation/state;
- required local capabilities and constraints;
- sealed-handle types and object/service generation;
- Region borrow/use/MOVE state;
- temporary delegation scope;
- response authority and ownership return;
- publication/release prerequisites.

### SIP-002 — exact invocation projection

`InvocationAuthorityContext` MUST NOT be a general service locator or enumerable capability bag. Generated code SHOULD create operation-specific internal projections containing only authority required for that method invocation.

### SIP-003 — no ambient authority

ManagedCap code MUST NOT derive authority from global service locators, `AsyncLocal`, process-wide capability managers, host filesystem/network/process APIs, or globally enumerable session/resource dictionaries.

Private trusted service tables are permitted if ordinary invocation code can reach only the exact object authorized by its explicit handle/session/capability.

### SIP-004 — mutable raw CLR references

A mutable raw CLR object reference MUST NOT cross a ManagedCap trust boundary. Reachable object graph is implicit authority.

### SIP-005 — copied values require deep schema closure

`record`, `readonly`, or `init` syntax alone is not proof of deep immutability. Generated contract validation MUST allow only bounded value graphs whose reachable schema is known and admitted.

Allowed categories may include primitives, enums, explicitly bounded byte/value messages and recursively admitted immutable value shapes.

### SIP-006 — exceptions and cancellation

Temporary authority, borrows and delegation MUST close on success, exception, cancellation and dispatcher failure. Finalizers are not a security-critical cleanup mechanism.

### SIP-007 — publication scope

The runtime may guarantee staged publication only for state/effects that remain under runtime/provider staging control. It MUST NOT claim transactional rollback of arbitrary managed side effects already performed by service code.

## 12. ManagedCap security profile

### MCP-001 — profiles

The system defines at least:

```text
ManagedCap
TrustedRuntime
NativeIsolated
PlatformExternal
```

### MCP-002 — ManagedCap

ManagedCap is untrusted managed component code admitted under a strict closed policy. By default it denies:

- `DllImport` / `LibraryImport`;
- `UnmanagedCallersOnly` where it creates an unmanaged boundary;
- unmanaged function pointer invocation / `calli`;
- arbitrary `Unsafe` memory access;
- `NativeMemory`;
- dangerous `Marshal`, `MemoryMarshal`, `CollectionsMarshal` surfaces;
- unrestricted `GCHandle`/address extraction;
- `Reflection.Emit`;
- non-public reflection mutation/invoke except explicit narrow allowlist;
- runtime assembly loading from bytes/path/network;
- arbitrary `AssemblyLoadContext` loads;
- dynamic code generation / DLR where it expands unverified invocation;
- direct host filesystem/network/process/device/registry authority;
- undeclared native assets;
- deserializers capable of arbitrary type materialization;
- ambient authority-bearing mutable statics.

### MCP-003 — verification stack

Security verification MUST include:

```text
source analyzer          developer feedback
+
post-build IL/metadata   compiler-independent enforcement
+
transitive dependency/native-asset closure
+
AOT/link closed-world evidence where selected
+
runtime generation/revocation revalidation
```

Source analyzers are not security proof.

### MCP-004 — existing AdmissionVerifier is the final static pipeline

ManagedCap policy MUST be implemented by refactoring/extending existing `SingPlus.Admission`. Separate scanners may exist internally, but there is one final admission result and one policy version.

### MCP-005 — full-module verification for v1

ManagedCap v1 MUST inspect the entire component assembly for forbidden metadata/interop/IL surfaces rather than relying only on root reachability. Closed-world reachability optimization may be added later only with equivalent evidence.

### MCP-006 — transitive closure

Every managed dependency and native asset in the admitted closure MUST be categorized and digest-bound. Unknown/unresolved runtime loads fail admission.

### MCP-007 — NativeAOT

Production `QualifiedManaged` SHOULD require a qualified NativeAOT build unless an alternative closed-world JIT profile is separately proven. NativeAOT is evidence and attack-surface reduction; it is not capability authority by itself.

### MCP-008 — C# 15 preview

C# 15 memory-safety work is useful audit metadata but is not a SingCap guarantee. The verifier examines actual metadata/IL/API behavior independent of whether source used the `unsafe` keyword.

### MCP-009 — NativeIsolated

`NativeIsolated` requires an independently enforced protection boundary such as a separate OS process, VM or qualified platform protection domain. Native code in the same unrestricted address space as ManagedCap cannot be classified as NativeIsolated.

## 13. Manifest, audit and supply chain

### MAN-001 — ServiceManifestV2 is additive

V2 reuses V1 identity/version/image/contracts/dependencies/platform/resource/budget/lifecycle semantics and adds only SingCap-specific fields such as:

```text
SecurityProfile
DependencyContentDigests / closure identity
StaticCapabilityImports
SealedTypeImports/Exports
MemoryPolicy
RuntimePolicy
DelegationPolicy
AuthorityTableQuotas
AdmissionPolicyVersion
AotPolicy
```

### MAN-002 — manifest is intent, not authority

A manifest declares requested static authority. Runtime startup validates image/dependencies/profile and then mints exact local authority. Manifest contents do not authorize operations by themselves.

### MAN-003 — deterministic audit artifact

Admission/build MUST emit deterministic `singcap-audit-v1.json` including at least:

```text
component identity/version
image digest
dependency graph + digests
native asset inventory
security profile
provided/required contracts
static capability imports
sealed imports/exports
delegation policy
memory and authority-table quotas
region-sharing contracts
unsafe/native/reflection/dynamic results
admission verifier version/policy digest
SDK/runtime/AOT compiler version tuple
HybridCPU package version/digest/source commit where consumed
final static verdict
```

The audit file is evidence, not authority.

### MAN-004 — authority/audit consistency

A production ManagedCap component MUST NOT receive undeclared static startup authority that is absent from the audit/manifest decision. Dynamic authority delegated through an authorized runtime protocol must be separately auditable by event identity and lineage.

### MAN-005 — artifact provenance

`ProductionCandidate` requires defined signing/provenance/reproducible-build policy. Hash pinning proves content identity, not publisher authenticity.

### MAN-006 — version drift

Changing SDK, runtime, AOT compiler, admission verifier/policy, or provider contract package requires requalification according to the claim level.

## 14. External operations and HybridCPU boundary

### EXT-001 — SingNext authority remains local

HybridCPU MUST NOT mint SingCap authority, choose local Capability IDs, host the SingNext derivation graph, or serve as a CHERI-emulation layer.

### EXT-002 — provider guard is secondary enforcement/evidence

Existing HybridCPU owner/context/domain guards and the new `ExternalOperationAdmissionBinding` MAY be used as independent coarse/provider-side checks. A CPU guard receipt is not a SingNext capability and a provider admission receipt is not a local capability.

### EXT-003 — exact correlation/generation

For external operations, SingNext must preserve exact operation correlation and current provider generation snapshots. Cross-request receipts or generation drift are stale.

### EXT-004 — lifecycle

The target external lifecycle remains conceptually:

```text
Prepare -> Admit -> Submit -> DeviceComplete -> Visible -> Publish -> Release
```

Completion is not visibility, publication or release.

### EXT-005 — new HybridCPU publication gate

Where the current HybridCPU contract surface is adopted, `ExternalOperationPublicationEvidence` / gate may be used as an additional provider/CPU fail-closed check before CPU-side staged publication. SingNext still owns its local publication decision and Region/use closure.

### EXT-006 — cancellation ambiguity

Unconfirmed provider cancellation never authorizes Region reuse or ownership return. Quarantine/containment remains necessary.

### EXT-007 — package/source binding

Because package `HybridCPU.ExternalRuntime.Contracts` remains `1.14.0` while current source evolved, SingNext qualification MUST record:

```text
NuGet package version
package SHA-256
HybridCPU source commit
public API/contract schema versions
```

A different package digest under the same version is a supply-chain change requiring explicit review.

## 15. Performance requirements

### PERF-001
Capability-table lookup target: expected `O(1)`.

### PERF-002
Resource/subject generation checks: `O(1)`.

### PERF-003
Revocation ancestry may be `O(depth)` only with a small explicit maximum depth.

### PERF-004
No capability-table access occurs per ordinary scalar/array/Span element access after successful borrow/view acquisition.

### PERF-005
Sentry may reuse validation results only for the exact invocation/effect lease and only until its invalidation boundary.

### PERF-006
Caches MUST include appropriate revocation/resource/session epochs; no authorization cache may outlive invalidation silently.

## 16. Concurrency requirements

The qualification suite MUST cover at least:

```text
validate vs revoke
derive vs revoke
quota consume vs revoke
sibling quota consume
one-shot double consume
double MOVE
borrow read vs write
session close vs invocation
session close vs async completion
cancel vs provider completion
provider generation change vs publication
service restart vs stale sealed handle
pool reuse vs stale borrow
runtime restart vs persisted token replay
```

Each security-critical state machine MUST document its linearization point.

## 17. Error model

Typed errors SHOULD include or refine:

```text
CapabilityNotFound
CapabilitySubjectMismatch
CapabilityRightsDenied
CapabilityConstraintDenied
CapabilityRevoked
CapabilityStaleSubjectGeneration
CapabilityStaleResourceGeneration
CapabilityRealmMismatch
CapabilityConsumed
CapabilityDelegationDepthExceeded
CapabilityQuotaExceeded
CapacityExhausted

SealTypeMismatch
SealObjectStale
SealServiceIncarnationMismatch
SealUnsealerDenied

RegionStaleGeneration
RegionRangeInvalid
RegionAlignmentInvalid
RegionBorrowConflict
RegionOwnershipMismatch
RegionMutationEpochMismatch
RegionQuarantined

SessionStale
SessionClosed
CompartmentProfileViolation
AdmissionDependencyViolation
AdmissionNativeAssetViolation
AmbientAuthorityViolation
```

Security failure MUST NOT silently downgrade to a weaker implementation path.

## 18. Diagnostics and confidentiality

### DIAG-001
Diagnostics/evidence MUST NOT become reusable authority. An authority inspector requires explicit diagnostic privilege and SHOULD return redacted identity/metadata rather than live reusable handles.

### DIAG-002
Telemetry must not leak capability tokens, secret path/content data, Region contents or provider-private identities unless explicitly authorized.

### DIAG-003
Cross-security-domain storage reuse MUST define a zeroization policy. Temporal generation safety is not confidentiality erasure.

## 19. API-shape guidance

### 19.1 Capability handles

Opaque value types are acceptable because copying a token does not duplicate authoritative consumable state. Equality/hash MAY compare opaque identity; application code MUST NOT infer rights from equality.

### 19.2 Sealed handles

Prefer opaque marker-typed handles, not implementation-resource generics.

### 19.3 Region borrows

Lexical view: `ref struct`. Async lease: reference type or opaque handle with explicit close/revalidation. Never box/store Span.

### 19.4 Serialization

Wire tokens are ephemeral references bound to authority realm/service/session generations. Serialization does not make them durable credentials.

### 19.5 CapRef / SharedArena

Neither is required for SingCap-M v1. They are deferred until a concrete workload demonstrates that SIP + sealed handles + Region sharing cannot represent the requirement efficiently and safely.

## 20. Claim levels

```text
ModelOnly
StaticAdmission
RuntimeEnforced
QualifiedManaged
ProductionCandidate
```

Promotion is explicit and evidence-based.

- **ModelOnly:** contracts/models only.
- **StaticAdmission:** static policy proof exists but runtime authority enforcement incomplete.
- **RuntimeEnforced:** capability/Region/seal/SIP runtime gates active.
- **QualifiedManaged:** negative/adversarial/property/concurrency suite + deterministic dependency closure passed.
- **ProductionCandidate:** adds TCB review, artifact provenance/signing policy, reproducible qualification and platform-specific evidence.

## 21. Mandatory tests and properties

### Capability algebra

- rights/resource/range/operation/quota/lifetime widening attempts;
- sibling union attempt;
- delegation depth bypass;
- wrong subject/process generation;
- revoked ancestor/live-looking child;
- consumed handle replay;
- runtime-incarnation replay;
- capability-table exhaustion.

Property:

```text
for every admitted derivation chain:
EffectiveAuthority(child) is a subset of EffectiveAuthority(parent)
```

Property:

```text
after revoke, generation advance or authority-realm restart:
an old handle cannot regain authority without a privileged new mint/re-import
```

### Sealing

- wrong marker/type id;
- wrong service incarnation;
- wrong session/unsealer;
- stale generation;
- revoked object;
- sibling enumeration attempt.

### Region

- arithmetic overflow;
- alignment/type mismatch;
- stale Region/Borrow generation;
- write/read conflicts;
- double MOVE;
- cancellation/completion race;
- provider loss quarantine;
- pool reuse with stale views;
- zeroization policy test.

### SIP

- omitted/wrong capability;
- stale/wrong session;
- mutable object graph rejection;
- response authority laundering;
- temporary grant cleanup on exception/cancel;
- session close racing async completion.

### Admission

Crafted assemblies covering all denied native/unsafe/reflection/dynamic/static/dependency surfaces, including forbidden code hidden outside the static root graph.

### HybridCPU/provider

- exact contract/package/source identity;
- CPU guard present but provider admission denied;
- provider admitted but CPU guard stale;
- cross-request receipt;
- provider generation drift;
- DeviceComplete without Visible;
- Visible without SingNext publication;
- cancellation ambiguity/quarantine;
- release only after exact closure.

## 22. Definition of Done — SingCap-M v1

SingCap-M v1 is complete only when all are true:

1. one local capability authority ledger exists; V1/V2 cannot diverge;
2. authority-realm and service-incarnation replay protection is implemented;
3. IDs/generations have fail-closed exhaustion semantics;
4. typed monotonic constraints and non-amplifying quotas are enforced;
5. subtree revocation and operation-effect linearization are implemented and stress-tested;
6. generic software sealing is deployed in at least one real service and then representative services;
7. Region subrange/read/write/async-use is an extension of RegionAuthority;
8. SIP sentries generate exact non-ambient invocation authority projections;
9. raw mutable object graphs are rejected at ManagedCap boundaries;
10. ManagedCap extends the existing AdmissionVerifier, uses full-module scan and deterministic dependency/native closure;
11. manifest/audit/provenance surfaces are deterministic and CI-enforced;
12. filesystem/network/process representative services use the strengthened model;
13. HybridCPU provider integration remains semantic and does not change ISE;
14. exact HybridCPU package digest/source commit is qualified;
15. adversarial/property/concurrency/performance suites pass;
16. documentation claim matrix matches live code and does not use CHERI-equivalent wording.

## 23. Deferred items

The following are explicitly not required for v1:

```text
CapRef<T>
SharedArena
unbounded capability object heap
general shared mutable CLR object graph
new HybridCPU capability ISA
per-load capability checks
hardware sealing emulation
real-time/side-channel guarantees
```

## 24. Normative source references

### SingNextOS

- `https://github.com/yuriyyak23/SingNextOS/tree/a67eea1aafc72054d22f1586b62c6883cdc71681`
- `contracts/SingPlus.Contracts/Capabilities.cs`
- `contracts/SingPlus.Contracts/Regions.cs`
- `contracts/SingPlus.Contracts/ComponentManifests.cs`
- `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs`
- `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs`
- `src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs`
- `src/Sip/SingPlus.Sip/Regions/OwnedBuffer.cs`
- `src/Runtime/SingPlus.Runtime/Services/EndpointSessionRegistry.cs`
- `tools/SingPlus.Admission/AdmissionVerifier.cs`

### HybridCPU-v2

- `https://github.com/yuriyyak23/HybridCPU-v2/tree/794c4a53494f503855ac8cf209efab23fde083b2`
- `HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs`
- `HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs`
- `HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs`
- `HybridCPU_ExternalRuntime/ExternalOperationAdapterSession.cs`
- `HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Backends/ProviderNeutralExternalAcceleratorBackend.cs`
- `HybridCPU_ISE/CloseToHSL/Core/Runtime/Domains/SecureCompute/Policies/Admission/SecureDomainAdmissionPolicy.cs`

### .NET / CHERIoT

- `https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/rc1/11.0.0-rc.1.md`
- `https://learn.microsoft.com/dotnet/csharp/whats-new/csharp-15`
- `https://learn.microsoft.com/dotnet/csharp/language-reference/unsafe-code`
- `https://learn.microsoft.com/dotnet/core/deploying/native-aot`
- `https://cheriot.org/book/concepts.html`
- `https://cheriot.org/book/compartments.html`
- `https://cheriot.org/book/audit.html`
