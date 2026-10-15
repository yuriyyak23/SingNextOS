> Target plan and qualification exits; current implementation queue is [modernize state](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md). Use [dev validation](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md) for implementation changes; this document does not require a full campaign per iteration.

# First Qualification Vertical — Staged Heterogeneous Operation

## Purpose

The first vertical qualifies one staged single-host executable lifecycle that exercises cross-project contracts. Its evidence applies to that contour; it does not prove the whole architecture.

## Included

```text
single host
ordinary SIP/reference semantic oracle
one invocation/session
one read-only input RegionUse
one exclusive staged output RegionUse
one ResourceBudgetAuthority lease
one external/heterogeneous operation
provider-local admission
V1 base semantic contracts + additive sidecars
minimal memory semantics
minimal DMA/generation binding if the selected provider needs it
P05 refinement check
HybridCPU runtime legality
execution + retire/device completion
visibility evidence
SingNext publication
budget settlement
Region release/reclaim
```

Preferred executable targets are the already concrete MatrixTile / lane6 DmaStreamCompute / lane7 external-runtime seams, selecting whichever has the clearest end-to-end executable adapter at implementation time. The vertical is provider-specific even though the contract is provider-neutral.

## Explicitly excluded

```text
shared mutable/atomic Region
zero-copy/SVA unless required by the selected adapter
persistence/durability
hard deadline/minimum-service claim
stateful preemption/resume
generic IFC
device production attestation
partial hardware RAS
multi-host
energy guarantee
PCL requirement
new ISA
```

## Required transition chain

```text
App/SIP
 -> capability/session/Region admission
 -> budget reserve
 -> OperationObligationsV1 + sidecars
 -> planner selection
 -> provider admission
 -> ExecutionGuaranteesV1 + sidecars
 -> refinement
 -> SemanticExecutionBindingV1 + extension digest
 -> HybridCPU legality
 -> submit
 -> execute/retire or device-complete
 -> visible
 -> publish
 -> settle
 -> release/reclaim
```

For each applicable boundary identify the following once in the contour contract/evidence; do not duplicate the same fields in every iteration report:

```text
owner
input evidence
generation vector
failure state
linearization point
rollback/compensation rule
```

## Negative scenarios that must pass

- capability revoked before final submit;
- session closed before submit;
- Region mutation generation changes before submit;
- budget lease stale;
- provider generation changes;
- provider admits but HybridCPU legality denies;
- refinement fails one mandatory memory clause;
- duplicate submit attempt;
- cancel after possible effect;
- completion arrives before visibility evidence;
- visibility arrives after publication attempt;
- provider loss after possible write;
- stale translation generation if DMA binding is used.

## Differential oracle

Run the same semantic workload through ordinary/reference SIP and the heterogeneous contour. Project both traces to the common semantic trace. The heterogeneous trace must be allowed by the same obligations and must not add unauthorized security-relevant transitions.

## Claim ceiling

- SingNext owner transitions: `RuntimeEnforced` for the tested local contour.
- Cross-project adapter: `ExecutableAdapter` for the exact package/runtime tuple.
- Formal model: `ModelOnly` + executable trace/refinement evidence.
- Real hardware ordering/DMA/persistence/security: **not claimed**.

## Exit criteria

The selected lifecycle must reach actual provider closure/release, budget settlement and owner reclaim through exact consumers, including applicable failure paths. A missing artifact release or original-invocation settlement contract leaves that exit open and pins retained. If this contour requires duplicate ownership, bypassed legality or stale snapshots as permission, correct the design before qualification. A passing QV1 qualifies this contour only; it does not close every phase or authorize global gate enablement.
