# Phase 90 — Deferred: CapRef<T> and SharedArena

## Status

**Not part of SingCap-M v1.**


## Baseline source anchors

- SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`
- Technical specification: `../SINGCAP_M_TECHNICAL_SPEC.md`


## Why deferred

Current SingNextOS already has three mechanisms that cover the principal object-sharing needs:

```text
generated SIP operations
sealed service-owned handles
Region ownership/borrow sharing
```

Adding a generic `CapRef<T>` or shared managed-object arena before a concrete workload proves necessity would enlarge the TCB and create new reachability, GC, aliasing, revocation and serialization problems.

## Re-entry criteria for CapRef<T>

A future ADR may propose CapRef only if it demonstrates a real service where:

- repeated SIP proxy calls are measurably inadequate;
- sealed operation handles do not express the contract cleanly;
- Region sharing is semantically wrong because the resource is not byte/buffer state;
- the proposed proxy still resolves through the **same capability ledger**;
- no generic object reference crosses the boundary.

## Re-entry criteria for SharedArena

SharedArena requires a substantially stronger proof:

- concrete object-graph workload;
- ownership and mutation model;
- GC root/lifetime integration;
- revocation/reuse semantics;
- no sibling reachability leakage;
- object graph schema restrictions;
- performance benefit over SIP/Region approaches;
- full adversarial qualification.

## Prohibited shortcut

Neither subsystem may be introduced merely because it resembles CHERI object capabilities. CHERI principles do not justify a second software object-ownership universe.
