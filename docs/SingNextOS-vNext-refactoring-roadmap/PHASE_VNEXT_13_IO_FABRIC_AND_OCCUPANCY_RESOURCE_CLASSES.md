# P13 — DMA, CXL/fabric, network and occupancy resource classes

## Goal

Generalize the proven ComputeTime authority model to non-CPU heterogeneous resources without introducing a generic unit that hides incompatible semantics.

## Resource families

### Throughput

```text
DmaBandwidthBytes / window
MemoryBandwidthBytes / window
FabricBandwidthBytes / window
NetworkTxBytes / window
```

### Occupancy

```text
DeviceLocalMemoryBytes
GuestMemoryBytes (only if existing accounting semantics can be safely projected)
AcceleratorContexts
QueueSlots
InflightOperations
```

## CXL rule

Public authority is semantic:

```text
remote-memory capacity
fabric bandwidth
coherent-access eligibility
```

Never expose CXL HDM index, DPA/HPA interleave, mailbox offsets, route/port identity as authority.

## DMA rule

DMA resource capability limits capacity/bandwidth/concurrency but does not replace:

```text
DmaCapability
Region ownership/use
IOMMU/platform mapping authority
completion/visibility/closure
```

## Cross-resource operations

An accelerator operation may require a vector of leases:

```text
ComputeTime + DmaBandwidth + DeviceMemory + ConcurrentOperations
```

Acquisition must avoid deadlock and partial non-compensatable commit. Define canonical ordering for reversible reservations, then final commit.

## No unit laundering

Do not merge `100us CPU` and `100us NPU` into generic `200us compute` unless a separately specified semantic class defines portable equivalence.

## Exit criteria

Each new resource family is separately gated and qualified; no blanket "heterogeneous budgets supported" claim.
