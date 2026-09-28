# SingNextOS

**Capability-native, ownership-oriented operating-system research platform for typed services, managed isolation, compositional resource authority, and provider-neutral heterogeneous execution.**

SingNextOS explores an operating-system architecture in which **semantic authority, mutable-memory ownership, quantitative resource admission, publication, and reclamation remain explicit OS-owned facts**, while execution on CPUs, accelerators, devices, virtualization backends, CXL/fabric resources, and HybridCPU-v2 is expressed through narrow semantic provider contracts.

The system is implemented in C#/.NET and uses generated typed **SIP** service protocols instead of a large untyped syscall ABI.

SingNextOS is designed around one central rule:

```text
no observation, identity, mapping, scheduling decision,
provider receipt, completion event, or cached plan
may silently substitute for the authoritative owner
of the fact being decided
```

The resulting architecture combines:

```text
capability security
+
explicit memory ownership
+
typed service-state transitions
+
quantitative resource conservation
+
provider-neutral heterogeneous execution
+
explicit publication and reclamation
```

> **Project status:** systems-research OS/runtime architecture and executable conformance platform.
> Stronger security, execution, timing, or provider claims are enabled only for exact qualified contours. Source code, executable tests, qualification artifacts, and pinned provider/package tuples outrank descriptive documentation when they disagree.

---

## Architectural identity

After the SingCap-M and vNext refactorings, SingNextOS is best understood as a **compositional authority OS for managed services and heterogeneous execution**.

It does not collapse all security and execution facts into one handle, one scheduler, one provider object, or one global state machine.

Instead, the system deliberately maintains independent authoritative owners for different kinds of truth:

```text
CapabilityAuthority
    WHAT semantic actions may this subject perform?

RegionAuthority
    WHO owns or may temporarily use these exact bytes?

ResourceBudgetAuthority
    HOW MUCH quantitative capacity is reserved, consumed,
    settled, or quarantined?

EndpointSession / invocation owners
    WHO is talking to WHOM, through WHICH protocol state
    and WHICH exact invocation?

ExternalOperationAuthority
    WHICH external effect is currently in flight?

Publication owners
    WHEN does a result become system-visible truth?

Provider / HybridCPU
    CAN the exact operation execute legally on this platform?
```

These facts compose at admission and lifecycle boundaries, but none is allowed to impersonate another.

---

# Core architectural principles

SingNextOS intentionally enforces distinctions that conventional handle/syscall systems often blur:

```text
identity != authority
discovery != authority
intent != authority
mapping != ownership
coherence != ownership
accounting != permission
resource permission != effect permission
reservation != guarantee
provider admission != SingNext authority
compiler metadata != runtime legality
evidence != authority
completion != visibility
visibility != publication
publication != release
cancellation request != proof of no effect
provider loss != resources safely reclaimable
```

The corresponding design rules are:

* **Authority stays local to SingNextOS.** HybridCPU and other providers may admit and execute work, but they do not mint SingNext capabilities or decide local ownership/publication.
* **Mutable data has an explicit owner.** Sharing is expressed through bounded borrow/use state rather than ambient shared mutable memory.
* **Quantitative resource truth has one owner.** `ResourceBudgetAuthority` owns reservation, consumption, settlement, and quarantine.
* **Effect permission and resource permission are distinct.**
* **Schedulers and planners are policy, not authority.**
* **Generated SIP sentries are security transitions.**
* **Unknown external state fails closed.**
* **Restart never resurrects stale authority.**
* **Public DTOs, receipts, certificates, manifests, and telemetry are not trusted merely because they are well formed.**
* **Provider-private hardware details do not enter application/SIP authority ABI.**
* **Compatibility layers remain downstream projections of native SingNextOS semantics.**

---

# Architecture at a glance

```text
Application / source-facing Sing+ APIs
                    |
                    v
        Generated typed SIP client
                    |
                    v
         Service discovery metadata
                    |
                    v
             EndpointSession
                    |
                    v
        Generated SIP security sentry
                    |
        +-----------+------------+
        |           |            |
        v           v            v
 Capability     Region       Resource-use
 Authority      Authority    capability grant
        |           |            |
        |           |            v
        |           |    ResourceBudgetAuthority
        |           |      reservation / lease
        |           |            |
        +-----------+------------+
                    |
                    v
        Cross-owner admission protocol
                    |
                    v
          ExternalOperationAuthority
                    |
                    v
             ComputePlanning
           / ResourceScheduler
          (policy / evidence only)
                    |
                    v
          PlatformAuthorityBridge
                    |
                    v
       Provider-neutral semantic contracts
                    |
          +---------+---------+
          |                   |
          v                   v
      Host/model       executable adapters
                              |
                              v
                    HybridCPU ExternalRuntime
                              |
                              v
                    HybridCPU runtime / ISE
                              |
                              v
                    execution / retire
                              |
                              v
                    provider completion
                              |
                              v
                         visibility
                              |
                              v
                    SingNext publication
                              |
                              v
             release / reclaim / settlement
```

The key property is that this is **not one giant transaction or lock**. Each authoritative state machine retains its own linearization point.

Cross-owner operations use explicit:

```text
prepare
 -> reversible reservation
 -> exact generation revalidation
 -> local commit
 -> irreversible submit
 -> settlement / compensation / quarantine
```

---

# Compositional admission model

For a resource-consuming external operation, admission is conceptually:

```text
EffectAllowed
AND ResourceUseAllowed
AND QuantitativeReservationLive
AND DataOwnershipAllowed
AND ExactSessionAndGenerationsLive
AND ProviderAdmissionAllowed
AND CpuRuntimeLegal
AND FeatureContourQualified
    -> irreversible execution may become eligible
```

No term substitutes for another.

Examples:

```text
Effect capability present
+ no resource lease
    -> no resource-consuming execution

Resource lease present
+ no effect capability
    -> no semantic effect

Provider accepted operation
+ stale SingNext capability
    -> no valid local admission

SingNext authority valid
+ HybridCPU runtime legality false
    -> no execution

DeviceComplete
+ memory not Visible
    -> no publication
```

This compositional predicate is the central security/execution model of SingNextOS.

---

# Authoritative owner map

