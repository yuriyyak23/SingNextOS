> Target semantics and contour exits; current implementation and open tasks: [modernize backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md). Candidate type names do not require new APIs when an existing contract suffices. Select tests/models by actual consumer and risk under [dev policy](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md). Historical status and duplicate planning text: [archive](../../artifacts/v6/iteration-20261007-documentation-context-cleanup/history/11-P02-DURABILITY-PERSISTENT-MEMORY.md).

# P02 — Durability and Persistent-Memory Semantics

## Scope

Proceed only after P01 visibility semantics. Start with staged durable output to a named persistence provider; no generic PMem/CXL durability claim.

### Target semantic requirements

- Persistence-domain descriptor/evidence.
- Persist barrier semantics and crash ordering.
- Torn write/metadata-data ordering model.
- Recovery generation/anti-rollback rules.

### CONTRADICTIONS

- Persistent media identity cannot imply durability.
- Checkpoint cannot restore capability/session/provider authority.

### Excluded designs

- No DurableAuthority.
- Do not add persistence state into RegionAuthority beyond memory consequences/ownership.

### Candidate contract shapes

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

### Applicable behavior and qualification tests

- Crash-point matrix around write/flush/visible/publish/persist/durable.
- Torn metadata/data fault injection.
- Reboot fresh-admission test.
- Duplicate replay after crash.
- Provider loss during persist.
- Persistence-domain downgrade rejection.

### Property-specific model options

- TLA+ crash-state transition model.
- Property tests for the selected durable-publication ordering; completion/visibility/publication/persistence/durability remain separate facts, not a universal strength scale.

### DEPENDENCIES
Hard: P01 + P05 memory/visibility core. P10 soft dependency for failure consequences. Required by P11 only for persistent remote state.

### EXIT CRITERIA

- A named staged persistent provider can demonstrate crash-consistent durable publication.
- Recovery creates fresh authority/admission state.
- No generic hardware durability claim is emitted.

## Consumer requirements

- Add persistence sidecar and provider evidence.
- Add crash/recovery generation rules.
- Add staged durable publication adapter.

## Owner and lifecycle rules

Apply the [owner commit discipline](02-AUTHORITY-OWNER-MAP.md) and [normative invariants](04-NORMATIVE-INVARIANTS.md) to actual consumers. Proposed HybridCPU impact classifies integration needs; it does not authorize ISE internals outside the current compiler/External Tools Runtime scope.

## Feature gates and claim ceiling

**Gate(s):** `V6-DURABLE-OUTPUT`  
**Property-specific claim boundaries:** `ExecutableAdapter for modeled/named provider; ProductionQualified only on named hardware persistence domain`. Stronger physical/production claims require named hardware/provider evidence.

## Suggested slices (skip implemented unchanged cuts)

- P02-A schema.
- P02-B staged provider adapter.
- P02-C crash harness.
- P02-D recovery freshness.
- P02-E hardware-specific qualification later.

## Rollback

Disable durable-output gate; use previous staged non-durable or storage-specific qualified path. Never treat prior persisted evidence as fresh runtime permission.
