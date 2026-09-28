# Corrected Authority-Owner Map

The map is reconstructed from live architecture surfaces and previous completed architecture contracts. Descriptor/evidence producers are not promoted to owners.

| Logical fact | Authoritative owner | Generation/epoch | Mutation / linearization point | Evidence producers | Forbidden substitute owners |
|---|---|---|---|---|---|
| capability/effect permission | `CapabilityAuthority` | capability/derivation/revocation generation | capability grant/derive/revoke commit | sentries, audit logs | provider receipt, compiler proof, device identity |
| process/service incarnation | `ProcessRegistry` / service incarnation owner | process/service generation | create/replace/terminate registration commit | boot/runtime registry events | PID value, PASID, endpoint name |
| endpoint session lifecycle | `EndpointSessionRegistry` | session generation | open/close/rotate commit | transport events | connection ID, provider session |
| invocation lifecycle | invocation owner | invocation generation | create/terminal transition | IPC trace | completion receipt alone |
| Region ownership/use | `RegionAuthority` | Region generation / `MutationEpoch` / use generation | allocate/borrow/transfer/reclaim/subrange state commit | provider memory evidence | mapping, coherence, physical address |
| sealed object state | `SealedObjectAuthority` | seal/object generation | seal/unseal/rotate commit | crypto/provider evidence | ciphertext blob, measurement |
| external operation lifecycle | `ExternalOperationAuthority` | operation generation | prepare/admit/submit/terminal/quarantine transition | provider receipts | provider correlation ID |
| publication / response truth | publication/response owner | response/publication generation | publish/commit response transition | visibility/completion evidence | completion, visibility, retire |
| quantitative resource accounting | `ResourceBudgetAuthority` | budget/lease generation | reserve/bind/consume/settle/release | usage measurement | scheduler, capability ledger, telemetry |
| scheduler choice | scheduler policy component | policy snapshot only, not authority generation | scheduling selection | cost/telemetry | permission, budget truth, legality |
| provider admission | concrete provider/runtime admission subsystem | provider/session/manifest generation | provider-local admit/submit commit | adapter receipts | SingNext capability, compiler proof |
| HybridCPU machine legality | `IRuntimeLegalityService` / GuardPlane path | runtime/guard/certificate generation as applicable | legality decision for current machine state | SafetyVerifier, certificates | SingNext capability, compiler metadata |
| machine replay state | HybridCPU runtime replay machinery | replay phase/certificate generation | replay-state transition/retire boundary | replay certificates | OS permission |
| semantic external replay/dedup | external operation/invocation lifecycle owner | operation/invocation generation | dedup/terminal transition | request correlation | HybridCPU replay state |
| compiler evidence | compiler/toolchain produces; verifier accepts/caches evidence | compiler contract/schema/digest | proof validation/cache insertion | compiler | never an authority owner |
| virtual domain state | VirtualDomain runtime/registry | domain generation | create/reconfigure/destroy | VMX projection evidence | VMCS field identity, topology |
| secure domain state | SecureCompute domain owner | secure-domain/policy generation | create/reconfigure/destroy | measurement/attestation | attestation token, VirtualDomain owner |
| device lease / assignment | platform/device owner | device lease/reset generation | assign/revoke/reset transition | device/provider events | device ID, attestation |
| address-space incarnation | process/VM address-space owner | address-space generation | map-space replace/destroy transition | page-table/IOMMU adapter | VA/PASID |
| IOMMU/translation mapping | platform/IOMMU provider owner | translation generation | map/unmap/invalidate completion | IOMMU/provider evidence | RegionAuthority, IOVA, PASID |
| CXL/provider topology state | platform/provider adapter | provider/fabric generation | reconfigure/reset/disconnect | topology/health events | app/capability ABI |
| boot evidence | boot/loader evidence owner | boot measurement/generation | handoff record commit | firmware/loader | runtime capability/Region authority |
| remote delegated lease | original logical owner issues; remote holder owns local lease state only | owner epoch + lease epoch | issue/renew/revoke/expiry transition | fabric transport | route/topology, remote cache |
| energy/thermal measurement | provider/runtime measurement subsystem | measurement/provider generation | measurement sample/limit transition | counters/firmware | budget ledger unless committed budget fact |

## Cross-owner commit rule

Any operation depending on multiple mutable owners uses the established discipline:

```text
read/prepare immutable intent
-> obtain/pin narrow owner leases where available
-> provider-local preparation/admission
-> final revalidation of every unpinned mutable generation
-> single existing submit/commit linearization point
-> provider call outside unrelated owner locks
-> record effect-possible state
-> close visibility/publication/settlement/reclaim in owner order
```

There is no global runtime `GlobalStateManager` and no second coordinator ledger.