| Owner                                        | Authoritative truth                                                                   | Explicitly does not own                                           |
| -------------------------------------------- | ------------------------------------------------------------------------------------- | ----------------------------------------------------------------- |
| `CapabilityAuthority`                        | semantic permission, derivation, revocation, typed constraints                        | budget counters, Region ownership, provider legality, publication |
| `ResourceBudgetAuthority`                    | limits, quantitative reservation, lease state, consumption, settlement, quarantine    | effect permission, Region reclaim, provider truth                 |
| `ProcessRegistry`                            | process identity/incarnation                                                          | capability rights, budget accounting                              |
| `EndpointSessionRegistry` / invocation owner | session state, invocation identity and protocol state                                 | provider legality, global budget truth                            |
| `RegionAuthority`                            | mutable ownership, borrow/use state, generations, mutation state, reclaim eligibility | effect permission, budget settlement                              |
| `SealedObjectAuthority`                      | sealed object identity and liveness                                                   | unrelated resource/effect authority                               |
| `ExternalOperationAuthority`                 | exact external-effect lifecycle                                                       | budget mint/refund, publication substitution                      |
| response/publication owners                  | response and publication truth                                                        | provider completion, resource settlement                          |
| `PlatformAuthorityBridge`                    | local platform-binding state and provider correlations                                | capability minting                                                |
| planners / schedulers                        | policy, candidate selection, cached evidence                                          | authority                                                         |
| provider / HybridCPU ExternalRuntime         | provider admission and execution evidence                                             | SingNext authority/publication                                    |
| HybridCPU runtime                            | runtime legality and architectural retire                                             | SingNext effect/resource authority                                |

This avoids a second capability universe, a second Region ledger, a second quantitative budget ledger, or replicated publication truth.

---

# Capability model: SingCap-M

SingCap-M turns the capability subsystem into the system-wide substrate for **live, generation-exact, monotonically narrowing authority**.

A capability is an opaque authority reference backed by an authoritative record rather than a caller-editable descriptor.

Conceptually:

```text
AuthorityRealmId
CapabilityToken
SubjectIdentity + SubjectGeneration
ResourceIdentity + ResourceGeneration
EffectiveConstraints
ParentRevocationNode
DelegationDepth
Quota lineage
Session/lifetime constraints
State
```

The fundamental derivation rule is:

```text
Authority(child) subset-of Authority(parent)
```

Subset semantics apply to more than rights:

```text
rights
resource identity
range/subresource
operations
quota
lifetime
target subject
session
provider-semantic scope
delegation depth
assurance ceiling
```

Unknown constraint types fail closed.

---

## Non-amplifying delegation

A parent may derive narrower authority for another subject, but derived siblings cannot be recombined into authority wider than their lineage permits.

```text
Parent A
  |
  +--> Child B
  |
  +--> Child C

B union C != reconstructed A
```

unless a privileged operation still owns the exact authority required for that union.

This supports least-authority chains such as:

```text
application
 -> service
 -> helper
 -> compute broker
 -> provider adapter
```

without making downstream components ambiently equivalent to upstream principals.

---

## Revocation and operation admission

A successful capability lookup is not a durable permission for a later irreversible effect.

```text
Validate(capability)
!=
permission to perform an effect after arbitrary delay
```

Effectful paths therefore use exact operation-admission semantics.

A revocation race has a clear linearization result:

```text
revoke first
    -> new operation admission fails

operation admission first
    -> already-admitted effect follows explicit
       completion/cancellation/publication/closure semantics
```

Revocation prevents future admission; it does not rewrite physical history.

---

## Authority realms and restart safety

Ephemeral authority belongs to an exact authority realm/runtime incarnation.

After:

```text
runtime restart
service restart
process-generation change
resource-generation change
```

an old opaque token cannot become authority over a newly created object simply because an identifier was reused.

Persisted authority requires an explicit privileged re-authorization protocol.

Ordinary handles are **not durable credentials**.

---

# Software sealing

SingNextOS supports opaque service-owned objects through software sealing.

Representative shapes include:

```text
SealedHandle<File>
SealedHandle<Socket>
SealedHandle<Window>
SealedHandle<Queue>
```

The public handle contains opaque identity only.

The authoritative object record may bind:

```text
SealObjectId
SealTypeId
AuthorityRealm
service incarnation
object generation
owner / subject / session constraints
allowed operation class
revocation state
internal resource reference
```

Ordinary application code does not receive a generic object resolver.

Unsealing is internal, type-specific, operation-specific, and generation-checked.

A service restart advances service incarnation and invalidates stale sealed handles unless a separate durable recovery protocol explicitly recreates them.

---

# ManagedCap security profile

`ManagedCap` treats managed component code as potentially adversarial rather than implicitly trusted.

The profile denies or restricts escape paths such as:

```text
DllImport / LibraryImport
unmanaged calli / arbitrary function pointers
NativeMemory
unsafe unmanaged memory access
dangerous Marshal / MemoryMarshal / CollectionsMarshal usage
unrestricted GCHandle/address extraction
Reflection.Emit
non-public reflective mutation/invocation
arbitrary AssemblyLoadContext loads
runtime assembly loading from arbitrary bytes/path/network
undeclared native assets
dynamic code paths that expand unverified invocation
direct ambient host filesystem/network/process/device authority
authority-bearing mutable global state
```

Security does not depend on source analysis alone.

The verification stack is:

```text
source analyzer
+
post-build IL / metadata verification
+
transitive managed dependency closure
+
native-asset closure
+
AOT/JIT qualification
+
runtime generation/revocation revalidation
```

`AdmissionVerifier` remains the single static admission pipeline rather than creating an independent second verifier universe.

---

## Security profiles

Representative execution/security profiles include:

```text
ManagedCap
TrustedRuntime
NativeIsolated
PlatformExternal
```

`NativeIsolated` requires an independently enforced protection boundary, such as a separate process, VM, or qualified platform protection domain.

Native code executing unrestricted inside the same trusted address space cannot be relabeled as isolated merely by convention.

---

# Typed SIP: native service protocol layer

SIP is SingNextOS's native typed service protocol system.

Contracts are expressed as C# interfaces and compiled into deterministic protocol metadata and runtime adapters by the SingPlus generators.

Representative contract vocabulary includes:

```text
[SipContract]
[Message]
[InitialState]
[Transition]
[RequiresCapability]
[Borrows]
[Consumes]
[ReturnsOwnership]
[BoundedPayload]
```

SIP is more than serialization.

Every cross-compartment SIP invocation is a **security transition**.

Generated sentries validate the exact invocation context before user implementation code runs.

---

## Exact invocation projection

A sentry may validate:

```text
caller process/domain generation
EndpointSession identity/generation/state
protocol state
required effect capabilities
resource-use grants
budget/resource lease
sealed object type/generation
Region borrow/use/MOVE state
temporary delegation/donation
response authority
publication/release prerequisites
```

