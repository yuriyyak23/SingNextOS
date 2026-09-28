# Phase 8 — Compiler Lowering, Admission, and Bundling

## Goal

Lower provider-neutral IR intent into existing HybridCPU carriers while preserving typed-slot admission and ensuring that compile-time metadata never impersonates SingNextOS runtime authority.

## Lowering targets

Use existing carrier families where semantics fit:

- lane6 `DmaStreamCompute` descriptors for DSC/streaming execution;
- lane7 `SystemSingleton` / L7-SDC descriptors for system/external accelerator commands;
- ordinary load/store forms for normal CPU access to memory, including memory that may later be placed on CXL Type-3 by SingNextOS.

Do not add a `CXL` instruction solely because runtime storage/execution happens over CXL.

## Descriptor metadata

Lower only stable semantic fields required by HybridCPU runtime, for example:

- operation semantic kind;
- region roles / footprint identity;
- staged publication requirement;
- coherent-access requirement;
- effect/retry constraints;
- cancellation/fence requirements;
- compiler structural certificate inputs.

Do not lower provider topology.

## HybridCpuBundleBuilder

Extend bundle preflight only for structural facts that the compiler can prove. Existing slot capacities and aliased-lane conflicts remain authoritative for compiler admission.

Runtime-only provider facts must not enter `StructurallyAdmissible` as if statically proven.

Examples of compile-time checks:

- descriptor encoding fits carrier;
- lane6/lane7 placement is valid;
- required system singleton count does not exceed capacity;
- conflicting aliased lane use is rejected;
- footprint metadata is internally consistent;
- direct-output intent is encoded explicitly if requested.

Examples that remain runtime checks:

- SingNextOS provider availability;
- current device/region authority;
- current coherence support;
- current mapping/fabric generations;
- secure-compute readiness;
- whether zero-copy/direct output is presently safe.

## Compiler/runtime handshake

Emit a compact semantic contract version and descriptor identity that `HybridCPU_ExternalRuntime` can correlate with the request sent to SingNextOS.

If runtime cannot satisfy the emitted semantic requirements, execution must reject/fallback according to explicit policy rather than silently weaken semantics.

## Fallback policy

Define which intents permit runtime fallback, for example:

- `external-preferred` may fall back to CPU execution if an equivalent implementation exists;
- `external-required` must fail if not admitted;
- `coherent-preferred` may use staging;
- `coherent-required` may not silently become non-coherent;
- `direct-output-preferred` may become staged output;
- `direct-output-required` must be rare and explicitly rejected when not provable.

## Diagnostics

Compiler diagnostics should explain semantic requirements, not CXL topology. Runtime diagnostics may later state that no provider satisfied them.

## Tests

Add lowering golden tests for lane6/lane7, bundle capacity tests, alias-conflict tests, semantic version tests, fallback-policy encoding tests, and assertions that no raw CXL identifier appears in emitted IR/descriptor dumps.

## Exit criteria

The compiler produces existing HybridCPU execution carriers plus provider-neutral semantic metadata, and all actual CXL/provider selection remains deferred to SingNextOS runtime admission.