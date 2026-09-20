# P08 — ComputePlanning v2: semantic resource envelopes

## Goal

Extend `ComputeIntent` / `ComputePlan` with resource semantics while keeping physical placement provider-private.

## New semantic types

Illustrative shapes:

```text
ComputeExecutionClass
  ManagedDefault
  VectorEligible
  MatrixEligible
  StreamingEligible
  ExternalAcceleratorEligible

ResourceExecutionEnvelope
  ResourceClass
  MaximumExecutionTimeNs?
  MaximumBytesPerWindow?
  MaximumConcurrency?
  RequiredAssurance
  PreemptionRequirement?
```

Do not encode lane/slot/opcode/queue/topology.

## Planner inputs

```text
ComputeIntent
Region-use compatibility
EffectCapability status
Resource capability/lease envelope
Provider semantic capabilities
Provider availability/load evidence
Publication preference
Security/virtualization requirements
```

## Planner output

`ComputePlan` remains **non-authoritative**. It may name a provider candidate/generation and resource requirements, but every run revalidates live owners.

## Selection policy

Keep policy separate from authority:

```text
PreferLowerLatency
PreferHigherBandwidth
PreferLowerEnergy (future)
```

Preferences never widen authority.

## Provider fallbacks

If a caller has generic `ComputeTime` but no provider satisfies the semantic class, the plan may fall back only if the contract permits. It must not reinterpret resource units across incompatible providers.

## Tests

- same intent chooses different providers without changing authority;
- stale cached plan fails live revalidation;
- Matrix resource authority does not authorize generic external accelerator effects;
- unknown execution class fails closed;
- direct/staged publication remains independent.

## Exit criteria

`FG-VNX-COMPUTE-V2` may enable host/model execution; HybridCPU still off until P09.