The result is an operation-specific internal authority projection.

SingNextOS deliberately avoids designs such as:

```text
ServiceContext {
    all_process_capabilities;
    all_sessions;
    all_resources;
}
```

because an enumerable global authority bag creates confused-deputy and ambient-authority risk.

---

# EndpointSession and invocation authority

Service discovery returns metadata.

It does not grant permission.

The normal invocation flow is:

```text
resolve service
 -> verify contract identity
 -> open EndpointSession
 -> bind exact caller/service process generations
 -> establish protocol state
 -> validate required authority
 -> execute typed messages
 -> close/cancel explicitly
```

`EndpointSession` is therefore the authority-bearing invocation context.

Invocation identity and cancellation state remain explicit rather than being inferred from transport objects or ambient async state.

---

# Request-scoped resource donation

vNext extends invocation semantics with safe resource donation.

This addresses a common confused-deputy/DoS pattern:

```text
cheap client request
    ->
privileged server spends expensive server resources
```

A caller may instead donate a bounded resource envelope to the exact invocation:

```text
Client
  |
  | narrowed resource grant + quantitative lineage
  v
Server
  |
  | narrower delegation
  v
Downstream service
  |
  v
Provider / accelerator
```

Donation is:

```text
request-scoped
generation-bound
provenance-preserving
non-ambient
non-amplifying
```

It is not copied into global server state.

Nested donation cannot silently widen:

```text
resource class
amount
validity
priority ceiling
assurance
provider semantic scope
delegation depth
```

---

## No budget laundering

The donation model explicitly prevents:

```text
budget laundering
priority laundering
assurance escalation
resource-class widening
provider-scope widening
double charging
double refund
```

Server-owned fallback capacity, when policy permits it, is separately identified and separately accounted.

It cannot silently be attributed to the caller's lineage.

---

# Ownership-oriented memory

SingNextOS treats mutable-memory ownership as an authority fact rather than as a consequence of mapping.

The model is built around:

```text
RegionId
RegionGeneration
RegionOwner
MutationEpoch
OwnedRegion<T>
OwnedBuffer<T>
BorrowLeaseHandle
RegionUseHandle
RegionBackingLeaseHandle
```

Three generation/version concepts are deliberately distinct:

```text
RegionGeneration
    allocation / reuse / ownership identity

BorrowGeneration
    exact temporary borrow instance

MutationEpoch
    content / coherence / publication state
```

This prevents object lifetime, borrow lifetime, and content version from being conflated into one ambiguous counter.

---

## MOVE

MOVE is an ownership transition.

```text
sender owns region
      |
      v
atomic ownership transition
      |
      v
receiver owns region
```

After successful MOVE, the sender's previous object becomes logically invalid even if the underlying CLR storage remains physically allocated.

Concurrent MOVE attempts have at most one winner.

---

## BORROW

BORROW retains the original owner while granting bounded temporary access.

```text
owner remains authoritative
+
temporary access
+
exact borrow generation
+
explicit close
```

Lexical borrows may use stack-bound views such as `ref struct`.

Asynchronous use carries a heap-safe lease/opaque handle and rematerializes stack views only after authoritative revalidation.

`Span<T>` itself is not persisted across suspension.

---

## Region-use modes

Representative Region-use modes include:

```text
ReadOnly
ExclusiveWrite
StagedOutput
DirectCoherentWrite
DevicePrivate
SharedReadMostly
```

Conflicts are decided by `RegionAuthority`, not by caller claims.

For example:

* overlapping exclusive writes conflict;
* write/read overlap follows the declared stability policy;
* device-private and staged-output states follow external-operation lifecycle;
* direct coherent write is a stronger explicit contour rather than an assumed property.

---

## Safe reclaim

Safe reuse follows an explicit sequence:

```text
stop new use
 -> close or quarantine outstanding uses
 -> revoke logical handles
 -> advance generation
 -> zero/clear where confidentiality requires it
 -> return storage to pool/reuse
```

Generation invalidation provides temporal authority safety.

It does not by itself erase confidential bytes.

---

# Quantitative resource model

vNext adds quantitative resource isolation without creating a separate `TemporalResourceAuthority`.

Resource control is composed from two existing authoritative owners:

```text
CapabilityAuthority
    may this subject consume resource class R
    under semantic envelope E?

AND

ResourceBudgetAuthority
    is exact quantitative capacity actually
    reserved/leased now?
```

This distinction is fundamental:

```text
permission != capacity
capacity != effect permission
reservation != guarantee
accounting != authority
```

---

## Resource-use grants

A resource-use grant is a constrained capability in the existing `CapabilityAuthority`.

It may express permission such as:

```text
may consume ComputeTime
up to semantic ceiling X
for subject S
during validity interval T
with assurance <= A
for provider semantic scope P
```

Deriving such a grant does **not** reserve quantitative capacity.

Actual reservation happens only in `ResourceBudgetAuthority`.

---

## ResourceBudgetAuthority

`ResourceBudgetAuthority` is the sole local quantitative owner for:

```text
configured limits
used capacity
reservations
leases
consumption state
settlement
refund/release
quarantine
```

Its reservations admit capacity but do not authorize semantic effects.

A budget account, snapshot, pressure value, or reservation DTO cannot itself authorize compute, DMA, network, CXL, or any other operation.

---

## Resource conservation

For one budget lineage and one resource dimension:

```text
available
+ reserved
+ conservatively_charged
+ irreversibly_consumed
    <= admitted parent limit
```

This invariant holds under concurrency.

Two capability descendants may each express overlapping maximum permission, because capability grants are not quantitative reservations.

Actual consumable capacity is committed exactly once by `ResourceBudgetAuthority`.

---

## Resource lease lifecycle

A representative quantitative lifecycle is:

```text
Prepared
 -> Reserved
 -> Bound
 -> Consuming
 -> Settling
 -> Released
```

After external consumption may have occurred:

```text
Consuming / ambiguous
 -> Quarantined
 -> Reconciled
 -> Settling / Released
```

A cancellation request is an event, not proof of zero consumption.

Before irreversible submit, a reservation may be safely released.

After possible submit, settlement requires exact reconciliation, trusted containment, or conservative charging.

---

# Resource families and dimensional correctness

SingNextOS does not introduce one universal scalar for all resources.

Resource families retain typed semantics.

## Time resources

Examples:

```text
ComputeTimeNs
ManagedRuntimeTime
qualified execution-time classes
```

Typical operations include:

```text
narrow
reserve
consume
settle
replenish where qualified
```

Time in one execution class is not automatically fungible with time in another.

---

