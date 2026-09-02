# EXT-HCPU-006

**Status:** External Blocked

## Required external capability

The external HybridCPU platform integration must expose truthful, versioned discovery and neutral bindings for virtualization/nested-domain/evidence services, and must distinguish production-positive SecureCompute support from policy/projection/future-gated scaffolding.

## Why SingNextOS needs it

SingNextOS intends to use HybridCPU virtualization and secure-domain capabilities without making VMX/VMCS compatibility state the OS authority model. The current HybridCPU documentation contains neutral virtualization owners and a SecureCompute architecture under activation hardening. A platform binding must therefore be able to say both **supported** and **not production-active** precisely.

## Existing interface expected

An already existing or externally supplied platform interface that can, where supported:

- discover neutral virtualization-domain support;
- create/adopt bounded execution/memory/I/O child-domain composition;
- expose nested-domain capability filtering and lifecycle;
- expose classified platform evidence without leaking host-only authority;
- expose optional VMX compatibility projection separately from neutral authority;
- report SecureCompute availability and exact supported activation class;
- return unsupported/denied for future-gated secure backend, migration, nested or publication contours.

## Minimal reproduction

1. Query the external provider's versioned feature manifest.
2. Verify neutral virtualization support can be distinguished from VMX compatibility support.
3. Verify a child domain cannot receive more authority than its parent capability set.
4. Read one allowed evidence projection and prove the evidence object cannot be reused as a capability/grant.
5. Query SecureCompute and verify the provider reports the current positive/negative state truthfully.
6. If SecureCompute is not production-positive, verify SingNextOS confidential-domain creation fails closed rather than falling back to ordinary memory/execution.
7. Verify stale domain/restore generations invalidate old external bindings or are reported as unsupported if the provider lacks restore semantics.

## SingNextOS component blocked

Concrete HybridCPU-backed virtualization, nested-domain, platform-evidence and confidential-domain services. Local API/contracts and ordinary capability/ownership runtime remain unblocked.

## Explicit non-request

This requirement does **not** ask for:

- SecureCompute activation;
- new VMX functionality;
- mutable VMCS authority;
- new nested execution backend;
- hardware-rooted signing if not already present;
- secure migration if not already production-positive.

The correct adapter behavior for absent functionality is explicit unavailability.

## Fallback/mock used

Host test providers may expose neutral virtualization/evidence test doubles, but they must never be presented as HybridCPU SecureCompute evidence or production hardware attestation.