# P14-7 — HybridCPU / Provider-Neutral Scheduling Integration

## Goal

Expose enough semantic Job structure to SingNextOS scheduling/provider layers to improve placement/offload decisions without leaking HybridCPU physical topology or inverting authority.

## Principle

A Job is a semantic dependency/authority/dataflow graph. It may describe **what kind of execution a stage can accept**, never which physical HybridCPU lane/opcode/register/topology it must use.

Allowed execution classes may resemble:

```text
ManagedCpuCompute
ReadOnlyRegionCompute
ExclusiveRegionCompute
MatrixSemantic
BulkCopySemantic
BulkComputeSemantic
ExternalIo
ProviderNeutralAccelerator
```

Exact names are implementation choices. They are hints/eligibility classes, not authority.

## Layering

```text
SIP contracts
  -> verified SipJob graph
      -> Region/authority dependency graph
          -> SingNext scheduler semantic plan
              -> Platform Authority Bridge / NeutralRuntime
                  -> qualified HybridCPU/provider mechanisms
```

Application/Job metadata MUST NOT contain:

```text
lane number/mask
raw VLIW bundle
raw HybridCPU opcode
VMCS pointer
IOMMU id
direct provider token
CXL BDF/HPA/DPA topology
```

## Authority split

Before any external/hardware effect:

```text
local SingNext capability/session/Region/seal authority
AND
exact live provider/platform admission
AND
feature availability/claim permits the contour
```

Job scheduler evidence cannot mint either side.

## Read-only DAG scheduling

If `FG-READONLY-DAG` and `FG-DAG-PARALLEL` are qualified, independent read-only branches may be scheduled concurrently/SMT-aware. Scheduling decision must not change the Region conflict/ownership result.

## Provider stages

`FG-EXTERNAL-EFFECT-STAGE`/`FG-HYBRIDCPU-ACCEL` require ordinary existing lifecycle:

```text
Prepared -> Admitted -> Submitted -> DeviceComplete -> Visible -> Published -> Released
```

A Job may reduce local transport around this lifecycle but cannot compress these states into “stage complete”. Ambiguous provider completion keeps affected resources pinned/quarantined according to existing rules.

## HybridCPU compatibility

Use the exact qualified provider package/source contract. Do not modify HybridCPU ISA, capability semantics or lane model merely to support SipJob. If the desired scheduling information cannot be expressed provider-neutrally, the stage falls back to ManagedCpu/normal SIP until a separately versioned external contract is qualified.

## PR slices

- P14-7.1: provider-neutral `StageExecutionClass`/eligibility contract.
- P14-7.2: scheduler receives verified dependency/Region-use graph.
- P14-7.3: read-only branch placement experiment behind gate.
- P14-7.4: external effect stage integration with existing `ExternalOperationAuthority`.
- P14-7.5: HybridCPU provider conformance with exact package/source digest.

## Tests

```text
no lane/opcode/topology type appears in public SIP/Job contract
same Job works when HybridCPU feature unavailable via fallback/rejection rules
provider admission receipt never satisfies SingNext capability check
provider generation never substitutes Region/resource/session generation
completion without visibility/publication does not release/publish
parallel placement preserves deterministic ownership/publication semantics
provider callback re-entry obeys lock/provider boundary
```

## Performance evidence

Separate:

```text
Job fusion savings
scheduler/placement savings
provider execution latency
hardware acceleration savings
```

Do not attribute provider/hardware speedup to SipJob transport fusion or vice versa.

## Exit criteria

`FG-HYBRIDCPU-HINTS` may be qualified without claiming acceleration. `FG-HYBRIDCPU-ACCEL` remains off unless provider-specific executable evidence independently qualifies it.