## Throughput resources

Examples:

```text
DmaBytesPerWindow
NetworkTxBytesPerWindow
FabricBytesPerWindow
MemoryBandwidthBytesPerWindow
```

A throughput budget means a quantity over an explicit time window.

It does not imply queue occupancy, service latency, or guaranteed completion time.

---

## Occupancy resources

Examples:

```text
DeviceLocalMemoryBytes
GuestMemoryBytes
QueueSlots
InflightOperations
AcceleratorContexts
```

These resources are held for a lifetime and released only after authoritative closure.

They are not automatically replenished like periodic CPU-time budgets.

---

## Forbidden dimensional operations

The common algebra rejects meaningless operations such as:

```text
CPU ns + GPU ns
CPU ns + bytes/sec
queue slots + bytes
energy + time
```

unless a separate versioned policy defines an explicit mapping, and such a mapping does not silently become admission authority.

---

# Multi-resource admission

A heterogeneous operation may require a resource vector:

```text
ComputeTime
+
DMA bandwidth
+
Device-local memory
+
Queue slot
+
Inflight-operation capacity
```

Reversible reservations are acquired in a canonical order.

Only after all required local owners have been prepared and exact generations revalidated may the operation cross the irreversible submit boundary.

Conceptually:

```text
reserve ComputeTime
 -> reserve DMA bandwidth
 -> reserve DeviceMemory
 -> reserve QueueSlot
 -> revalidate capabilities/session/Region/provider generations
 -> commit local bindings
 -> submit
```

If a pre-submit step fails, reservations unwind.

After submit may have occurred, the system uses settlement, compensation, or quarantine rather than pretending rollback is still possible.

---

# Cross-owner admission protocol

Security-critical work often depends on several independent owners.

SingNextOS therefore uses a prepare/revalidate/commit discipline.

```text
Prepare intent/session/Region/external operation
        |
        v
Validate effect capability
        |
        v
Validate resource-use grant
        |
        v
Reserve quantitative lease
        |
        v
Revalidate exact generations
        |
        v
Commit local consumptive bindings
        |
        v
Release authority locks
        |
        v
Provider admission / submission
```

No provider, service callback, continuation, or application code executes while capability, Region, budget, session, or ExternalOperation authority locks are held.

This prevents authority locks from becoming arbitrary re-entrancy or distributed-lock boundaries.

---

# ExternalOperation lifecycle

External work uses one explicit lifecycle:

```text
Prepared
 -> Admitted
 -> Submitted
 -> DeviceComplete
 -> Visible
 -> Published
 -> Released
```

The operation record carries exact identities, generations, dependencies, effect classification, visibility requirements, publication policy, provider binding, replay protection, and Region-use state.

The architecture enforces:

```text
Admitted       != Submitted
Submitted      != DeviceComplete
DeviceComplete != Visible
Visible        != Published
Published      != Released
```

Resource settlement is correlated with this lifecycle but remains a separate authoritative state machine.

---

## Resource lifecycle vs effect lifecycle

For one operation:

```text
ExternalOperation:
Prepared -> Admitted -> Submitted -> DeviceComplete -> Visible -> Published -> Released

Resource lease:
Reserved -> Bound -> Consuming -> Settling/Quarantined -> Released
```

The machines are correlated but do not own one another's truth.

For example:

```text
resource consumed
!=
result published
```

and:

```text
resource settled
!=
Region safe to reclaim
```

---

# Ambiguous provider state and quarantine

SingNextOS treats unknown external state as a real state, not as a reason to guess.

Suppose:

```text
resource lease reserved
 -> operation submitted
 -> provider connection lost
```

The system must not infer:

```text
provider lost
=> operation did not execute
=> refund everything
```

Valid reconciliation outcomes include:

```text
ExactNoConsume
ExactUsage(x)
WorstCaseCharge
ContainedAndClosed
Unknown -> Quarantined
```

Unknown consumption remains quarantined until an authoritative reconciliation rule closes it.

The same principle applies to Region safety:

```text
provider lost
!=
Region safe to reuse
```

This protects both memory safety and resource conservation against disconnect/retry/replay attacks.

---

# Settlement and publication are independent

Resource use may become irreversible before application-visible publication.

Example:

```text
accelerator consumes 4 ms
 -> DeviceComplete
 -> visibility succeeds
 -> publication fails
```

The resource owner must still settle the 4 ms.

Otherwise failed publication would become a mechanism for obtaining free hardware execution.

Therefore:

```text
settlement != publication
publication != settlement
```

Similarly, settlement does not authorize Region reclaim.

---

# Compute and accelerators

Compute is exposed as semantic typed service operations, not as a raw opcode/lane interface.

A representative SIP contract shape is:

```csharp
[SipContract, InitialState("Ready")]
public interface IComputeService
{
    [Message(1)]
    [Transition("Ready", "Ready")]
    [RequiresCapability(
        ResourceKind.Compute,
        CapabilityResourceIds.Dsc1Copy,
        CapabilityRights.Execute)]
    [ReturnsOwnership]
    ValueTask<OwnedBuffer<byte>> CopyAsync(
        [Borrows] OwnedBuffer<byte> source,
        [Consumes] OwnedBuffer<byte> destination);
}
```

The application expresses:

```text
semantic effect
owned/borrowed data
resource requirements
publication expectations
```

It does not express:

```text
HybridCPU lane
raw opcode
physical slot
provider queue
DSC/L7 private token
IOMMU identifier
CXL physical topology
```

---

# ComputePlanning

`ComputePlan` is explicitly non-authoritative.

Planning may consider:

```text
semantic execution class
resource envelope
Region-use compatibility
provider availability
provider generation
performance/load evidence
publication preference
virtualization/security requirements
```

The result is a candidate placement decision.

Before execution, authoritative owners are revalidated.

A stale cached plan can never stand in for:

```text
capability still live
Region still owned
lease still active
provider generation still current
```

---

## Provider-independent placement

The same semantic operation may be placed on different providers:

```text
Host managed runtime
HybridCPU
Vector provider
Matrix provider
streaming provider
external accelerator
```

provided that the semantic contour is compatible and separately qualified.

Changing provider does not change the application's authority model.

This is one of the central goals of SingNextOS heterogeneous execution.

---

# ResourceScheduler and provider agents

Placement, fairness, load balancing, and topology-aware scheduling live outside the authority core.

A resource scheduler may consume observations such as:

```text
queue depth
load
latency class
bandwidth class
thermal/performance state
provider generation
preemption granularity
qualified feature contour
```

and produce:

```text
placement decision / scheduling hint
```

