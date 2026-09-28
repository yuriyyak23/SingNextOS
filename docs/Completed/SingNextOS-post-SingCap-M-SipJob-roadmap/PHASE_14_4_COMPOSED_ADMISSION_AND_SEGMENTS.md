# P14-4 — Composed Admission, Reversible Preparation, and Segment Commit

## 1. Goal

Compose multi-stage/multi-session admission using existing authority owners without treating consumptive authority acquisition as reversible and without creating a Job-level transaction manager.

This phase is the normative home of:

```text
prepare
pin/reserve
final revalidate
commit
execute outside locks
settle/release
```

## 2. Why segmentation is required

A Job may contain operations with different observability and irreversibility. A single global "admit whole Job" step is unsafe because:

- one-shot/quota authority may be consumed during acquisition;
- private service state may mutate;
- Region ownership may already transfer;
- external operations may become irreversible;
- session/service state may change while later stages are still pending.

Therefore admission is segmented and aligned with ordinary SIP semantic boundaries.

## 3. Participant classification

Each authority participant must declare one of these existing-owner semantics:

### 3.1 `ProbeOnly`

Non-authoritative lookup/precheck. It may fail, but success gives no execution permission.

Examples: manifest lookup, descriptor compatibility, locating an opaque owner reference.

### 3.2 `ReversiblePrepare`

Owner-defined prepare/pin/reservation that can be released before commit without consuming the protected right/effect.

Examples are allowed only where the existing owner explicitly provides those semantics.

### 3.3 `CommitConsumptive`

A linearization that consumes or irreversibly changes authority/state.

Examples include an operation-authority acquisition that consumes quota/one-shot state during acquisition.

Disposal of a returned lease is cleanup, not semantic compensation unless the owner explicitly specifies compensation.

### 3.4 `PostCommitSettlement`

Owner-defined settlement after execution: Region/output ownership transfer, publication, external-operation completion/visibility/release, etc.

## 4. Corrected segment protocol

For each segment/stage boundary:

```text
A. Resolve
   - resolve process/service/session/authority references
   - no permission inferred from lookup

B. Prepare reversible participants
   - session pins/reservations where existing semantics support them
   - seal pins/reservations where reversible
   - Region uses/reservations where appropriate and reversible
   - external-operation prepare state where the owner explicitly supports it

C. Final live revalidation
   - exact process/service incarnation
   - session generation/state
   - all prepared pins still valid
   - protocol pre-state
   - Region/seal/provider generations required before commit

D. Commit consumptive participants
   - acquire/commit operation authority as late as safely possible
   - commit other non-compensatable participants only when their owner protocol makes the sequence safe

E. Release owner locks

F. Execute generated service/provider sentry

G. Settle
   - output validation
   - Region transitions
   - protocol transition(s) at their required linearization point
   - invocation/cancellation outcome
   - publication/visibility

H. Release
   - release remaining pins/leases in reverse dependency order
   - compensate only operations whose owner explicitly defines reversible compensation
```

No service/provider/user code runs during A-E while an authority-owner lock is held.

## 5. OperationAuthorityLease rule

If the current `CapabilityAuthority` operation-acquisition path consumes quota or marks one-shot authority consumed at acquisition, P14 treats that acquisition as **CommitConsumptive**.

Consequences:

1. It MUST NOT be acquired merely to "prepare" a stage that may never run.
2. A later failure MUST NOT pretend that disposing the lease restored quota/one-shot state.
3. The operation should be acquired immediately before the stage is committed to enter its generated sentry, after all safely reversible preconditions have been prepared/revalidated.
4. An unreached later stage does not consume its operation authority merely because an earlier Job segment started.

## 6. Multi-noncompensatable rule

A fused segment MUST NOT require an unsafe chain of independent fallible non-compensatable commits.

If the segment needs:

```text
Commit A (irreversible/fallible)
then
Commit B (independent, irreversible/fallible)
```

and there is no existing owner-supported prepare/reservation that guarantees B can commit once A commits, then fusion across that boundary is not admissible.

Required action:

```text
insert FusionBarrier / materialize ordinary SIP
or
narrow the segment so each irreversible commit aligns with an ordinary semantic boundary
```

Do not add a Job-local two-phase-commit ledger.

## 7. Session close vs admission

Session semantics are preserved exactly.

Required race cases:

```text
close before pin
close after pin
close after final revalidation but before operation commit
close concurrent with implementation entry
```

