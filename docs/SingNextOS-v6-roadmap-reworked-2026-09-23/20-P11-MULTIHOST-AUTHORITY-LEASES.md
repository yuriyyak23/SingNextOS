> Target semantics and contour exits; current implementation and open tasks: [modernize backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md). Candidate type names do not require new APIs when an existing contract suffices. Select tests/models by actual consumer and risk under [dev policy](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md). Historical status and duplicate planning text: [archive](../../artifacts/v6/iteration-20261007-documentation-context-cleanup/history/20-P11-MULTIHOST-AUTHORITY-LEASES.md).

# P11 — Multi-Host Delegated Authority Leases

## Scope

Future-gated. Only begin after single-host memory/DMA/failure/reclaim semantics close. Use monotonic epoch-bound delegation, not a distributed capability database.

### Target semantic requirements

- Lease epoch/expiry model independent of unsafe wall-clock assumptions where possible.
- Ownership transfer protocol.
- Partition/revoke/renew rules.
- Duplicate execution/ABA prevention.
- Remote reclaim/possible-effect closure.
- Persistent restart semantics with fresh authority.

### CONTRADICTIONS

- Remote lease != parent authority.
- Fabric route/topology != authority.
- Owner reboot cannot silently resurrect old leases.

### Excluded designs

- No distributed capability database.
- No global consensus for every lookup.

### Candidate contract shapes

- RemoteAuthorityLeaseV1 monotonic delegation.
- Optional quantitative resource escrow/sub-allocation.
- Explicit transfer protocol only where ownership actually moves.

### AUTHORITY IMPACT
Original logical owner remains authoritative. Remote holder owns only local lease state. ResourceBudgetAuthority conserves parent/child allocations.

### HYBRIDCPU IMPACT
Usually `SIDEBAND_CONTRACT` in provider/fabric transport; HybridCPU runtime legality remains host-local and fresh.

### COMPILER IMPACT
None required.

### ISA IMPACT
`NONE`

### SECURITY RISKS

- Lease replay/ABA after partition or reboot.
- Remote effect continues after owner assumes expiry.
- Escrow over-allocation.

### CORRECTNESS RISKS

- Clock skew invalidates expiry assumptions.
- Duplicate ownership transfer.
- Remote reclaim before effect closure.

### PERFORMANCE RISKS

- Cross-host coordination can be expensive; data plane should use cached narrow leases after safe issuance.

### Applicable behavior and qualification tests

- Partition at each lifecycle transition.
- Renew/revoke race.
- Owner/remote reboot.
- Lease ID/epoch reuse.
- Escrow conservation.
- Duplicate execution.
- Remote provider loss and ambiguous write.
- Fabric reconfiguration.

### Property-specific model options

- TLA+ lease/partition/transfer protocol.
- Alloy finite owner/lease narrowing model.

### DEPENDENCIES
Hard: P01 + P04 + P05 + P10; P02 if persistent state participates. P09 optional for secure fabric. Explicitly not on single-host critical path.

### EXIT CRITERIA

- Single logical owner is preserved under all explored partitions.
- Expired/stale leases cannot execute or publish.
- Remote effects close/quarantine before reclaim.
- Consensus use is justified only at required transfer/epoch linearization points.

## Consumer requirements

- Specify lease protocol formally before code.
- Prototype resource escrow.
- Implement only after single-host closure.

## Owner and lifecycle rules

Apply the [owner commit discipline](02-AUTHORITY-OWNER-MAP.md) and [normative invariants](04-NORMATIVE-INVARIANTS.md) to actual consumers. Proposed HybridCPU impact classifies integration needs; it does not authorize ISE internals outside the current compiler/External Tools Runtime scope.

## Feature gates and claim ceiling

**Gate(s):** `V6-MULTIHOST-LEASES`  
**Property-specific claim boundaries:** `FutureGated / ModelOnly until distributed protocol and executable multi-host environment exist`. Stronger physical/production claims require named hardware/provider evidence.

## Suggested slices (skip implemented unchanged cuts)

- P11-A formal protocol only.
- P11-B simulation/fault harness.
- P11-C narrow delegated resource pilot.
- P11-D ownership transfer optional.
- P11-E production fabric qualification much later.

## Rollback

Disable all remote leases; no fallback interprets a remote lease as local authority. Remote work must close/expire/quarantine before local reclaim.
