# Phase 03 implementation evidence: secure virtualized Type-2 compute

## Implemented contour

`VirtualComputeContext` is kernel-private and binds one exact virtual domain,
input and output guest mappings, bounded `VirtualIoBinding`, and an optional
exact `SecureExecutionBinding`.  `RuntimeKernel` resolves this context from its
existing domain and mapping registries; it does not create a second authority
registry or accept provider-private identities from a caller.

`CxlType2AcceleratorService.Submit` rejects a plan whose intent requires a
virtualized domain.  Such a plan must use the internal `SubmitVirtualized` path
with a context validated by the kernel.  The context is revalidated before
RegionUse admission, before the provider effect, after provider materialization,
and before completion/publication.  A live external operation pins its virtual
domain, input/output guest mappings, Virtual I/O lease, and secure execution
until exact provider closure permits the external operation to be released.

Completion and publication require the exact `CxlType2Execution` registered for
the operation and principal.  A replayed or modified execution object fails
with `StaleGeneration` before observing completion or publishing output.
Direct coherent output remains future-gated.

## Audit corrections

- ordinary `Submit` no longer accepts `RequiresVirtualizedDomain`;
- exact virtual context is revalidated after the provider call and immediately
  before publication;
- context dependencies remain pinned across ambiguous submit/cancel/release;
- forged or cross-operation execution objects cannot drive publication;
- composed Type-2 work participates in process teardown before domain roots.

## Verification

The focused phase 0-4 suite passed 102 tests.  The complete solution suite
passed 1,110 tests with two explicitly skipped opt-in suspended-child probes.
Commands are recorded in `00_04_COMPLETENESS_CORRECTNESS_AUDIT.md`.

## Boundary

This is a provider-neutral runtime composition contour.  It is not a
ProductionSecure promotion, a real production CXL Type-2 integration, a direct
coherent output implementation, or a production-security claim.

