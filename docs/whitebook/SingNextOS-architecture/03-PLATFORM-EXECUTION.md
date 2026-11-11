# Platform, devices и execution

## Provider boundary

Platform.Abstractions разделяет admission/evidence, memory/device, DMA/MMIO/IRQ, child/guest/artifact, virtualization, SecureCompute и CXL contracts. PlatformAuthorityBridge связывает их с runtime owners и сохраняет original attempts/raw. Provider owns acceptance/closure, bridge receipts остаются correlations.

ExternalOperationAuthority ведёт prepare/admit/submit/terminal/quarantine. Local release до submit и post-submit containment различны. Completion, visibility, publication, settlement и reclaim — самостоятельные commits. Resource/semantic binding относится к exact current source/generation.

Platform.Host содержит software/model producers. Platform.HybridCpu связывает runtime с ExternalRuntime package и executable adapter paths. Успешный software/native adapter тест не является physical qualification. Sibling source edit не обновляет NuGet consumer: нужны actual assets/project checks, новый immutable package при delta и actual adoption.

Original resolution: branded request до effect, canonical raw до callbacks, pure same-original lookup после lost reply. Resolve не повторяет Submit/Bind/Start/Revoke. Missing/copy/foreign/source mismatch сохраняет unknown state и dependent pins. Completed cleanup не требует воскресить active capability.

## Drivers, translation и DMA

Drivers задают console/timer interfaces и manifests. Runtime admission связывает device resources с capabilities/process/provider generation. Contracts разделяют lease, DMA grant/submission/completion/visibility/page fault, MMIO registers/backing и IRQ binding.

Bounded software original DMA copy/backing и actual clients приняты. Request/acquisition/backing удерживаются до effect; unknown reply не повторяет copy. Completion не гарантирует visibility или release RegionUse. Source/destination backing/charges закрываются независимо; foreign resource остаётся живым при original cleanup.

IOMMU/translation mapping принадлежит platform owner. PASID/IOVA не Region identity, invalidate completion не no-further-use independent users. Existing legality/fences сохраняются.

Software MMIO → status → IRQ использует actual producers/clients. Provider delivery и KernelEventRegistry lifetime независимы. Teardown закрывает IRQ/MMIO/device/DMA и RegionUse отдельно. Cross-owner DMA copy не required staged ExternalOperation predecessor для P08; acquisition guard не обходится.

## Guest/artifact execution

Контур: .NET host → external provider → CPU model → compiler-produced HCEXE. Compiler-produced guest поддержка linked entry, arrays/newarr, EH catch/finally/rethrow, nominal slots/reference returns/cctors относится к bounded implemented artifacts, не к произвольной .NET программе.

Artifact Bind/Start, private executable data pages, GC roots, guest quota и original terminal observation — разные lifetimes. Unknown Start не повторяется. Release и surviving-original Start/Bind cleanup реализованы; independent/incapable/general-history users открыты.

Completed означает lifetime finished. Result42 и unhandled exit255 различны. Result/failure read не Resume, permission, independent closure или reclaim. Generic ExecutionFailure не machine MemoryFault/IllegalInstruction producer. DSC1 software backing v2 имеет bounded Kernel/ComputeService adoption.

## Virtualization и nested

VirtualDomainAuthority owns local lineage/mapping; provider owns original create/transition/revoke acceptance/closure. Child/nested factories и parent routes exact original, ancestry metadata не continuity.

Session nested Create и Configure/Park/Resume связывают invocation с original domain fact до effect. Exact no-effect/closure consumed до domain history retirement. Partial retry продолжает cleanup без повторного transition/create/close.

Host original model transition/Revoke используют branded immutable request/raw и pure canonical resolver. Fact регистрируется до metadata effect. Unknown query retains quarantine/charge. Closed metadata остаётся для warm resolution; heap-history quantitative reclaim этим не заявляется. Model Park/Resume не executable state capture/resume.

Virtual-I/O Bind/Transfer/Close сохраняют original attempts/private backing. Unknown Transfer не повторяется. CloseIo может отдельно исполниться после exact custody handoff; response cancellation не снимает backing pins. Native drain прекращает dispatch и reconciles request/response, не подменяя provider teardown.

Current gap: DestroyAsync host ещё вызывает direct DestroyVirtualDomain без invocation correlation. Это следующий consumer modernize/01.

## CXL и SecureCompute

CXL разделяет Type3 memory authority/placement, Type2 accelerator, fabric management, trust/security и multihost gate. Topology не permission, placement не reservation. Local Fence/teardown сохраняют RegionUse/backing/charge. Positive remote isolation/no-further-use Missing/ExternalBlocked; disconnect/local deny не physical containment.

SecureCompute имеет отдельный domain/policy owner и provider contracts. Attestation — named evidence с freshness, не permission. ProductionSecure producer отсутствует; fake/model не замена. Protected IFC/secure Type2 требуют exact device/evidence generations. Multi-host leases, physical reset и persistent remote state имеют отдельные claims.

## Источники

- [DMA contract](../../../src/Platform/SingPlus.Platform.Abstractions/PlatformDmaGrantContracts.cs)
- [Host virtualization](../../../src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.Virtualization.cs)
- [Virtualization host](../../../src/Runtime/SingPlus.Runtime/Virtualization/RuntimeVirtualizationServiceHost.cs)
- [Virtual-I/O](../../../src/Runtime/SingPlus.Runtime/Virtualization/RuntimeKernel.VirtualIo.cs)
- [CXL multihost](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlMultiHostGate.cs)
- [DMA ADR](../../SingNextOS-v6-roadmap-reworked-2026-09-23/ADR-032-ORIGINAL-NATIVE-DMA-COPY.md)
- [DSC1 ADR](../../SingNextOS-v6-roadmap-reworked-2026-09-23/ADR-054-ORIGINAL-SOFTWARE-DSC1-BACKING.md)
