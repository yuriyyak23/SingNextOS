# P04 — CROSS OWNER ADMISSION COMMIT PROTOCOL

**Live disposition:** closed at `RuntimeEnforced` for the internal host/JIT process + effect capability + compute-time resource grant + budget lease + Region + ExternalOperation contour on reviewed HEAD `b86cc33c16a77760c6971cc1a656c34b6cf2a10d`; `FG-VNX-CROSSOWNER-ADMISSION` remains OFF. Session/invocation and generated SIP entry are deliberately deferred to P05/P06. See `EVIDENCE_P04_CROSS_OWNER_ADMISSION.md` and `P04_QUALIFICATION_TUPLE.json`.

## Purpose

Define the transaction protocol that composes effect permission, resource-use grant, budget lease, Region/session generations and external-operation preparation without pretending these owners are one state machine.

## Preconditions

- P03 closed.
- Existing SingCap-M authority composition protocol and Region/ExternalOperation prepare paths understood.

## Architectural decisions

- Use prepare -> reversible reserve -> exact revalidation -> commit -> submit ordering.
- Define which steps are rollbackable versus compensatable versus irreversible.
- Never invoke service/provider code while owner locks are held.
- If a cross-owner atomic multi-lock is unnecessary, prefer ordered owner-local commits plus generation revalidation and a single irreversible boundary.
- Define canonical lock/order rules only for local reversible reservations to avoid deadlock.

## State / linearization model

Conceptual protocol:

```text
Prepare intent/session/Region/external-op
 -> validate effect + resource grants
 -> reserve budget lease
 -> revalidate all exact generations
 -> commit local consumptive admissions/bindings
 -> release all owner locks
 -> provider admission/submission
```

Before submit, failures unwind. After submit may have happened, compensation/quarantine replaces rollback.

## Negative-space obligations

- revoke between check and commit;
- session/process generation changes after reserve;
- Region mutation between prepare and submit;
- provider callback reentrancy;
- deadlock from lock-order inversion;
- crash after local commit but before/after submit;
- duplicate submit retry.

## Required executable tests

- Fault injection after every protocol step.
- Race: revoke/retire/session-close vs admission.
- Lock-order/deadlock stress test.
- Duplicate request/idempotency test.
- Invariant trace proving no provider call occurs while authority locks are held.

## Expected code / contract owners

- existing capability composition protocol
- generated sentry/runtime kernel admission helpers
- Region/ExternalOperation/budget owners

## Claim boundary

`RuntimeEnforced`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- Each commit point has owner and rollback/compensation semantics.
- No check-then-act security gap remains on the supported contour.

## Prerequisite for next phase

P05 generated SIP sentries must call this protocol rather than reimplementing it.
