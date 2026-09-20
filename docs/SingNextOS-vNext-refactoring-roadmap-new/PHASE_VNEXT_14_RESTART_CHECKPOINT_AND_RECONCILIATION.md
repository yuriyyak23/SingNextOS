# P14 — RESTART CHECKPOINT AND RECONCILIATION

## Purpose

Make authority/resource/external-operation state safe across process, session, runtime and provider restart without resurrection, duplicate refund or unsafe reclaim.

## Preconditions

- P13 resource families classified.

## Architectural decisions

- Checkpoint may persist policy/accounting intent and non-authoritative descriptors, not reusable live capability/lease credentials.
- Runtime/service restart advances relevant realm/incarnation/generation and makes old grants/donations stale unless privileged reauthorization creates new ones.
- Provider restart invalidates old provider-generation receipts/bindings for new settlement.
- In-flight ambiguous operations remain quarantined until exact reconciliation or conservative charge/containment.
- Automatic refund/reclaim requires authoritative proof, not timeout.

## State / linearization model

Reconciliation outcomes:

```text
ExactNoConsume
ExactUsage(x)
WorstCaseCharge
ContainedAndClosed
Unknown -> Quarantined
```

Restore never maps persisted credential bytes directly to Active authority.

## Negative-space obligations

- process ID/generation ABA;
- session numeric ID reuse;
- provider generation reuse;
- checkpoint contains stale handle;
- crash between settlement and release;
- replay of old receipt after restore;
- double refund after restart.

## Required executable tests

- Restart/ABA fuzz across all identities.
- Checkpoint round-trip proving authority requires fresh admission.
- Crash-after-each-transition harness.
- Provider restart + late old receipt rejection.
- Unknown consumption remains quarantined across restart.
- No automatic Region reclaim from budget settlement.

## Expected code / contract owners

- authority realms/registries
- checkpoint subsystem
- ExternalOperationAuthority
- ResourceBudgetAuthority
- provider generation owners

## Claim boundary

`RuntimeEnforced`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- No authority resurrection.
- Every in-flight state has terminal/reconciliation policy.

## Prerequisite for next phase

P15 may observe these states but cannot drive them.
