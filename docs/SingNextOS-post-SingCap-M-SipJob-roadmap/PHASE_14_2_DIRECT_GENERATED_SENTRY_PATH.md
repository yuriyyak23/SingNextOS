# P14-2 — Direct Generated Intra-Runtime Sentry Path

## Goal

Execute a qualified same-runtime Job edge without intermediate channel/response materialization while preserving the exact generated SIP security transition.

## Core rule

**Do not call the service implementation directly from the Job executor.** The executor calls an operation-specific generated trusted thunk/sentry, and only that thunk projects exact live authority and invokes the service implementation.

```text
JobExecutor
   -> GeneratedStageSentryThunk
       -> exact session/protocol/authority projection
       -> ManagedCap SIP implementation
```

## Normal vs fused adapters

For one SIP contract/method generate two trusted adapters from the same metadata:

```text
NormalRuntimeAdapter
  -> RuntimeSipClientTransport -> channel/session/response

JobStageAdapter
  -> direct trusted sentry -> implementation
```

The application contract remains unchanged; no `FooDirect()`/`FooUnsafeForJob()` API is introduced.

## Transport elements eligible for elision

Only for a stage classified inline/local and not requiring an observable independent lifecycle:

```text
ChannelEnvelope allocation
queue enqueue/dequeue
intermediate ResponseEnvelope
per-edge TaskCompletionSource/waiter
service rediscovery already fixed by binding
scheduler wake-up solely caused by the materialized edge
```

## Semantics that remain

```text
current process/domain/service generation
EndpointSession generation/state semantics
exact capability requirement/operation admission where applicable
sealed object check where applicable
Region projection/transition hooks
protocol-state legality/transition
bounded copied-value schema
response ownership/authority declaration
```

A direct path must not turn protocol metadata into a Job-owned state machine. It invokes the same authoritative transition owner or a narrowly refactored shared primitive used by both normal and fused paths.

## Invocation lifecycle rule

P14-2 supports only stages whose intermediate invocation is not independently observable/cancellable and has no provider callback. Such inline stages may use lightweight Job stage correlation evidence. Any stage that needs ordinary invocation lifecycle is materialized and handled by P14-5.

## TCB shape

Trusted runtime may retain implementation references in a private binding table keyed by exact service incarnation + contract digest + generated thunk ID. These references are never returned to ManagedCap code and are invalidated on service/runtime generation change.

## Primary paths

```text
sdk/SingPlus.Generators/ClientRuntimeAdapterGenerator.cs
sdk/SingPlus.Generators/SingPlusGenerator.cs
src/Runtime/SingPlus.Runtime/RuntimeSipClientTransport.cs
src/Runtime/SingPlus.Runtime/Services/RuntimeKernel.Services.cs
src/Runtime/SingPlus.Runtime/Services/EndpointSessionInvocationRegistry.cs
new/internal Jobs execution code
```

## PR slices

- P14-2.1: generated stage thunk ABI/internal interface.
- P14-2.2: refactor shared protocol/session/sentry validation primitive used by normal and direct paths.
- P14-2.3: direct synchronous inline execution for bounded-value-only 2-stage plan.
- P14-2.4: one final external publication; no intermediate `ResponseEnvelope`.
- P14-2.5: instrumentation counting materialized vs elided boundaries.

## Differential tests

For each representative method run normal SIP and fused Job with the same caller/session/capability state and assert equivalent:

```text
success/failure code
protocol state
returned declared value/handle
ownership state (once P14-3 is enabled)
revocation/stale-generation denial
final publication status
```

Negative races include session close and capability revoke between prepare and sentry commit. Direct execution may win only at the same documented linearization point as normal execution.

## Performance gate

Measure at minimum:

```text
2/3/4 empty inline stages normal vs fused
allocations/op and GC bytes/op
queue operations/op
intermediate invocation/response records/op
median/p95/p99 latency
```

No performance result qualifies security; P14-8 closes the claim.

## Exit criteria

A representative bounded-value linear chain executes through generated sentries with intermediate transport materialization absent, while normal and fused semantic conformance tests remain equivalent.