It cannot produce:

```text
authorized = true
```

as a replacement for live owner validation.

Provider agents may replicate/copy observation data.

They do not replicate:

```text
capability lineage
Region ownership
budget/lease truth
publication truth
```

This permits sophisticated scheduling policy without making the scheduler the system's security root.

---

# Temporal resource isolation

SingNextOS explicitly separates different claim strengths:

```text
AccountingOnly
RuntimeEnforced reservation
EnforcedUpperBound
GuaranteedReservation
```

These are not equivalent.

An implementation may be able to prove:

```text
subject cannot consume more than X per period
```

without being able to prove:

```text
subject will receive at least X every period
```

and neither statement alone proves:

```text
operation will complete before deadline D
```

---

## Upper-bound enforcement

A temporal upper-bound contour requires explicit answers for:

```text
measurement source
measurement trust
clock source
preemption granularity
worst-case non-preemptible interval
overrun behavior
replenishment semantics
restart semantics
burst semantics
multicore/SMT interactions
provider-loss behavior
```

No resource class is promoted merely because a DTO contains `BudgetNs` or `PeriodNs`.

---

## Guaranteed reservation

Guaranteed minimum capacity is a stronger property.

It requires provider-specific executable evidence under contention and failure.

A reservation object alone is not proof of service guarantee.

Hard realtime remains outside the default architecture unless independently qualified.

---

# SipJob: semantics-preserving service composition

SipJob is an execution-composition optimization over ordinary SIP semantics.

It is not an authority owner.

```text
SipJobPlan != authority
SipJob cache != authority
SipJob execution class != resource authority
```

Fusion may eliminate:

```text
transport
queues
waiters
intermediate materialization
some dispatch overhead
```

but it may not silently eliminate authoritative transitions.

---

## Fusion barriers

Unless exact differential proof demonstrates equivalent authoritative traces, barriers remain around:

```text
external-effect submit
consumptive resource commits
resource settlement
publication
unsafe ownership transfer
irreversible provider transitions
```

Ordinary SIP remains the semantic oracle.

---

## Parallel DAGs

Parallel branches do not race over one indivisible consumable lease.

```text
parent lease
    |
atomic split
   / \
  A   B
```

Each branch receives its own committed quantitative slice.

A join does not magically restore already-consumed authority.

This allows resource-safe heterogeneous service DAGs.

---

## Async SipJob execution

Across suspension, the runtime persists only heap-safe opaque handles and correlations.

On resume it revalidates:

```text
session generation
capability realm
lease state
Region state
provider generation
```

Cached fused plans cannot outlive their invalidation boundaries.

---

# PlatformAuthorityBridge

`PlatformAuthorityBridge` is the privileged boundary translating already-authorized local SingNext facts into provider operations.

Its job is not to become a universal HAL and not to mint application authority.

It validates exact local/provider correlations such as:

```text
DomainId
ProcessHandle
RegionHandle
local owner
provider generation
binding generation
semantic access mode
```

before provider results can influence local state.

Higher-level feature families use narrow semantic contracts rather than exposing raw hardware mechanisms.

---

# NeutralRuntime

The repository includes a physically separated HybridCPU NeutralRuntime stack:

```text
HybridCPU_NeutralRuntime.Contracts
HybridCPU_NeutralRuntime.AuthorityCore
HybridCPU_NeutralRuntime.Model
HybridCPU_NeutralRuntime.Tests
```

NeutralRuntime defines what an operation **means**, not how a particular device encodes it.

Representative semantic surfaces include:

```text
domains
Region mappings
devices
MMIO
interrupts
DMA
child domains
guest memory
virtual events/traps
bounded virtual I/O
execution artifacts
```

Provider-private queue, descriptor, opcode, lane, topology, and physical-address details remain downstream.

---

# HybridCPU-v2 integration

SingNextOS treats HybridCPU-v2 as an external execution/runtime system rather than as a source of OS authority.

The intended composition is:

```text
SingNextOS authority
    |
    v
PlatformAuthorityBridge
    |
    v
provider-neutral semantic contract
    |
    v
HybridCPU adapter / ExternalRuntime
    |
    v
HybridCPU runtime legality
    |
    v
ISE / implementation
```

HybridCPU owns:

```text
runtime execution legality
architectural retire
provider/runtime execution facts
```

SingNextOS owns:

```text
semantic authority
resource admission
Region ownership
ExternalOperation correlation
publication
local release/reclaim
```

Provider admission remains a third independent gate.

---

## No HybridCPU ISA changes

The SingCap-M/vNext architecture does not require:

```text
new opcodes
capability registers
tagged pointers
tagged memory
tagged cache lines
new pointer width
capability-aware LOAD/STORE/FETCH
changed VLIW bundle format
changed register model
lane/opcode exposure in application ABI
```

CHERI/CHERIoT-inspired ideas are used only at the software authority-algebra level:

```text
monotonic narrowing
non-amplifying delegation
provenance
bounded sub-authority
checked constraint algebra
exact generations
fail-closed stale authority
```

---

# Device, MMIO, IRQ, and DMA authority

Device access is capability-scoped rather than ambient driver privilege.

MMIO, IRQ, DMA, device execution, resource capacity, and Region ownership remain distinct authority/resource dimensions.

A DMA lifecycle is conceptually:

```text
Region ownership/use
 -> DMA/mapping authority
 -> quantitative resource reservation where required
 -> submit
 -> device completion
 -> required memory visibility/acquire
 -> close/revoke provider grant
 -> Region reclaim/reuse
```

A DMA grant is not completion.

Completion is not visibility.

A device-visible mapping is not proof that ownership may be returned.

---

# CXL and fabric integration

CXL is integrated through the existing authority/resource/external-operation substrate rather than through a parallel security model.

Representative decomposition:

```text
CXL.io
    -> device / MMIO / IRQ / DMA authority

CXL.mem
    -> memory placement/backing/capacity

CXL.cache
    -> coherent-access eligibility

fabric
    -> semantic topology/binding/reconfiguration evidence
```

The following remain distinct:

```text
coherence != ownership
coherence != publication
coherence != replay safety
coherence != authority
```

Resource controls may additionally cover semantic families such as:

```text
FabricBytesPerWindow
remote-memory occupancy
InflightOperations
```

but they do not expose HDM/DPA addresses, interleave topology, mailbox details, IOMMU handles, or other provider-private identifiers as application authority.

---

# Virtualization and nested domains

Virtualization is expressed as a neutral service model rather than as a VMX/VMCS-shaped native kernel ABI.

