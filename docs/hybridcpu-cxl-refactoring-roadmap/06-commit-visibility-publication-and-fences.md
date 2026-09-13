# Phase 6 — Commit, Visibility, Publication, and Fences

## Goal

Refactor HybridCPU external accelerator commit/fence semantics to consume the SingNextOS lifecycle explicitly and preserve architectural publication rules.

## Core distinction

HybridCPU must represent:

```text
DeviceComplete != Visible != Published != Released
```

`AcceleratorCommitCoordinator` remains the architectural publication owner for staged L7 output, but it must consume an explicit visibility/publication precondition rather than infer readiness from device completion.

## AcceleratorCommitCoordinator changes

Extend commit input with an opaque external-operation receipt set that proves:

- exact operation/descriptor identity;
- DeviceComplete observed;
- required visibility action completed;
- admission/generation context still valid for publication;
- staged output coverage matches descriptor footprint;
- no direct-write violation occurred;
- effect class permits staged publication.

The coordinator must reject stale, mismatched, duplicate, or incomplete receipts.

## Visibility layer

Introduce a runtime abstraction that maps SingNextOS semantic visibility results into CPU publication eligibility, without embedding provider-specific cache operations in HybridCPU core.

Examples of semantic states:

- coherent and already CPU-visible;
- explicit acquire completed;
- cache maintenance completed;
- visibility failed;
- provider reset made result indeterminate.

## Fence behavior

Refactor accelerator fences to distinguish policies:

- observe active operations;
- wait for DeviceComplete;
- wait for Visible;
- commit completed staged operations;
- cancel pre-submit/admitted operations;
- request cancellation of submitted operations when supported;
- drain after reset/reconfiguration;
- establish replay barrier for irreversible effects.

A fence must not convert `DeviceComplete` into publication without visibility and authority revalidation.

## Direct coherent writes

If later enabled, direct writes bypass staged copy but do not bypass:

- owner/domain guards;
- generation validation;
- visibility semantics;
- replay effect classification;
- retirement/fence ordering.

By default they should form an irreversible replay/publication boundary.

## Fault semantics

If reset/reconfiguration occurs after DeviceComplete but before publication, the commit path must fail closed unless SingNextOS can still prove the result visible and valid for the exact admitted operation.

## Tests

Add tests for:

- completion without visibility rejected;
- visibility without matching operation rejected;
- stale generation at commit rejected;
- duplicate publication rejected;
- reset between complete and visible;
- reset between visible and publish;
- fence wait modes;
- staged rollback before publication;
- irreversible direct-write barrier behavior.

## Exit criteria

No external accelerator result becomes architectural solely because the device reported completion.