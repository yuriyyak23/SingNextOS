# v6 Cross-Project Contracts — SingNextOS ↔ HybridCPU-v2

## Contract direction

```text
SingNextOS semantic obligations
    -> provider-neutral request
    -> provider/HybridCPU execution guarantees
    -> pure refinement
    -> exact SemanticExecutionBinding
    -> independent CPU runtime legality
    -> execute/retire/evidence
    -> completion/visibility/persist/containment receipts
    -> SingNext publication/settlement/reclaim
```

## Required additive ABI families

Only add a family when live V1 contracts cannot encode it:

- memory guarantee/ordering/atomicity descriptors;
- temporal/preemption guarantees;
- translation/DMA correlation generations;
- durability receipts;
- trust/attestation evidence classification;
- partial failure/containment/RAS evidence;
- energy/performance envelope evidence;
- compiler semantic proof digest/reference transport.

## Forbidden ABI leakage

Do not expose as SingNext application/SIP authority:

```text
lane IDs
slot indices
raw opcodes
physical queues
CXL port/decoder/DPA/HPA identities
IOMMU internal domain handles
PASID as authority
VMCS/VMX identifiers
backend token IDs
microarchitectural cache/bank IDs
```

## Versioning

All new contracts are additive and canonical. Mandatory unknown fields/classes deny. Optional fields must have explicit semantics for absence. Package version alone is not artifact identity; qualification records digest + source SHA + contract schema.

## Independent-gate rule

A valid provider receipt never makes an invalid SingNext operation legal. A valid SingNext capability never makes an illegal HybridCPU bundle executable. A valid compiler proof never overrides either.
