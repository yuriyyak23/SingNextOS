# P05 — SIP AND MANIFEST RESOURCE CONTRACTS

**Live disposition:** closed at `RuntimeEnforced` for generated, internal host/JIT ComputeTime/Nanoseconds sentry admission on current HEAD `8c3f55e47555b2db99356b861ee404211d072edc`; `FG-VNX-SIP-RESOURCE` remains OFF. The phase entered on `b86cc33c16a77760c6971cc1a656c34b6cf2a10d`; an externally-created merge commit incorporated the in-progress work without a dirty-tree conflict. Donation remains P06 FutureGated. See `EVIDENCE_P05_SIP_RESOURCE_CONTRACTS.md` and `P05_QUALIFICATION_TUPLE.json`.

**2026-09-22 sequential re-audit:** current HEAD is `800027893dcf1ed23d8d7fe775841dd9d01a9fff`. A cross-layer canonicalization defect was remediated: non-trimmed semantic scopes are now rejected consistently by the SIP contract, manifest validator and generator. Current evidence is `P05_REAUDIT_20260922.md` with tuple `P05_REAUDIT_20260922_TUPLE.json`. The claim remains limited to the internal generated JIT contour; the gate remains OFF.

## Purpose

Add typed, generator-known resource requirements to SIP and manifests while preserving generated sentries as the security transition and metadata as non-authoritative.

## Preconditions

- P04 closed.
- Ordinary SIP remains semantic oracle.

## Architectural decisions

- Generated declarations describe required semantic resource class/envelope and donation policy; they never embed a trusted mutable grant.
- Generated sentry resolves live effect capability, resource-use grant, budget lease, Region/session state and invokes P04 admission protocol.
- Manifest admission declares maxima/accepted donation classes but materializes no authority.
- Unknown/dynamic/reflection bypasses fail closed or use ordinary validated fallback.

## State / linearization model

No new independent state machine. Sentry uses existing session invocation lifecycle plus P03/P04 lease/admission state.

## Negative-space obligations

- effect cap present but resource grant absent;
- budget present but effect cap absent;
- reflection/dynamic dispatch bypass;
- manifest says guarantee on unqualified platform;
- stale generated metadata/version;
- service throws after admission but before provider submit.

## Required executable tests

- Generated-code tests verifying all gates are live-resolved.
- Bypass/reflective dispatch negative tests.
- Old SIP contract compatibility tests.
- Manifest version/unknown resource class fail-closed tests.
- Service exception cleanup/lease release test pre-submit.

## Expected code / contract owners

- SIP generator/analyzer
- ServiceManifest contracts/admission
- GeneratedSentry/runtime invocation path

## Claim boundary

`RuntimeEnforced`. This phase MUST NOT claim a stronger contour without P16 evidence.

## Exit criteria

- No metadata field can validate as authority.
- Ordinary SIP end-to-end path closes pre-submit failures correctly.

## Prerequisite for next phase

P06 donation is layered onto the ordinary SIP sentry only after P05 closes.
