# v6 Authority Owner Map

## Rule

`one semantic fact -> one authoritative owner` remains the primary design constraint. v6 descriptors correlate facts; they do not merge owners.

| Fact | Authoritative owner | v6 additions | Explicit non-owner |
|---|---|---|---|
| capability/effect permission | `CapabilityAuthority` | optional IFC/declassification and resource-use constraints | compiler, provider, scheduler |
| process/service incarnation | process/service registries | exact proof/lease binding generation | durable identity, manifest |
| session/invocation protocol | session/invocation owners | temporal donation/preemption correlation | provider completion |
| memory ownership/use | `RegionAuthority` | shared-atomic use modes, damage/quarantine ranges, memory-semantic use descriptors | CXL coherence, IOMMU mapping |
| quantitative resources | `ResourceBudgetAuthority` | temporal/energy dimensions and settlement | scheduler/provider telemetry |
| external-effect lifecycle | `ExternalOperationAuthority` | persistence/RAS/translation dependencies | completion token |
| publication | current publication/response owner | durable-publication dependency when requested | provider visibility receipt |
| runtime legality | HybridCPU `IRuntimeLegalityService` / GuardPlane | validated compiler-proof evidence as an input only | compiler certificate, SingNext capability |
| provider admission/resources | provider/runtime | memory/temporal/translation/trust/RAS guarantees | SingNext planner |
| translation state | platform/IOMMU provider owner | exact mapping/process/PASID-style generation | VA/IOVA/PASID values |
| durability state | storage/persistence provider + SingNext durable-state lifecycle | persist/confirm receipts | publication state alone |
| IFC labels/policy | SingNext security policy owner | labels, transforms, declassification rules | data bytes themselves |
| locality/topology observation | provider/platform evidence owner | semantic cost projection | app authority ABI |
| attestation/measurement | attestation producer | freshness/assurance classification | capability authority |
| RAS/health | provider/platform evidence owner; SingNext owns consequences | damage map, degraded-state projection | raw health event as ownership mutation |
| remote delegated authority | original logical owner | epoch-bound delegated lease | remote fabric route |
| energy/thermal truth | budget ledger for local committed budget; provider for enforceable machine envelope | new resource dimensions | planner telemetry |
| compiler semantic proof | compiler/toolchain evidence producer | `CompilerSemanticProofV1` | runtime legality / authority |

## Forbidden owner inversions

The following designs are rejected:

```text
CXL coherence -> Region ownership
IOMMU/PASID -> capability
attestation token -> execution permission
compiler proof -> legality authority
energy telemetry -> budget refund authority
provider completion -> publication
persistent bytes -> restored live capability
remote lease cache -> owner replacement
scheduler placement -> provider admission
```

## Cross-owner commit rule

Any operation depending on multiple mutable owners MUST continue to use the existing prepare/pin/revalidate/commit discipline. v6 may add participants, but it must not add a second coordinator that snapshots owner state and later treats the snapshot as permission.
