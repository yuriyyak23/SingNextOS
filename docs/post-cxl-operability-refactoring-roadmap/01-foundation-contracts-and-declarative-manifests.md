# Phase 1 — Foundation Contracts and Declarative Manifests

## Goal

Freeze shared service identity, dependency, configuration, feature-request and budget-request contracts for all later phases. Configuration remains descriptive; live authority continues to be materialized by existing SingNextOS capability and platform admission mechanisms.

## Required outcomes

- versioned `ServiceId`, `ServiceGeneration`, `ServiceInstanceHandle`;
- hard and optional dependency descriptions;
- versioned service manifest schema;
- requested/granted/denied/degraded admission results;
- restart, drain, checkpoint and telemetry policy declarations;
- normalized configuration digest for correlation only;
- deterministic validation;
- dependency graph primitives for the supervisor.

## Rules

A manifest never acts as a capability, device lease, provider lease, replay certificate, or external-operation permission. Semantic platform requests must not expose CXL topology or HybridCPU internal execution details.

## Acceptance

Tests prove generation separation, mandatory versus optional requirements, invalid declaration rejection, deterministic validation, and strict separation between configuration data and live runtime authority.

## Implemented contract and lifecycle

`ServiceManifestV1` is the single versioned declarative component/service request. It reuses `SingProcessManifestV1` for exact process/domain generation, entry point and required local capabilities, and adds:

- typed hard/optional service dependencies;
- provider-neutral platform feature requirements with mandatory/optional criticality;
- restart, drain, ordinary-checkpoint and telemetry declarations;
- declarative budget requests for the Phase 05 ledger;
- runtime compatibility constraints;
- deterministic canonical serialization and SHA-256 normalized digest;
- requested/granted/denied/unsupported/degraded admission decisions.

Legacy `requiredContracts` are normalized to hard dependencies inside the same manifest. They do not create a second dependency model.

`RuntimeKernel.EvaluateComponentAdmission` is a read-only preflight projection. `RuntimeKernel.AdmitComponent` consumes the same evaluator and remains the only materialization path. A preflight result never grants a capability or provider binding. Actual local capabilities are minted by `CapabilityAuthority`; exact platform bindings are created by `PlatformAuthorityBridge`; service sessions are opened through the existing service/session registries.

```text
manifest request
  -> deterministic validation/evaluation
  -> Denied (mandatory requirement unavailable)
  -> Degraded (optional requirement unavailable)
  -> existing kernel/provider materialization
  -> evidence decision updated without embedding live handles/tokens
```

An unknown mandatory platform feature produces typed `Denied` / `PlatformUnsupported` before process/provider materialization. An unknown optional feature produces explicit `Unsupported` and an overall `Degraded` result. Hard dependency binding failure retains the existing component rollback lifecycle; optional binding failure does not abort the component and is recorded as degraded.

`ServiceInstanceHandle` correlates exact service, service generation, process generation and domain under `ServiceIdentityContract.Version`. It remains a typed handle to be validated by the owning registries, not ambient root authority.

## Non-goals and FutureGated

- Policies are declarations only; restart/drain execution belongs to Phase 02, deadlines to Phase 04, budgets to Phase 05, checkpoint execution to Phase 08 and telemetry subscriptions to Phase 09.
- No manifest field contains live capabilities, provider leases, recovery tokens, CXL topology, HybridCPU execution internals or replay authority.
- Dependency-cycle validation and generation-bound binding/rebinding are Phase 02 supervisor responsibilities.
- Budget decisions remain `Requested` until the single Phase 05 authoritative ledger exists; Phase 01 does not emulate quota authority.
- The normalized digest is correlation/evidence only and cannot replace identity, generation or authority validation.
