# Platform, heterogeneous execution and devices

## Platform boundary

[`SingPlus.Platform.Abstractions`](../../../src/Platform/SingPlus.Platform.Abstractions/SingPlus.Platform.Abstractions.csproj) defines provider contracts for admission/evidence, memory and devices, DMA/MMIO/IRQ, virtualization and guests, executable artifacts, CXL and SecureCompute. [`PlatformAuthorityBridge`](../../../src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.cs) adapts calls to authoritative runtime owners and exact provider generations; it must not become another permission or budget ledger.

The [host implementation](../../../src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.cs) is a software/model provider. The [HybridCPU implementation](../../../src/Platform/SingPlus.Platform.HybridCpu/HybridCpuPlatformAuthorityProvider.cs) is a separate adapter path with neutral-runtime dependencies. The existence of an interface does not guarantee every provider implements it or that model behavior qualifies physical hardware.

## External effects and original resolution

[`ExternalOperationAuthority`](../../../src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs) tracks operation preparation, admission, submission, terminal/quarantined states and dependencies. The bridge retains an original request before possible effect and canonical raw observations before callbacks. On a lost response, only a pure **same-original** resolution may reconcile; never repeat irreversible `Submit`, `Start`, `Bind`, `Transfer` or `Revoke` merely because a reply was absent.

Independent distinctions: provider completion, memory visibility, local publication, result consumption, proof of no-further-use, and final backing/charge reclamation.

## DMA and translation

The source tree contains [DMA grants](../../../src/Platform/SingPlus.Platform.Abstractions/PlatformDmaGrantContracts.cs), [DMA submission](../../../src/Platform/SingPlus.Platform.Abstractions/PlatformDmaSubmissionContracts.cs), [DMA completion](../../../src/Platform/SingPlus.Platform.Abstractions/PlatformDmaCompletionContracts.cs), [visibility](../../../src/Platform/SingPlus.Platform.Abstractions/PlatformDmaVisibilityContracts.cs), [page fault](../../../src/Platform/SingPlus.Platform.Abstractions/PlatformDmaPageFaultContracts.cs), and [copy backing](../../../src/Platform/SingPlus.Platform.Abstractions/PlatformDmaCopyBackingContracts.cs) contracts. Actual bridge consumers live under [platform DMA](../../../src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.Dma.cs), [copy lifetime](../../../src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.DmaCopyLifetime.cs) and related files.

A software original DMA copy is a bounded execution contour, **not** proof of physical IOMMU isolation, cache coherence, or all-hardware no-further-use. IOVA/PASID mappings must not impersonate Regions. Completion alone does not release source/destination backing or independent charge dependencies.

## MMIO, IRQ and drivers

[`SingPlus.Drivers`](../../../src/Drivers/SingPlus.Drivers/SingPlus.Drivers.csproj) provides console/timer interfaces and manifests. Provider MMIO-register and IRQ contracts have [actual bridge paths](../../../src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.MmioRegisters.cs) and [IRQ handling](../../../src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.Irq.cs). Provider delivery and `KernelEventRegistry` waiter completion remain separate. Device teardown must reconcile IRQ, MMIO, DMA and Region users separately.

## Guest executable artifacts

The named bounded execution route is:

```text
.NET host -> external provider / adapter
 -> HybridCPU external runtime / CPU model
 -> compiler-produced HCEXE guest -> exact terminal observation
```

[`SingPlus.GuestHost`](../../../tools/SingPlus.GuestHost/SingPlus.GuestHost.csproj), [executable adapter](../../../tools/HybridCpu_ExecutableAdapter/HybridCpu_ExecutableAdapter.csproj), [artifact bind contracts](../../../src/Platform/SingPlus.Platform.Abstractions/PlatformArtifactBindResolutionContracts.cs) and [terminal contracts](../../../src/Platform/SingPlus.Platform.Abstractions/PlatformExecutionTerminalContracts.cs) represent distinct boundaries. Guest Bind, Start, terminal observation, result read, backing cleanup, and charge settlement do not collapse into one success flag. Supported compiler output does not imply arbitrary .NET IL execution, full guest scheduling, or a bare-metal SingNextOS image.

## Virtualization and nested domains

[`VirtualDomainAuthority`](../../../src/Runtime/SingPlus.Runtime/Virtualization/VirtualDomainAuthority.cs) owns local lineage and mapping. The provider owns exact create/transition/revoke acceptance and closure. Model Park/Resume must not be described as general hardware VM state capture.

**Corrected current behavior:** SIP `Message_DestroyAsync` calls `DestroySessionVirtualDomain(_caller, invocation, ...)` in [`RuntimeVirtualizationServiceHost`](../../../src/Runtime/SingPlus.Runtime/Virtualization/RuntimeVirtualizationServiceHost.cs). The method calls `DestroyVirtualDomainCore(..., invocation)` and binds the invocation via `SessionInvocations.BindVirtualTransition` before dependent drain in [`RuntimeKernel.Virtualization`](../../../src/Runtime/SingPlus.Runtime/Virtualization/RuntimeKernel.Virtualization.cs). The old statement that this consumer called direct `DestroyVirtualDomain` without correlation is **obsolete**. This confirms the correlation route, not end-to-end qualification of every failure.

The [host model revoke provider](../../../src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.Virtualization.cs) stores an original revoke fact before the effect and resolves only the same original request, with checked provider incarnation and canonical raw observation. See [bridge original-revoke path](../../../src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.OriginalModelRevokes.cs).

Virtual-I/O [bind/transfer](../../../src/Runtime/SingPlus.Runtime/Virtualization/RuntimeKernel.VirtualIoTransfer.cs) and exact backing pins retain independent lifetimes; an unknown transfer cannot be replayed.

## CXL, SecureCompute and multi-host

CXL sources distinguish [Type-3 memory](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlType3MemoryAuthority.cs), [Type-2 accelerator](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlType2AcceleratorService.cs), [fabric management](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlFabricManagerAuthority.cs) and [multi-host gate](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlMultiHostGate.cs). Topology is not permission; placement is not reservation. [SecureCompute contracts](../../../src/Platform/SingPlus.Platform.Abstractions/PlatformSecureComputeContracts.cs) and [secure runtime](../../../src/Runtime/SingPlus.Runtime/SecureCompute/RuntimeKernel.SecureCompute.cs) do not alone establish a ProductionSecure hardware producer.

Physical remote isolation, external reset containment, and trusted persistent remote continuation remain separate qualification obligations. Do not infer them from disconnected local routes, a software provider or a successful model test.
