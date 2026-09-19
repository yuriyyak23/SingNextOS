# P14-0 — Architectural Freeze, Baseline Re-Pin and ADRs

## Goal

Freeze the post-SingCap-M-v1 semantics before introducing a fast path. This phase adds no fused runtime execution claim.

## Entry condition

P13 qualification artifacts exist and the repository state to be modified is known. The exact SingNextOS HEAD, .NET SDK/runtime/compiler tuple, AdmissionVerifier policy digest and HybridCPU package/source identity are re-pinned. Any delta from the P13 qualified tuple is audited rather than inherited by assumption.

## Required ADR decisions

1. `SipJob` is an execution-composition plan/evidence object, never authority.
2. No raw CLR/service implementation reference crosses ManagedCap SIP/Job boundaries.
3. Trusted direct stage thunks are TCB-private; ManagedCap code receives only operation-specific projections.
4. Same generated SIP contract metadata drives normal and fused execution.
5. Fusion is allowed to elide transport only; session/capability/seal/Region/protocol/effect/publication semantics remain owned by existing registries.
6. Job execution is segmented; one-shot/quota/effect authority is not pre-consumed for unreachable future stages.
7. Job failure is not transaction rollback.
8. Intermediate lifecycle is materialized when externally observable or independently cancellable.
9. Feature gates default off and normal SIP is the conformance/fallback path.
10. No dynamic IL/runtime assembly generation is required for P14; runtime composition selects precompiled qualified generated thunks.

## New identity policy

If `SipJobPlanId`, `SipJobBindingId` or `SipJobRunId` are introduced, each identity must define:

```text
owner registry
incarnation/restart behavior
generation/non-reuse rule
serialization policy
whether it is externally visible
why possession is not authority
```

Plan digest is evidence. A replayed plan/binding after runtime/service/process incarnation change must rebind or fail stale.

## Threat model additions

Add explicit adversaries:

```text
forged/replayed Job handle
stale plan cache after revoke/restart
stage substitution with same method name/different contract digest
attempt to inject raw CLR object into an internal edge
confused-deputy use of executor-owned capability
ownership loss on fault after MOVE
cancellation at branch/effect barrier
fusion across unqualified runtime/provider contour
```

## Primary paths

```text
docs/Completed/SingCap-Refactoring/roadmap/* (source constraints)
new docs/roadmap for P14
sdk/SingPlus.Generators/
sdk/SingPlus.Analyzers/
tools/SingPlus.Admission/
src/Runtime/SingPlus.Runtime/Services/
src/Runtime/SingPlus.Runtime/Capabilities/
src/Runtime/SingPlus.Runtime/Regions/
```

## PR slices

- P14-0.1: baseline/source/toolchain re-pin and delta inventory.
- P14-0.2: ADR for Job non-authority/no-raw-reference semantics.
- P14-0.3: ADR for segmented admission/fusion barriers/fallback.
- P14-0.4: architecture checks forbidding parallel Job authority owners.

## Tests/CI

Documentation and architecture tests only. Add static checks for forbidden class names/patterns such as a second Job capability/Region authority manager and for dependencies that would expose service implementation types to ManagedCap public surfaces.

## Exit criteria

All decisions above have one selected answer; feature gates are enumerated; baseline tuple is exact; no P14 runtime semantic code lands before this phase closes.

## Rollback

Documentation/architecture-test rollback only.