The architecture supports:

```text
child-domain lifecycle
parent/child authority derivation
guest-memory custody
virtual events
semantic traps
bounded virtual I/O
execution-artifact admission
recursive teardown
generation-safe replacement
```

A child receives a bounded subset of parent authority.

VMX/VMCS-style state may exist as a downstream compatibility projection:

```text
native neutral virtualization fact
    ->
optional VMX/legacy projection
```

not as the source of native SingNext authority.

---

# Evidence and SecureCompute

Evidence is an observation plane.

Evidence may describe:

```text
platform state
measurement
readiness
replay information
provider output
usage
completion
security feature contour
```

but evidence cannot:

```text
mint capabilities
transfer Region ownership
reserve resource capacity
settle a budget by itself
authorize resubmission
publish effects
```

SecureCompute admission combines current authority and exact current evidence without conflating them.

Stronger claims require stronger qualification.

---

# Native services

SingNextOS uses source-familiar C# APIs over typed SIP instead of making POSIX or Win32 the native substrate.

Native service families include filesystem, networking, process management, GUI/presentation, compute, virtualization, and platform/device services.

Object identity is generation- and session-bound.

Representative conceptual objects include:

```text
FileObject
SocketObject
ProcessObject
SurfaceObject
VirtualDomain
QueueObject
```

A familiar async API shape does not imply:

```text
POSIX fd
Win32 HANDLE
Linux syscall ABI
ambient process authority
CoreCLR host OS contract
```

In SingNextOS:

```text
source familiarity != binary compatibility
```

---

# Example: authority composition for networking

A network send may conceptually require:

```text
EndpointSession
+
sealed Socket object
+
Network.Send effect capability
+
Region BORROW for payload
+
NetworkTx resource-use grant
+
ResourceBudget lease
+
provider/network admission
```

The caller does not automatically receive:

```text
ambient network namespace authority
unbounded bandwidth
mutable ownership of every buffer
provider-private socket/device state
```

The same compositional pattern applies to filesystem, process, GUI, compute, DMA, and virtualization services.

---

# GUI and presentation

GUI is treated as a normal typed-service subsystem using the same authority and ownership model as the rest of SingNextOS.

Representative roles include:

```text
Display
Compositor
WindowManager
Input
Clipboard
Font/Text
Accessibility
Notification
Shell
```

Cross-application operations such as:

```text
global input
screen capture
clipboard access
foreign-window access
display configuration
```

are capability-scoped rather than ambient.

Surface presentation uses explicit ownership/read-lease semantics and release fences rather than assuming submitted frames are immediately reusable.

---

# Restart, checkpoint, and reconciliation

Restart semantics are generation-based and fail closed.

After restart or replacement:

```text
old capability       -> stale
old session          -> stale
old donation         -> stale
old sealed handle    -> stale
old provider receipt -> stale
```

Checkpoint may persist:

```text
policy
logical intent
non-authoritative descriptors
safe accounting/reconciliation information
```

but must not deserialize credential bytes directly into live authority.

Restoration requires fresh admission/re-authorization.

---

## External work across restart

In-flight ambiguous provider work may survive as reconciliation state.

Possible outcomes include:

```text
ExactNoConsume
ExactUsage(x)
WorstCaseCharge
ContainedAndClosed
Unknown
```

`Unknown` remains quarantined.

Timeout alone does not authorize:

```text
refund
Region reclaim
ownership return
resubmission
```

---

# Observability and audit

SingNextOS exposes enough correlation to explain resource and effect lifecycles without turning telemetry into authority.

A resource-consuming external operation may be traced conceptually as:

```text
capability lineage
 -> resource-use grant
 -> budget reservation/lease generation
 -> EndpointSession / invocation
 -> ExternalOperation generation
 -> provider request / provider generation
 -> usage/completion evidence
 -> local settlement
 -> visibility/publication
 -> release/reclaim
```

This chain is diagnostic evidence.

It is not a credential chain that can be replayed to recreate authority.

Telemetry must not leak live capability tokens, private Region contents, or provider-private identifiers across security boundaries.

---

# Security and concurrency model

SingNextOS treats concurrency races as part of the authority design rather than as implementation details.

Security-critical tests cover classes such as:

```text
validate vs revoke
derive vs revoke
one-shot double consume
sibling quota consumption
concurrent last-unit resource reserve
double settlement
double refund
double MOVE
borrow read vs write
session close vs invocation
session close vs async completion
cancel vs provider completion
provider generation change vs publication
process/service restart vs stale handle
pool reuse vs stale borrow
runtime restart vs persisted-token replay
resource split vs settlement
provider receipt replay
cross-operation receipt reuse
```

Every security-critical state machine is expected to document a linearization point.

---

# Performance model

The authority architecture is designed to keep expensive validation on **operation boundaries**, not ordinary element access.

The memory path is:

```text
authority/borrow validation
 -> bounded managed view
 -> normal CLR/JIT/AOT memory operations
```

not:

```text
for every LOAD/STORE:
    capability-table lookup
```

Capability and exact-generation lookup targets are expected to be `O(1)`.

Revocation ancestry may be `O(depth)` only with a small bounded delegation depth.

Caches may retain:

```text
how/where to validate
provider generation
planning evidence
```

but not:

```text
authorized = true forever
lease still valid = true
Region still owned = true
```

SipJob may reduce transport overhead while preserving authoritative transition semantics.

---

# Security TCB

For the strong managed-security contour, the trusted computing base includes at least:

```text
RuntimeKernel / privileged kernel
CapabilityAuthority
ResourceBudgetAuthority
RegionAuthority
SealedObjectAuthority
session/invocation authority
ExternalOperationAuthority
publication owners
generated SIP sentries
AdmissionVerifier
qualified .NET runtime / GC / JIT or AOT backend
PlatformAuthorityBridge
trusted runtime support
provider adapters translating already-authorized requests
```

Planner policy, scheduling heuristics, topology caches, telemetry processing, and performance prediction should remain outside the core authority TCB wherever possible.

---

# Component admission and manifests

Component manifests describe **requested intent**, not live authority.

A manifest may describe:

```text
component identity/version
image digest
provided/required contracts
platform requirements
static capability imports
resource requirements
budget requirements
security profile
dependency closure
sealed-type imports/exports
delegation policy
memory policy
authority-table quotas
runtime/AOT policy
```

Startup verifies these requirements and then creates exact runtime authority under current generations and policy.

A manifest does not authorize operations merely because it contains a declaration.

---

## Deterministic audit artifacts

Managed security qualification emits deterministic evidence that can bind:

