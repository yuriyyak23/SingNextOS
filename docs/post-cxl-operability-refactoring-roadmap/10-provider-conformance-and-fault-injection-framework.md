# Phase 10 — Provider Conformance and Fault-injection Framework

## Goal

Turn SingNextOS provider semantics into reusable executable qualification so future GPU, NPU, storage, network, RDMA or other backends can be added without weakening authority/effect invariants.

## Framework architecture

Create reusable test abstractions conceptually similar to:

```text
ProviderConformanceSuite<TContract>
ProviderScenario
ProviderFaultPlan
ProviderEffectOracle
ProviderClosureOracle
ProviderGenerationOracle
```

Production provider interfaces remain unchanged unless qualification exposes a missing semantic requirement.

## Conformance dimensions

Every effect-capable provider family must be tested for:

- exact identity/generation handling;
- wrong owner/domain rejection;
- stale request rejection;
- malformed success receipt handling;
- proven pre-effect rejection semantics;
- ambiguous post-effect failure semantics;
- exception after effect boundary;
- exact terminal closure;
- double close behavior;
- stale close behavior;
- reset/generation change while active;
- reconfiguration while active;
- teardown during active work;
- cancellation at every meaningful lifecycle stage;
- recovery-token identity/exactness where supported;
- quarantine when closure/containment proof is absent.

## Fault plans

Required deterministic fault modes:

```text
FailBeforeEffect
FailAfterAcceptance
ThrowAfterAcceptance
ReturnMalformedReceipt
ChangeGeneration
DelayClosure
FailClosure
ResetDuringSubmitted
ResetDuringVisible
ReturnStaleClosureReceipt
```

Fault plans are test/model facilities, not unprivileged production APIs.

## Semantic oracle

Tests must assert both provider result and SingNextOS local authority state:

- whether RegionUse remains pinned;
- whether budget remains charged;
- whether service teardown is blocked/quarantined;
- whether publication was suppressed;
- whether stale generation is rejected;
- whether exact closure permits reclaim.

## Integration targets

At least two distinct provider families should consume the reusable framework before closing the phase, for example:

- CXL/compute model provider;
- generic platform memory/device or HybridCPU adapter model.

The point is proving reuse across contract families rather than creating a framework tailored to one provider.

## Supervisor/fault integration

Use fault injection to exercise:

- service crash during Submitted;
- replacement blocked by ambiguous provider effect;
- deadline/cancellation after acceptance;
- budget pin retained until closure;
- trace divergence/fault correlation;
- telemetry fault visibility;
- checkpoint refusal while faulted effect is live.

## Required tests

Framework self-tests plus provider-specific matrices must prove deterministic repeatability and cleanup between scenarios. A failed fault-injection scenario must not contaminate the next scenario's generations or accounting.

## Exit criteria

At least two provider families pass the common semantic suite, ambiguous effect handling is tested generically, and new provider authors have a clear executable qualification path.