# Phase 08 — Generated SIP Sentries and Deep Value Schemas

## Goal

Turn every generated cross-compartment invocation into an exact, non-ambient security transition over the existing EndpointSession and ownership machinery.

## Entry dependencies

P08 is a join point and may not start semantic implementation until P06 has qualified sealed identity resolution and P07 has qualified Region synchronization/use pins. It also consumes the P04 Authority Composition Protocol. A generated sentry must not paper over an unqualified seal or Region owner with sequential validation.


## Baseline source anchors

- SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`
- Technical specification: `../SINGCAP_M_TECHNICAL_SPEC.md`


## Current code truth

The repository already has SIP attributes, source generators, ownership annotations and EndpointSession runtime state. Extend these surfaces; do not build a parallel RPC stack.

## Generated sentry responsibilities

For each method, generated trusted code must know and validate:

```text
caller process/domain generation
EndpointSession generation/state
required capability handles + exact constraints
sealed handle marker/type/service/session
Region ownership / borrow / consume / MOVE declarations
bounded copied-value schema
temporary delegation lifetime
response ownership/authority declarations
```

## InvocationAuthorityContext

If an internal context type is introduced, it must be non-enumerable and operation-specific. A service implementation should receive only the already-projected authority needed by that method.

Bad:

```text
context.GetCapability(id)
context.AllCapabilities
ServiceLocator.Current
AsyncLocal<AuthorityContext>
```

Preferred generated internal projection:

```text
WriteFileInvocationAuthority
  FileHandleProjection
  WriteBufferProjection
```

## Deep value schema

Generator/analyzer must recursively validate all copied SIP value graphs. A `record` containing `List<T>`, mutable array graph or arbitrary object reference is not automatically safe.

Define canonical allowed bounded collection/value forms and maximum sizes/cardinalities.

Use a positive recursive schema allowlist (`Primitive`, `Enum`, bounded string/bytes, generated value record, bounded immutable vector, explicit handle) rather than an inference that a CLR graph “looks immutable”. Any unclassified node or unbounded cardinality fails generation/admission.

## Publication

Generated sentry may stage ownership return/response publication, but must not claim rollback of arbitrary private service mutation. Runtime-mediated staged effects are committed only after their exact validation.

## Primary paths

```text
sdk/SingPlus.Sip.Sdk/
sdk/SingPlus.Generators/SingPlusGenerator.cs
sdk/SingPlus.Generators/ClientRuntimeAdapterGenerator.cs
sdk/SingPlus.Analyzers/SingPlusAnalyzer.cs
src/Runtime/SingPlus.Runtime/Services/*
src/Runtime/SingPlus.Runtime/RuntimeSipClientTransport.cs
src/Sip/SingPlus.Sip/*
tests/SingPlus.Tests/Contracts/*
tests/SingPlus.Tests/Runtime/*
```

## PR slices

- **P08-1:** sentry metadata model + deterministic generator golden files.
- **P08-2:** deep-value schema validator/analyzer.
- **P08-3:** runtime exact invocation projection.
- **P08-4:** temporary grant/borrow cleanup on success/fault/cancel.
- **P08-5:** migrate socket pilot to generated sentry end-to-end.

## Tests

- omitted/wrong rights capability;
- correct token wrong session;
- stale session generation;
- wrong sealed type;
- raw mutable CLR object in contract rejected;
- deep mutable graph hidden inside record rejected;
- response cannot return undeclared authority;
- exception cleans temporary delegation;
- cancellation does not imply external effect cancellation or Region return;
- session close racing invocation/async completion is deterministic.
- Region MOVE/reclaim and sealed close/service restart racing final sentry admission deny or commit as one correlated attempt;
- no generated sentry calls service/provider code while an authority lock is held.

## Exit criteria

Representative generated calls no longer rely on hand-coded ambient lookup for authority and the contract schema prevents implicit mutable object-graph authority.
