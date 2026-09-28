# 03. Secure Virtualized CXL Type-2 Compute

## Problem

`ComputeIntent.RequiresVirtualizedDomain` and `RequiresSecureEvidence` currently prove provider capability/readiness, not that one concrete Type-2 effect belongs to an exact guest and secure domain.

## Decision

Introduce an exact runtime context for virtualized compute, conceptually:

```text
VirtualComputeContext {
  VirtualDomainHandle;
  SecureExecutionBinding?;
  Guest input mapping;
  Guest output mapping;
  VirtualIo lease / parent DeviceLease;
}
```

RuntimeKernel resolves and validates the context. `CxlType2AcceleratorService` consumes a validated snapshot, not caller-supplied provider handles.

## Compound admission snapshot

For secure+virtualized Type-2 work capture and revalidate virtual-domain generation, guest input/output mapping generations, secure-domain/secure-execution generations, RegionUse/MutationEpoch identity, DeviceLease/VirtualIo generation, CXL endpoint generation, fabric binding ID+generation, Type-3 backing generation when applicable, CXL security-evidence generation/assurance, provider generation, and external-operation binding/generation.

## Required admission order

```text
validate compute plan
 -> validate exact virtual context
 -> validate secure context if required
 -> acquire RegionUse
 -> validate DeviceLease/VirtualIo
 -> validate CXL fabric admission
 -> validate security evidence
 -> record external submission
 -> provider effect
```

Before publication, repeat every generation/security predicate that can have changed.

## Failure semantics

- capability-bit presence alone never authorizes the effect;
- stale guest/secure context before provider effect aborts pre-submit;
- after provider effect, stale context requires exact provider closure or quarantine;
- `ProviderUnavailable` alone never permits RegionUse release;
- security evidence loss after completion but before publication prevents publication and guest event delivery;
- direct coherent output remains FutureGated.

## Required tests

- virtualized requirement without exact context is denied;
- secure requirement without exact secure binding/policy is denied;
- a different virtual domain cannot reuse the context;
- stale guest output/secure generation before publication fails closed;
- FM reconfiguration closes/contains provider work before local release;
- ambiguous submit/release keeps region/VM/secure authority pinned.
