# Phase 10 — CXL Memory and Coherent Access Semantics

## Goal

Define how HybridCPU-v2 and the compiler behave when SingNextOS places memory or execution on CXL 3.x/4.x resources, without making CXL topology part of the CPU architecture.

## CXL.mem Type-3 memory

Ordinary CPU access to a region backed by CXL Type-3 remains ordinary memory access from the compiler/ISA perspective.

Do not:

- add a CXL load/store instruction solely for Type-3 placement;
- model host CXL.mem access as an IOMMU mapping;
- encode HDM/DPA/interleave details in compiler IR or descriptors.

HybridCPU may consume semantic memory attributes from SingNextOS when they affect correctness/performance, such as latency class, persistence semantics, NUMA/placement hints, or whether an external provider can access the region.

## CXL.cache

Treat coherent device access as a semantic capability, not as ownership or publication authority.

HybridCPU/compiler may express `coherent access required/preferred`, but runtime must still prove:

- region authority;
- compatible access mode;
- no conflicting writer policy violation;
- visibility/publication semantics;
- current generation validity.

Do not assume a universal IOMMU authorization model for CXL.cache.

## CXL.io

CXL.io remains PCIe-compatible control/device access beneath SingNextOS. It does not require a new HybridCPU ISA class.

If L7/DSC services ultimately target a CXL device, the CPU sees the same provider-neutral ExternalRuntime contract.

## Compiler placement hints

Compiler annotations may express stable placement intent only when program semantics benefit from it, for example:

```text
capacity-preferred
low-latency-preferred
persistent-memory-required
external-device-access-required
coherent-shared-access-preferred
```

Avoid `place-on-CXL-port-X` style directives in normal IR.

## Memory ordering

Document and test which HybridCPU fences/orders are required around:

- CPU writes before external submit;
- external writes before CPU consumption;
- staged publication;
- direct coherent writes;
- persistent memory durability when supported.

Provider-specific cache maintenance remains below ExternalRuntime; HybridCPU consumes semantic completion/visibility results.

## Zero-copy

Zero-copy is selected only after runtime proof that ownership, access, coherence/visibility, alignment/footprint, replay/effect class, and lifetime constraints are satisfied.

The compiler must never promise zero-copy as an unconditional ABI property.

## CXL 4.x

CXL 4.x bandwidth, bundled-port, retimer, RAS, or fabric enhancements should normally alter provider capability/performance data rather than CPU instruction semantics.

## Tests

Add tests showing that the same emitted program can run with local memory, CXL Type-3 memory, staged Type-2 accelerator access, or another compatible provider without recompiling for topology.

## Exit criteria

CXL-backed memory/execution can vary at runtime while HybridCPU architectural semantics remain provider-neutral and stable.