# P00 — ARCHITECTURAL FREEZE AND LIVE BASELINE

**Live disposition:** closed at `ModelOnly` for reviewed HEAD `b86cc33c16a77760c6971cc1a656c34b6cf2a10d`. See `EVIDENCE_P00_ARCHITECTURAL_FREEZE.md` and `P00_QUALIFICATION_TUPLE.json`. All `FG-VNX-*` gates remain OFF.

## Purpose

Freeze exact current owners and the corrected no-second-ledger architecture before code changes.

## Preconditions

- Pin SingNextOS `6227ea7cf258ef6ffce52001d4d2ffee07355b35` and HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2` plus exact build/toolchain/package tuple.
- Re-run current tests for SingCap-M, SipJob, budget, Region, ExternalOperation and provider integration.
- Treat live code/tests as stronger than this roadmap.

## Architectural decisions

- Record `CapabilityAuthority`, `ResourceBudgetAuthority`, `RegionAuthority`, session/invocation, ExternalOperation and publication owners.
- Explicitly delete the design requirement for an independent `TemporalResourceAuthority`.
- Freeze CHERI/ISA/lane/opcode/topology non-goals.
- Create all vNext gates OFF and add static dependency checks.

## State / linearization model

No runtime state machine changes. This phase creates a machine-readable baseline tuple and owner map only.

## Negative-space obligations

- Roadmap baseline differs from live HEAD: perform delta review before every implementation PR.
- Detect any duplicate ledger/owner introduced by in-flight work.
- Reject stale capability/session/Region/provider generations exactly as current code does.

## Required executable tests

- Full current test suite baseline.
- Reflection/dependency test proving no public SIP/ManagedCap ABI exposes HybridCPU private types.
- Static test that budget snapshots/reservations do not claim effect authority.
- Owner-map test/document check tied to concrete code paths.

## Expected code / contract owners

- `src/Runtime/SingPlus.Runtime/RuntimeKernel.cs`
- `CapabilityAuthority`, `ResourceBudgetAuthority`, `RegionAuthority`, `ExternalOperationAuthority` owners
- current SipJob and HybridCPU adapter contracts/tests

## Claim boundary

`ModelOnly`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- Exact baseline tuple committed.
- All owners named with mutation/linearization boundaries.
- All gates exist and are OFF.
- No runtime semantics changed.

## Prerequisite for next phase

P01 may start only after baseline and owner map have no unresolved duplicate-owner finding.
