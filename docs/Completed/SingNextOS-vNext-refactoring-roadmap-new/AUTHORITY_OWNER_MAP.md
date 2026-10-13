# Authoritative owner map for corrected vNext

| Owner | Owns | Identity/generation | Linearization | Does NOT authorize/own |
|---|---|---|---|---|
| `CapabilityAuthority` | semantic permission and monotonic capability constraints, including resource-use grant constraints | authority realm + exact subject/resource generations | existing capability ledger operations | budget counters, Region ownership, provider legality, publication |
| `ResourceBudgetAuthority` | limits, used amounts, quantitative reservation/lease/settlement/quarantine | account/reservation generation + process generation | one local ledger lock/CAS-equivalent owner protocol | effect permission, Region reclaim, provider truth, publication |
| `ProcessRegistry` | process identity/incarnation | `ProcessHandle` generation | registry owner | capability rights, budget use |
| `EndpointSessionRegistry` / invocation owner | session/protocol state and exact invocation identity | session + invocation generation | registry transition | global budget ownership, provider legality |
| `RegionAuthority` | mutable memory ownership, borrow/use, generation/mutation state, reclaim eligibility | Region/borrow/mutation generations | Region owner | effect permission, budget settlement |
| `SealedObjectAuthority` | sealed object identity/liveness | sealed object generation | sealed-object owner | unrelated capabilities/resources |
| `ExternalOperationAuthority` | external effect state and exact lifecycle correlation | operation generation | external-operation state transition | budget mint/refund by evidence, publication owner substitution |
| response/publication owners | response visibility/publication truth | response/invocation generation | publication owner | provider completion, resource settlement |
| `PlatformAuthorityBridge` | platform bindings/semantic bridge state owned by its existing sub-ledgers | binding/provider generations | bridge-local transitions | SingNext capability minting |
| `NeutralRuntime` / planners | orchestration/policy/caches | cache/config generations | non-authoritative | authority truth |
| HybridCPU ExternalRuntime/provider | provider admission, execution/completion/visibility evidence | provider/contract/generation tuples | provider/runtime state | SingNext authority/publication |
| HybridCPU legality/retire | runtime legality and architectural retire | machine/runtime state | HybridCPU runtime | SingNext effect/resource authority |

## Resource authority decomposition

There is no standalone `TemporalResourceAuthority` owner. Resource admission requires two existing owners:

```text
CapabilityAuthority: may this subject consume resource class R under envelope E?
AND
ResourceBudgetAuthority: is exact quantitative capacity reserved/leased now?
```

This is orthogonal composition, not duplicated truth: permission and conservation are different facts.

## Locking rule

No provider/user/service callback runs while a capability, budget, Region, session, or ExternalOperation authority lock is held. Cross-owner operations use prepare/revalidate/commit patterns with explicit compensation before irreversible submit.
