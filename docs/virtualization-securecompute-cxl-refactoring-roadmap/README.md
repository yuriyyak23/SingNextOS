# SingNextOS Virtualization + SecureCompute + CXL Refactoring Roadmap

Status: Phases 00-05 implemented and qualified in the SingNextOS model/runtime boundary. This status is not a ProductionSecure promotion or a production CXL transport claim.

## Scope

This folder contains only SingNextOS work. HybridCPU-v2/ISE/compiler requirements remain in `../hybridcpu-cxl-refactoring-roadmap/` and are mirrored by a repository-local roadmap in `yuriyyak23/HybridCPU-v2`.

## Target architecture

```text
VirtualDomain authority
   +
SecureDomain authority
   +
OwnedRegion / DeviceLease authority
   +
CXL provider evidence/bindings
   =
exact composed admission for one effect
```

No component above becomes a replacement authority for another.

## Non-negotiable invariants

- `VirtualDomain` and `SecureDomain` remain separate authority roots and compose through exact bindings.
- CXL remains provider substrate, never app/guest authority.
- evidence != authority.
- coherence != ownership/publication.
- completion != visibility != publication.
- `ProviderUnavailable != ProviderClosed != ProviderEffectContained`.
- local reclaim requires exact provider closure or proven containment; otherwise quarantine.
- guest-visible event publication requires prior visibility and security/generation revalidation.
- CXL Type-3 backing remains ordinary `OwnedRegion` backing, not a new guest-memory type.
- CXL.io remains an ordinary platform device path under `DeviceLease`/VirtualIo.
- DirectCoherentWrite, secure multi-host writable memory, secure P2P and confidential migration remain FutureGated until their enforcement contracts exist.

## Phase index

1. `00-authority-composition-and-secure-execution-binding.md`
2. `01-secure-guest-memory-and-type3-composition.md`
3. `02-virtual-io-events-and-cxl-io.md`
4. `03-secure-virtualized-type2-compute.md`
5. `04-fault-reconfiguration-teardown-and-reclaim.md`
6. `05-validation-matrix-pr-slicing-and-exit-criteria.md`

Implementation and qualification evidence is recorded in the corresponding
`00_PHASE00_IMPLEMENTATION_EVIDENCE.md` through
`05_PHASE05_VALIDATION_EVIDENCE.md` files.  Feature promotion remains governed
by real provider contracts and independent production qualification; test
providers and evidence do not create authority or raise feature claims.

## End state

A secure virtualized CXL-backed operation must be traceable through exact local capabilities, virtual/secure generations, guest mappings, device authority, CXL binding/evidence generations, external-operation lifecycle and final guest-visible publication. Any stale or ambiguous stage fails closed without silently releasing memory or authority.
