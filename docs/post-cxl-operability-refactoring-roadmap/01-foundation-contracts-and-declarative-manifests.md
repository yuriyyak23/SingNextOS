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