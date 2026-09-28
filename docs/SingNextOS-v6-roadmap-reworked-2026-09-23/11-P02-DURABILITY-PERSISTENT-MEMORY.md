# P02 — Durability and Persistent-Memory Semantics

## Structured verdict

**PHASE:** P02 — Durability and Persistent-Memory Semantics  
**BASELINE:** Publication/external operation lifecycle exists; generic persisted/durable semantics across real persistence domains are not qualified.  
**VERDICT:** Proceed only after P01 visibility semantics. Start with staged durable output to a named persistence provider; no generic PMem/CXL durability claim.

### VERIFIED_EXISTING
- Completion, visibility and publication are separate existing lifecycle concepts.
- Direct boot architecture already requires fresh authority after reboot.
- Provider-specific storage/checkpoint mechanisms can own durable format/integrity without becoming Region authority.

### PARTIAL
- Flush/persist concepts may exist in models, but real ADR/eADR/CXL persistence domains are not generically proven.
- Checkpoint/recovery evidence exists for operability contours but must not serialize ephemeral authority.

### GAPS
- Persistence-domain descriptor/evidence.
- Persist barrier semantics and crash ordering.
- Torn write/metadata-data ordering model.
- Recovery generation/anti-rollback rules.

### CONTRADICTIONS
- Persistent media identity cannot imply durability.
- Checkpoint cannot restore capability/session/provider authority.

### REMOVE_OR_MERGE
- No DurableAuthority.
- Do not add persistence state into RegionAuthority beyond memory consequences/ownership.

### NEW_REQUIRED
- PersistenceSemanticsV1 sidecar.
- ProviderPersistEvidenceV1 bound to provider/media generation.
- RecoveryFreshnessRecord for non-authoritative recovery correlation.

### AUTHORITY IMPACT
Durable format/integrity belongs to storage/checkpoint subsystem; RegionAuthority continues ownership; publication owner continues published truth.

### HYBRIDCPU IMPACT
`SIDEBAND_CONTRACT` only for named persist/flush/fence evidence where current runtime cannot expose it.

### COMPILER IMPACT
No required compiler change; optional ordering evidence later.

### ISA IMPACT
`NONE`

### SECURITY RISKS
- Crash recovery resurrects stale authority or provider generations.
- Forged durability receipt used as publication permission.

### CORRECTNESS RISKS
- Write data durable but metadata torn, or vice versa.
- Published before persistence when durable publication was required.
- Provider disappears after completion but before durable confirmation.

### PERFORMANCE RISKS
- Persist barriers/copies can dominate latency; staged contour provides measurable baseline.

### REQUIRED TESTS
- Crash-point matrix around write/flush/visible/publish/persist/durable.
- Torn metadata/data fault injection.
- Reboot fresh-admission test.
- Duplicate replay after crash.
- Provider loss during persist.
- Persistence-domain downgrade rejection.

### FORMAL WORK
- TLA+ crash-state transition model.
- Property tests for monotonic Complete<Visible<Published<Persisted<Durable claims.

### DEPENDENCIES
Hard: P01 + P05 memory/visibility core. P10 soft dependency for failure consequences. Required by P11 only for persistent remote state.

### EXIT CRITERIA
- A named staged persistent provider can demonstrate crash-consistent durable publication.
- Recovery creates fresh authority/admission state.
- No generic hardware durability claim is emitted.

### ROADMAP PATCH
Limit P02 to provider-qualified durability chains. Keep physical persistence assumptions explicit and tuple-bound.

## Objective

Proceed only after P01 visibility semantics. Start with staged durable output to a named persistence provider; no generic PMem/CXL durability claim.

## Live baseline and existing mechanisms
- Publication/response owner.
- Checkpoint/storage subsystem.
- RegionAuthority for ownership only.

## Required implementation changes
- Add persistence sidecar and provider evidence.
- Add crash/recovery generation rules.
- Add staged durable publication adapter.

## Owner and lifecycle rules

The phase SHALL use existing owners and SHALL name every mutable generation used by a decision. Final revalidation is immediately before the existing owner/provider commit or submit linearization point for dependencies not pinned by a valid lease. Provider callbacks execute outside unrelated authority locks.

## Feature gates and claim ceiling

**Gate(s):** `V6-DURABLE-OUTPUT`  
**Claim ceiling at emulator/runtime freeze:** `ExecutableAdapter for modeled/named provider; ProductionQualified only on named hardware persistence domain`. Stronger physical/production claims require named hardware/provider evidence.

## PR slices
- P02-A schema.
- P02-B staged provider adapter.
- P02-C crash harness.
- P02-D recovery freshness.
- P02-E hardware-specific qualification later.

## Rollback

Disable durable-output gate; use previous staged non-durable or storage-specific qualified path. Never treat prior persisted evidence as fresh runtime permission.
