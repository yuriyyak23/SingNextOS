# P05 — SIP AND MANIFEST RESOURCE CONTRACTS

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
