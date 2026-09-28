# P11 — Multi-Host Delegated Authority Leases

## Structured verdict

**PHASE:** P11 — Multi-Host Delegated Authority Leases  
**BASELINE:** Current architecture is fundamentally single-host for authority ownership; prior CXL/fabric work provides intent/evidence but not a production distributed authority protocol.  
**VERDICT:** Future-gated. Only begin after single-host memory/DMA/failure/reclaim semantics close. Use monotonic epoch-bound delegation, not a distributed capability database.

### VERIFIED_EXISTING
- One-owner capability/Region/resource semantics provide the correct source model.
- Resource sub-allocation/escrow concepts can preserve conservation without duplicated counters.

### PARTIAL
- No production lease/partition/ownership-transfer protocol is proven across hosts.

### GAPS
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

### REMOVE_OR_MERGE
- No distributed capability database.
- No global consensus for every lookup.

### NEW_REQUIRED
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

### REQUIRED TESTS
- Partition at each lifecycle transition.
- Renew/revoke race.
- Owner/remote reboot.
- Lease ID/epoch reuse.
- Escrow conservation.
- Duplicate execution.
- Remote provider loss and ambiguous write.
- Fabric reconfiguration.

### FORMAL WORK
- TLA+ lease/partition/transfer protocol.
- Alloy finite owner/lease narrowing model.

### DEPENDENCIES
Hard: P01 + P04 + P05 + P10; P02 if persistent state participates. P09 optional for secure fabric. Explicitly not on single-host critical path.

### EXIT CRITERIA
- Single logical owner is preserved under all explored partitions.
- Expired/stale leases cannot execute or publish.
- Remote effects close/quarantine before reclaim.
- Consensus use is justified only at required transfer/epoch linearization points.

### ROADMAP PATCH
Move P11 to last-wave FutureGated work. Architecture may define the lease shape now, but implementation/claims remain disabled.

## Objective

Future-gated. Only begin after single-host memory/DMA/failure/reclaim semantics close. Use monotonic epoch-bound delegation, not a distributed capability database.

## Live baseline and existing mechanisms
- Capability/Region/Resource owners.
- Provider/fabric generations.
- Completed CXL architecture principles.

## Required implementation changes
- Specify lease protocol formally before code.
- Prototype resource escrow.
- Implement only after single-host closure.

## Owner and lifecycle rules

The phase SHALL use existing owners and SHALL name every mutable generation used by a decision. Final revalidation is immediately before the existing owner/provider commit or submit linearization point for dependencies not pinned by a valid lease. Provider callbacks execute outside unrelated authority locks.

## Feature gates and claim ceiling

**Gate(s):** `V6-MULTIHOST-LEASES`  
**Claim ceiling at emulator/runtime freeze:** `FutureGated / ModelOnly until distributed protocol and executable multi-host environment exist`. Stronger physical/production claims require named hardware/provider evidence.

## PR slices
- P11-A formal protocol only.
- P11-B simulation/fault harness.
- P11-C narrow delegated resource pilot.
- P11-D ownership transfer optional.
- P11-E production fabric qualification much later.

## Rollback

Disable all remote leases; no fallback interprets a remote lease as local authority. Remote work must close/expire/quarantine before local reclaim.