The allowed outcome set must match existing session pin/drain/generation semantics. A Job may not keep executing merely because it cached `SessionActive=true`.

## 8. Revoke vs admission

Capability revoke races are resolved by `CapabilityAuthority` at its existing linearization point.

Required outcomes:

- revoke wins before commit → operation denied and not consumed;
- operation commit wins under existing rules → resulting lease/state follows existing revocation semantics;
- no Job-level historical authorization bypass.

## 9. One-shot/quota semantics

### One-shot

Two concurrent Jobs using the same one-shot authority must have exactly one successful commit where ordinary semantics permit only one use.

The losing Job must not enter service code.

### Quota

A stage that never reaches `CommitConsumptive` must not consume quota due solely to Job preflight.

A stage that commits authority and then faults in service has consumed quota according to the authority owner's ordinary semantics; the Job must not refund it unless the owner explicitly supports refund.

## 10. Seal close and service restart

Prepared seal/session/process identities are revalidated before consumptive commit when their owner semantics require it.

If service incarnation changes:

- stale binding cannot be used;
- plan remains non-authoritative and may rebind only through P14-6 rules;
- already committed earlier-stage private state remains committed; Job is not rolled back.

## 11. Region MOVE/reclaim vs admission

Region transitions follow P14-3.

A MOVE that is a post-service settlement is not pre-committed merely to make later Job execution convenient.

If a required Region transition fails after earlier private service mutation, the Job reports the ordinary partial-failure semantics. It does not roll service state back.

## 12. Lock ordering and deadlock rule

The Job MUST NOT acquire multiple owner-internal locks and hold them while calling into another owner unless the current normative authority-composition protocol explicitly permits that ordering.

Preferred pattern:

```text
owner-local atomic operation
release owner lock
store opaque pin/lease
move to next owner
```

Final revalidation/commit is performed with owner-local atomic APIs rather than by externally holding a lock set.

No provider/user code, callbacks, `Equals`/`GetHashCode` on user-defined objects, reflection hooks, or async continuation runs under authority locks.

## 13. Reverse compensation semantics

Reverse order applies only to reversible resources successfully prepared but not committed:

```text
prepared Region use/reservation
prepared seal pin
prepared session pin
...
```

For each participant the roadmap/runtime must name the owner-defined operation:

```text
Prepare
Commit (if any)
ReleaseBeforeCommit
ReleaseAfterCommit
```

If `ReleaseBeforeCommit` cannot restore semantics, the participant is not reversible.

## 14. Segment boundaries

A segment ends before any boundary where later failure must not be represented as if earlier irreversible state were rollback-able.

Typical boundaries:

- irreversible private service mutation when continuing would imply false transaction semantics;
- external provider submission;
- externally observable invocation/publication;
- required ownership settlement;
- unsupported non-compensatable commit composition;
- cross-runtime boundary;
- NativeIsolated/confidential boundary.

## 15. Executable proof set

### Partial prepare failure

Prepare session + seal + Region use; force the last reversible participant to fail. Assert all earlier reversible state is released and capability quota/one-shot is unchanged.

### One-shot race

Two Jobs commit same one-shot authority concurrently. Exactly one enters generated sentry.

### Quota failure after prepare

Make capability quota unavailable after reversible prepares but before commit. Assert no service code, all reversible resources released.

### Session close race

Inject close at every preparation/commit hook. Compare outcome set with ordinary SIP.

### Unsafe dual-commit negative test

Construct a stage/segment requiring two unsupported independent non-compensatable commits. Planner/verifier must insert barrier/reject fusion rather than attempt pseudo-transactional admission.

### No code under lock

Instrument authority owners and assert service/provider callbacks never occur while owner lock is held.

## 16. PR decomposition

1. participant classification + segment descriptor model;
2. reversible prepare/release implementation using existing owner APIs;
3. just-in-time consumptive capability commit;
4. deterministic race hooks/tests;
5. unsafe multi-commit barrier rule;
6. multi-session enablement only after all above qualify.

## 17. Exit criteria

`FG-MULTI-SESSION-SEGMENT` remains OFF until:

- reversible vs consumptive semantics are explicit per participant;
- one-shot/quota tests prove no premature consumption;
- session/revoke/seal/restart/Region races fail closed;
- no fabricated compensation exists;
- no authority locks cover service/provider execution;
- unsupported multi-commit compositions materialize/fallback.
