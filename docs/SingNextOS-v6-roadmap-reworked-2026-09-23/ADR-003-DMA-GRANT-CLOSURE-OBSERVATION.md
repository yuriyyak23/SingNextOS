# ADR-003 — Separate DMA grant closure observation

Status: accepted for the additive internal managed observation contour, 2026-09-30.

## Context

DMA submission reaches completion and required visibility, then a separate provider revoke
closes its grant. Neither transition publishes application output, settles an external-operation
budget nor releases the Region reservation. Generic SemanticTraceEventKindV1.Released requires
Published and Settled. Changing that meaning would weaken existing consumers.

## Decision

Keep the V1 enum and generic validator unchanged. Add DmaGrantClosureObservationV1
(`singnext.dma-grant-closure-observation/1`) and an offline projection validator. The observation
links an opaque exact grant identity digest, provider generation, local backend epoch and the
last Visible event of the latest completed traced submission. Canonical encoding uses
little-endian fixed-width numbers, binary SHA256 digests and length-prefixed strict UTF8 tokens.

The bridge retains only one bounded last-visible snapshot in its existing DmaGrantRecord.
An untraced later cycle clears it. MarkDmaGrantClosed captures the closure epoch after successful
provider revoke and final incarnation/epoch checks. QueryV6PlatformDmaGrantClosure observes that
owner record; it does not call a provider, perform closure or release any resource. No callbacks
are added and no sink executes under an owner lock. Observation absence/failure does not affect
execution or reclaim decisions. The offline consumer checks exact prefix equality and tuple
consistency, including generic lifecycle validity, rather than adding fabricated events.

No observation field is consulted by grant admission, closure, Region or budget reclaim.
The supplied expected tuple and source/dependency manifest delimit evidence consistency.
Digests are correlation, not signatures/authentication, and cannot prove physical containment.
Provider/source identity must still be bound by the qualification artifact; a matching digest
alone cannot authorize an operation or establish a deployment claim.

## Consequences and compatibility

No existing public enum/API is modified. One additive Contracts DTO/checker and an internal
runtime query are added. V1 paths and generic traces remain compatible. No HybridCPU package,
ISA, opcode, machine legality or architectural state changes. All v6 gates remain OFF.
Managed production of the sidecar has a RuntimeEnforced ceiling for the tested owner observation;
physical grant containment remains FutureGated. Instrumentation memory/time is not qualified
by existing generic checker microbenchmarks. Exact lost-effect reconciliation remains separate.

Required evidence: read/write direction, pre-close refusal, stale grant/expected tuple refusal,
provider reset/exception/revoked status, backend reset, later untraced cycle, sink loss, canonical
negative cases and no publication/settlement/Region-release implication.
## Canonical reader completion — 2026-09-30

The additive ParseCanonical reader accepts at most 512 bytes, bounds each UTF8 token at
256 bytes before consumption and requires exact byte equality with canonical reserialization.
It rejects all truncated prefixes, trailing data, overlong length encoding, invalid UTF8,
unknown versions and zero/invalid tuples. The reader returns observation only; it is not an
admission, downgrade, reconciliation or closure API. Runtime-produced observations are decoded
before offline projection in the executable managed test contour. No new package/schema version
or existing API semantics change is required.