# Phase 2 — Legality, GuardPlane, and External Authority

## Goal

Integrate SingNextOS admission into HybridCPU-v2 without turning CXL or OS provider state into a new CPU scheduling legality authority.

## Current seam

HybridCPU already distinguishes legality provenance with `LegalityAuthoritySource` and uses GuardPlane, replay-phase certificates, structural certificates, detailed compatibility checks, and admission metadata. Preserve that model.

## Refactoring direction

Add an external-operation guard/binding layer adjacent to, but not inside, the meaning of CPU scheduling legality:

```text
CPU structural/scheduling legality
        -> LegalityDecision
        -> descriptor/runtime GuardPlane checks
        -> ExternalRuntime admission request
        -> SingNextOS admission receipt
        -> submit eligibility
```

A CPU-side `LegalityDecision.Allow(...)` never means SingNextOS has admitted the external operation.

## LegalityAuthoritySource policy

Do not add values such as:

- `CxlFabric`;
- `CxlMemory`;
- `SingNextOs`;
- `ExternalProvider`.

Those are not sources of CPU bundle/scheduling legality.

If runtime diagnostics need to explain external rejection, add a separate external-operation rejection taxonomy, for example:

```text
ExternalAdmissionRejectKind
ExternalBindingRejectKind
ExternalPublicationRejectKind
```

Keep it separate from `RejectKind` unless a specific CPU legality invariant is actually violated.

## Guard-before-reuse

Any cached descriptor validation, capability query, replay-qualified witness, or provider binding must be invalidated/rechecked when its owner/domain or SingNextOS generation context changes.

Before a reused external-operation binding can produce another hardware effect, require:

- current owner/domain GuardPlane success;
- descriptor identity match;
- operation-generation validity;
- current SingNextOS admission/binding validity;
- replay policy allowing re-submission.

## Descriptor guards

For L7-SDC and DSC descriptors, split checks into:

1. structural/encoding validity;
2. CPU owner/domain authority;
3. semantic region/access intent;
4. OS/provider admission;
5. submit-time generation validity.

No layer may infer a later layer's success from an earlier one.

## Failure behavior

- OS rejection before submit: no hardware effect; token transitions to rejected/faulted state according to existing runtime model.
- stale receipt before submit: fail closed and require re-admission.
- stale receipt after submit: allow only lifecycle actions required to safely complete/cancel/drain, never fresh publication from stale authority.
- CPU GuardPlane failure always blocks reuse regardless of cached OS admission.

## Tests

Add cases where:

- CPU legality allows but OS admission rejects;
- OS admission succeeds but owner/domain changes before submit;
- replay certificate matches but OS generation is stale;
- structural certificate matches but external binding is revoked;
- a stale external receipt cannot be converted into `LegalityDecision.Allow`.

## Exit criteria

Reviewers can prove from code/tests that CPU legality and SingNextOS external authority are independent gates and neither can impersonate the other.