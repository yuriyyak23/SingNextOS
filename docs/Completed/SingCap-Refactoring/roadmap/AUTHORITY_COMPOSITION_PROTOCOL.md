# Authority Composition Protocol

**Status:** normative design gate for P04, P06, P07, P08, P11, P12 and P13.

## Purpose

SingNextOS has one capability ledger and multiple orthogonal authoritative state machines. `CapabilityAuthority` owns permission; `ProcessRegistry` owns subject incarnation; `EndpointSessionRegistry` owns session lifecycle; the sealed-object table owns object identity/lifecycle; `RegionAuthority` owns memory ownership/use; `ExternalOperationAuthority` owns external-effect lifecycle; providers own only provider-side state/evidence.

No effect that depends on more than one owner may begin from a capability lookup or a sequence of unpinned validations alone.

## ACP-001 — one admission attempt

Every composed effect has one non-reusable admission-attempt identity. All acquired leases/pins and all generation observations are correlated to that attempt. A detached snapshot or copied DTO cannot start an effect.

## ACP-002 — prepare, pin, commit

The conceptual protocol is:

```text
resolve current subject and session
prepare exact capability operation lease
acquire exact sealed-object and/or Region-use pins
bind subject, service, session, object, Region and resource generations
revalidate every prepared participant
atomically mark the attempt Admitted in its owning lifecycle
release authority locks
call the external provider, if any
```

The implementation may use ordered locks, reservations plus version revalidation, or another bounded protocol. It must prove that a participant changing between prepare and commit causes denial/compensation, never an admission using mixed-time observations.

## ACP-003 — snapshot is evidence, pins remain authority

An internal `EffectAdmissionSnapshot` may record attempt ID, capability lease ID/lineage epoch, subject generation, session generation, sealed-object generation, RegionUse handle, Region/Borrow generation, MutationEpoch, resource generation and provider correlation/generation. These values are correlation evidence. Authority remains in the live records and exact leases/pins of their owning registries.

## ACP-004 — lock order and provider boundary

- Every participating registry documents its lock/linearization domain.
- Multi-record acquisition uses a deterministic total order or a reservation/revalidation protocol.
- No external provider call, callback, blocking wait or user code executes while an authority-registry lock is held.
- Completion callbacks re-enter through exact correlation IDs after provider code returns; they never resume with a retained monitor.

## ACP-005 — compensation

Failed prepare or final revalidation releases successfully acquired pins in strict reverse acquisition order. A failure after possible provider effect is not ordinary rollback: the exact external operation and affected Regions remain pinned or quarantined until closure/containment is proven.

## ACP-006 — revocation policy

Every effect operation class has one closed, trusted-runtime `EffectRevocationPolicy`:

```text
AdmissionOnly
CancelIfPossible
PublicationRevocable
GrandfatherAdmitted
```

The policy is static code/generated policy, never caller-provided. Its transition table states behavior for revoke before admission, after admission, after provider submission, before publication and before release. No policy may fabricate cancellation, rollback, visibility, publication, closure or reclaim.

## ACP-007 — required race proof

At minimum, the migrated pilots test capability revoke vs admission, Region MOVE/reclaim vs admission, session close vs admission, sealed close/service restart vs invocation, publication vs revoke/reclaim, provider completion vs service restart, partial prepare compensation and lock-order/deadlock stress.

## Exit gate

P04 cannot close until one real effectful path implements this protocol across every authority owner it uses. Later phases must extend that same protocol; they may not create a second coordinator that copies authority state.