```text
component identity/version
image digest
dependency graph and digests
native assets
security profile
static capability imports
delegation policy
memory/resource constraints
unsafe/native/reflection/dynamic findings
admission policy version/digest
SDK/runtime/AOT toolchain
provider package version/digest
provider source commit
final static verdict
```

Audit data is evidence, not authority.

---

# Claim discipline

SingNextOS deliberately avoids treating architecture, DTOs, parser support, or model execution as stronger implementation proof.

Claims are contour-specific.

Representative evidence/claim terms include:

```text
ModelOnly
StaticAdmission
RuntimeEnforced
ExecutableAdapter
EnforcedUpperBound
GuaranteedReservation
QualifiedManaged
ProductionCandidate / ProductionQualified
ProductionSecure
```

These terms are not a single automatic promotion ladder.

For example:

```text
ModelOnly
    does not imply RuntimeEnforced

RuntimeEnforced
    does not imply ExecutableAdapter

ExecutableAdapter
    does not imply EnforcedUpperBound

EnforcedUpperBound
    does not imply GuaranteedReservation

QualifiedManaged
    does not imply hardware isolation

provider execution
    does not imply ProductionSecure
```

Every enabled contour should be tied to exact:

```text
SingNextOS source commit
provider / HybridCPU source commit
contract/package version
package digest
runtime profile
feature gate
executable tests
qualification evidence
```

---

# Feature qualification

Proof does not transfer automatically between contours.

The following inference patterns are forbidden:

```text
model -> production runtime
host provider -> HybridCPU provider
one provider -> every provider
one resource family -> every resource family
JIT -> NativeAOT
fake provider -> executable hardware
parser support -> execution support
accounting -> enforcement
upper bound -> guaranteed capacity
average latency -> realtime deadline guarantee
completion -> publication
```

Unsupported features remain explicitly unavailable rather than silently degrading to a weaker security path.

---

# Repository layout

| Path                                             | Purpose                                                                                                               |
| ------------------------------------------------ | --------------------------------------------------------------------------------------------------------------------- |
| `contracts/SingPlus.Contracts/`                  | Shared capability, Region, budget, manifest, protocol, service, virtualization, GUI, and external-operation contracts |
| `src/Kernel/`                                    | Privileged kernel and boot contour                                                                                    |
| `src/Runtime/SingPlus.Runtime/`                  | RuntimeKernel and authoritative capability/Region/session/resource/external-operation subsystems                      |
| `src/Sip/SingPlus.Sip/`                          | Typed SIP service contracts                                                                                           |
| `src/Drivers/SingPlus.Drivers/`                  | Driver-facing component model and services                                                                            |
| `src/Platform/SingPlus.Platform.Abstractions/`   | Provider-neutral platform contracts                                                                                   |
| `src/Platform/SingPlus.Platform.Host/`           | Host/model provider                                                                                                   |
| `src/Platform/SingPlus.Platform.HybridCpu/`      | HybridCPU-neutral provider composition                                                                                |
| `sdk/SingPlus.Analyzers/`                        | Roslyn architecture/security analyzers                                                                                |
| `sdk/SingPlus.Generators/`                       | SIP and protocol source generators                                                                                    |
| `sdk/SingPlus.*.Sdk/`                            | Kernel/SIP SDK surfaces                                                                                               |
| `sdk/SingPlus.System/`                           | Source-facing native system library layer                                                                             |
| `tools/SingPlus.Admission/`                      | Static ManagedCap admission verifier                                                                                  |
| `tools/Runtime/HybridCPU_NeutralRuntime/`        | Neutral runtime contracts/model/authority core/tests                                                                  |
| `tools/HybridCpu_ExecutableAdapter/`             | Isolated HybridCPU executable adapter                                                                                 |
| `tools/SingPlus.HybridCpuQualification/`         | HybridCPU qualification/reproducibility tooling                                                                       |
| `tests/`                                         | Authority, Region, SIP, budget, provider, concurrency, integration, analyzers/generators, and conformance tests       |
| `docs/Completed/SingCap-Refactoring/`            | Completed SingCap-M architecture and implementation evidence                                                          |
| `docs/SingNextOS-vNext-refactoring-roadmap-new/` | vNext resource/compute architecture, invariants, qualification, and phase records                                     |
| `docs/`                                          | Whitebooks, cross-project architecture, CXL, HybridCPU, and historical design records                                 |

---

# Build requirements

The repository currently pins the .NET SDK through [`global.json`](global.json):

```text
.NET SDK 11.0.100-rc.1.26425.128
rollForward = disable
allowPrerelease = true
```

The default language version remains:

```text
C# 13
```

Preview-language qualification is opt-in through:

```text
-p:SingPlusPreviewLanguage=true
```

Builds are configured as deterministic.

Architecture analyzers and generators are enabled for the relevant Kernel, SIP, and Driver profiles.

---

## Restore, build, and test

```bash
dotnet --version

dotnet restore SingNextOS.slnx --force --no-cache

dotnet build SingNextOS.slnx \
  -c Release \
  --no-restore

dotnet test SingNextOS.slnx \
  -c Release \
  --no-restore
```

Security or production claims should rely on the exact test/qualification artifact associated with the source commit being evaluated rather than on an old README test count.

---

# Documentation

Recommended starting points:

### Current architecture

* [`README.md`](README.md) — architecture overview.
* [`docs/SingNextOS-vNext-refactoring-roadmap-new/README.md`](docs/SingNextOS-vNext-refactoring-roadmap-new/README.md) — corrected vNext resource/heterogeneous-execution architecture.
* [`docs/SingNextOS-vNext-refactoring-roadmap-new/AUTHORITY_OWNER_MAP.md`](docs/SingNextOS-vNext-refactoring-roadmap-new/AUTHORITY_OWNER_MAP.md) — authoritative state ownership.
* [`docs/SingNextOS-vNext-refactoring-roadmap-new/RESOURCE_MODEL.md`](docs/SingNextOS-vNext-refactoring-roadmap-new/RESOURCE_MODEL.md) — resource dimensions, leases, conservation, settlement.
* [`docs/SingNextOS-vNext-refactoring-roadmap-new/VNEXT_NORMATIVE_INVARIANTS.md`](docs/SingNextOS-vNext-refactoring-roadmap-new/VNEXT_NORMATIVE_INVARIANTS.md) — normative vNext invariants.
* [`docs/SingNextOS-vNext-refactoring-roadmap-new/VNEXT_FEATURE_GATES.md`](docs/SingNextOS-vNext-refactoring-roadmap-new/VNEXT_FEATURE_GATES.md) — feature qualification boundaries.

