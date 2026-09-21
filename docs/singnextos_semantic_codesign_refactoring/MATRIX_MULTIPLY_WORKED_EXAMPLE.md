# Worked Example — MatrixMultiply staged contour

## Semantic obligations

```text
Effect = MatrixMultiply
A = ReadOnly
B = ReadOnly
C = StagedOutput
ComputeTime <= X
DeviceMemory <= Y
PreemptionBound <= P
Isolation >= ExecutionContextIsolation
Publication = StagedUntilPermit/Decision
Replay = NonReplayableAfterCommit
```

`MatrixMultiply` is NEW_PROPOSED in SingNext's general semantic operation vocabulary; current `ComputeOperationKind` has only Copy/Transform/Reduce.

## End-to-end chain

| Step | Owner | Input / generation | Linearization | Failure behavior | Evidence is NOT |
|---|---|---|---|---|---|
| App/SIP intent | caller/SIP | invocation generation | invocation owner accepts | stale invocation rejects | authority beyond invocation |
| Effect admission | CapabilityAuthority | exact cap/subject/resource generation | operation-authority lease | revoke wins before commit | resource reservation |
| Region A/B/C use | RegionAuthority | exact Region/range/mutation generations | acquire use/pin | ABA/mutation -> stale | effect permission |
| resource use + lease | CapabilityAuthority + ResourceBudgetAuthority | compute/device-memory envelopes | exact vector reservation | no capacity -> reject | semantic effect authority |
| compute plan | planner | semantic candidates/provider gen | none; policy only | replan | authority |
| obligations snapshot | no new owner | exact owner snapshots | immutable snapshot creation | stale at final sentry | capability |
| provider admission | provider | semantic request/provider generation | provider receipt | deny/unavailable/stale | SingNext authority |
| guarantees | provider/runtime | execution class/version | exact guarantee observation | unsupported dimension -> mismatch | permission |
| semantic binding | ExternalOperation coordination | exact op/provider/contract/resource/visibility ids | bind-once | generation drift -> stale | authority |
| HybridCPU legality | HybridCPU runtime | machine/runtime state | runtime legality decision | typed reject | OS policy |
| submit | existing OS/resource/external-op protocol | all exact current gates | single submit-start winner | compensation only before winner | publication |
| execute/replay/retire | HybridCPU runtime | runtime state | architectural retire per CPU semantics | replay obeys runtime legality | OS release |
| usage evidence | provider/runtime | exact binding/provider generation | receipt sequence | malformed/over-envelope -> quarantine | budget truth |
| complete | provider/external op | exact binding | DeviceComplete transition | duplicate/reorder rejected | visibility |
| visible | provider + OS lifecycle | exact visibility contract | Visible transition | failure blocks publish | ownership/publication |
| OS publish decision | SingNext publication owner | exact operation/Region generations | grant decision | revoke/mutation may suppress staged output | execution permission |
| provider publication fence/action | provider | exact staged request/evidence | provider-specific publish | failure -> discard/quarantine | OS authority |
| settlement/release | budget/Region/external owners | exact lease/use/binding | each owner terminal transition | independent recovery | each other's authority |

## Level-3 claim
Only claim Level 3 after the provider proves: exact generation binding, runtime legality, enforced compute/device-memory envelope, bounded preemption if required, staged visibility, withheld publication until OS decision, and containment/recovery behavior. Otherwise the MatrixMultiply contour stays Level 2 or is rejected for stronger obligations.