### SingCap-M

* [`docs/Completed/SingCap-Refactoring/SINGCAP_M_TECHNICAL_SPEC.md`](docs/Completed/SingCap-Refactoring/SINGCAP_M_TECHNICAL_SPEC.md) — managed capability-security architecture.
* [`docs/Completed/SingCap-Refactoring/`](docs/Completed/SingCap-Refactoring/) — implementation evidence, authority-composition protocol, phase records, and qualification material.

### HybridCPU / platform architecture

* [`docs/whitebook/hybridcpu-ise/00_README.md`](docs/whitebook/hybridcpu-ise/00_README.md) — SingNextOS/HybridCPU architecture whitebook.
* [`docs/whitebook/hybridcpu-ise/02_PHILOSOPHY_AND_AUTHORITY_ALIGNMENT.md`](docs/whitebook/hybridcpu-ise/02_PHILOSOPHY_AND_AUTHORITY_ALIGNMENT.md) — intent/authority/evidence/publication model.
* [`docs/whitebook/hybridcpu-ise/04_MEMORY_OWNERSHIP_DMA_SECURE_IO.md`](docs/whitebook/hybridcpu-ise/04_MEMORY_OWNERSHIP_DMA_SECURE_IO.md) — memory, DMA, and visibility.
* [`docs/whitebook/hybridcpu-ise/07_PLATFORM_BRIDGE_AND_EXTERNAL_CONTRACTS.md`](docs/whitebook/hybridcpu-ise/07_PLATFORM_BRIDGE_AND_EXTERNAL_CONTRACTS.md) — provider boundary.
* [`docs/external-requirements/README.md`](docs/external-requirements/README.md) — cross-project prerequisites and evidence boundaries.

Historical roadmaps remain useful for design lineage, but current source, executable tests, normative completed specifications, and current qualification artifacts outrank stale phase prose.

---

# Source-of-truth order

When documents disagree, use:

```text
live code + executable tests
    >
current normative specifications
    >
current implementation/qualification evidence
    >
current roadmap/design documents
    >
historical/vision documents
```

No README statement should be treated as stronger proof than executable evidence.

---

# Design non-goals

SingNextOS deliberately does **not** define its architecture around:

* a giant Unix/Win32-style native syscall ABI;
* ambient authority derived from names, IDs, paths, or discovery;
* a universal mutable shared-memory model;
* caller-editable capability descriptors as authority;
* a second capability or resource ledger;
* a scheduler that owns security truth;
* a provider that mints SingNext capabilities;
* provider receipts as local authority;
* replay evidence as permission to resubmit;
* completion as publication;
* CXL coherence as ownership;
* zero-copy as a semantic guarantee;
* one generic scalar for all heterogeneous resources;
* guaranteed realtime merely because budgets/periods exist;
* hardware-private lane/opcode/queue/topology state in source-facing APIs;
* VMX/VMCS as the native virtualization object model;
* CHERI-style tagged pointers, capability registers, or per-memory-access capability lookup;
* HybridCPU ISA, VLIW-format, pointer-width, or register-model changes for SingCap.

The preferred pattern is:

```text
narrow semantic contract
+
exact local authority
+
explicit generations
+
typed ownership
+
quantitative conservation
+
independent provider legality
+
explicit lifecycle
+
bounded publication/reclaim
```

---

# What SingNextOS enables

Taken together, the current architecture is intended to support several system patterns particularly well.

## Least-authority managed services

Untrusted managed components can operate through explicit typed authority without receiving ambient host filesystem, network, device, process, or global-object access.

## Resource-funded RPC

Clients may fund expensive downstream work through bounded request-scoped resource donation instead of allowing cheap requests to consume arbitrary privileged-service capacity.

## Multi-tenant heterogeneous compute

A tenant can independently be restricted by:

```text
WHAT effects it may perform
WHICH data it may access
HOW MUCH resource it may consume
WHICH provider classes are semantically eligible
HOW MANY operations may be in flight
WHICH authority may be delegated further
```

## Ownership-safe accelerator pipelines

Data can flow through:

```text
service
 -> DMA
 -> accelerator
 -> another service
 -> compositor / publication
```

while preserving explicit ownership/use/visibility/reclaim boundaries.

## Replaceable scheduling policy

Scheduling, fairness, topology selection, and performance heuristics can evolve without redefining authority semantics.

## Restart-safe external execution

Stale credentials do not resurrect after restart, and ambiguous external work can remain safely quarantined until reconciliation.

## Resource-safe service DAGs

Ordinary SIP and fused SipJob execution can share the same authoritative semantics, including resource conservation across nested and parallel branches.

---

# Architectural summary

SingNextOS can be summarized as:

```text
Capability OS
+
Ownership OS
+
Typed Service OS
+
Resource-Accountable OS
+
Provider-Neutral Heterogeneous Runtime
+
Explicit Publication/Commit OS
```

The core philosophy is:

```text
authority stays with the exact owner
evidence never becomes authority
identity never substitutes authority
ownership is never inferred from mapping
resource accounting never substitutes permission
resource permission never substitutes effect authority
provider authority never substitutes SingNext authority
compiler metadata never substitutes HybridCPU runtime legality
completion never substitutes visibility
visibility never substitutes publication
publication never substitutes release
```

For co-design:

```text
SingNextOS
    owns semantic authority,
    ownership,
    quantitative local resource truth,
    publication,
    reclaim.

HybridCPU
    owns runtime execution legality
    and architectural retire.

Providers
    own provider-specific admission
    and execution facts.

Schedulers and planners
    own policy.

None of these layers may silently impersonate another.
```

That separation is what allows SingNextOS to compose managed capability security, resource isolation, service protocols, accelerators, DMA, virtualization, CXL/fabric resources, and HybridCPU execution without turning hardware-specific state into the native OS authority model.

---

# Technical status boundary

SingNextOS remains a **systems-research operating-system/runtime architecture**, not a production general-purpose OS distribution.

The architecture is intentionally designed so that stronger backend implementations can be introduced without changing the meaning of the public authority model:

```text
host/model implementation
 -> executable software adapter
 -> external runtime/emulator
 -> device backend
 -> FPGA
 -> ASIC / production platform
```

Moving down this stack should strengthen implementation evidence.

It should not redefine:

```text
capability semantics
Region ownership
resource conservation
EndpointSession authority
ExternalOperation lifecycle
publication semantics
```

---

# License

SingNextOS is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)**.

See [`LICENSE.txt`](LICENSE.txt).
