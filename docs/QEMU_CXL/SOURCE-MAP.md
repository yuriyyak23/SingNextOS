# Actual source / consumer map

Сверка26.10.2026. Классы/contracts/tests не доказывают actual native startup. [Current state](CURRENT-STATE-RU.md), [evidence epochs](evidence/README-RU.md).

## Runtime и shared contracts

| Source | Реальная роль / remaining boundary |
|---|---|
| [BootCapsuleEntry](../../src/Kernel/Boot/SingNext.Boot.Capsule/BootCapsuleEntry.cs), [DirectBootCapsule](../../src/Kernel/Boot/SingNext.Boot.Capsule/DirectBootCapsule.cs) | Run Unsupported; bounded Prepare/copy/reset-aware seams не native execution |
| [ManifestAndLoader](../../src/Kernel/Boot/SingNext.Boot.Core/ManifestAndLoader.cs), [ProtectedBootStateMachine](../../src/Kernel/Boot/SingNext.Boot.Core/ProtectedBootStateMachine.cs), [BootSelection](../../src/Kernel/Boot/SingNext.Boot.Core/BootSelection.cs) | Existing trust/image/selection/state consumers; independent provisioning/durable store Missing |
| [BootPorts](../../src/Kernel/Boot/SingNext.Boot.Core/BootPorts.cs), [BootContracts](../../tools/HybridCpu_ExecutableAdapter/Boot.Contracts/BootContracts.cs), [BootWire](../../tools/HybridCpu_ExecutableAdapter/Boot.Contracts/BootWire.cs), [bounded writer](../../src/Kernel/Boot/SingNext.Boot.Core/HybridBootInfoBoundedWriter.cs) | ushort mailbox, distinct reset/isolation/mapping contracts. HBV1 volume, SNB1 manifest, HBI1 BootInfo |
| [HybridCpuBootSession](../../src/Platform/SingPlus.Platform.HybridCpu.Boot/HybridCpuBootSession.cs) | Guarded PCI/CCI; concrete native managed hardware Missing, Unsupported implementation deny |
| [PciCxlDiscovery](../../src/Platform/SingPlus.Platform.HybridCpu.Boot/PciCxlDiscovery.cs), [CxlPciDvsecParser](../../src/Platform/SingPlus.Platform.HybridCpu.Boot/CxlPciDvsecParser.cs), [TransactionalHdmMapping](../../src/Platform/SingPlus.Platform.HybridCpu.Boot/TransactionalHdmMapping.cs) | Existing bounded discovery/parser/transactional Map/Retire; не второй native C owner |
| [HybridBootProductionAdapters](../../src/Runtime/SingPlus.Runtime/Boot/HybridBootProductionAdapters.cs), [HybridBootInfoImporter](../../src/Runtime/SingPlus.Runtime/Boot/HybridBootInfoImporter.cs), [HybridBootAuthorityAdmission](../../src/Runtime/SingPlus.Runtime/Boot/HybridBootAuthorityAdmission.cs) | NormalRam copy/epoch/image guard → fresh discovery/import → revalidation; no actual native startup caller |
| [CxlAuthorityBridge](../../src/Runtime/SingPlus.Runtime/Cxl/CxlAuthorityBridge.cs), [CxlType3MemoryAuthority](../../src/Runtime/SingPlus.Runtime/Cxl/CxlType3MemoryAuthority.cs), [provider contracts](../../src/Platform/SingPlus.Platform.Abstractions/CxlProviderContracts.cs) | Runtime authority/closure owners; source evidence/firmware identity не capability |
| [KernelEntryPoint](../../src/Kernel/SingPlus.Kernel/Boot/KernelEntryPoint.cs), [SingPlus.Boot Program](../../src/Kernel/Boot/SingPlus.Boot/Program.cs), [HostDebugBootPipeline](../../src/Kernel/Boot/SingPlus.Boot/HostDebugBootPipeline.cs) | Host-debug CLR path, не x86-64 UEFI/kernel bootstrap. [N1 assessment](blockers-singnextos/SNO-01-N1-TARGET-ABI-RU.md) |

## Native lab

| Source | Роль |
|---|---|
| [Lab project](../../tools/SingNext.CxlLab/SingNext.CxlLab.csproj) | References actual Boot.Capsule/Core/HybridCpu.Boot, без копии contracts |
| [NativePreparationOwner](../../tools/SingNext.CxlLab/efi/NativePreparationOwner.c), [NativeLifecycleClient](../../tools/SingNext.CxlLab/efi/NativeLifecycleClient.c) | Original record/snapshot/effects/RAM owner и opt-in A currentness; pins/lifetime зависят от выбранного profile |
| [PCI](../../tools/SingNext.CxlLab/efi/PciDiscovery.c), [ACPI](../../tools/SingNext.CxlLab/efi/AcpiDiscovery.c), [NativeMailbox](../../tools/SingNext.CxlLab/efi/NativeMailbox.c) | Native discovery/Identify0x4000; direct wire не managed session adoption |
| [NativeHdm](../../tools/SingNext.CxlLab/efi/NativeHdm.c), [NativeCapsule](../../tools/SingNext.CxlLab/efi/NativeCapsule.c), [CxlRead](../../tools/SingNext.CxlLab/efi/CxlRead.c), [RamStage](../../tools/SingNext.CxlLab/efi/RamStage.c) | HDM/FAT→fresh CXL RAM/guest crypto/authenticated snapshot/RAM/HBI1; observer ACK clear/free, не kernel transfer |
| [LabHdmHardware](../../tools/SingNext.CxlLab/LabHdmHardware.cs), [NativePreparationSession](../../tools/SingNext.CxlLab/NativePreparationSession.cs), [NativeOwnerEvidence](../../tools/SingNext.CxlLab/NativeOwnerEvidence.cs) | Host Shell/managed transaction и original metadata witnesses; не guest reset authority |
| [LabRamPreparationWitness](../../tools/SingNext.CxlLab/LabRamPreparationWitness.cs), [CoreResetContractQualification](../../tools/SingNext.CxlLab/CoreResetContractQualification.cs) | Host copy/reset-contract experiment, не runtime/physical isolation |

## External DMA tools

В связанном ISE repo — standalone external tools, не arch/core/ISA:

- [CxlIommuFeasibility](<../../../HybridCPU ISE/Tools/CxlIommuFeasibility/CxlIommuFeasibility.csproj>), [DmaEnforcement](<../../../HybridCPU ISE/Tools/CxlIommuFeasibility/DmaEnforcement.cs>), [VtdGrantOwner](<../../../HybridCPU ISE/Tools/CxlIommuFeasibility/VtdGrantOwner.cs>), [VtdIo](<../../../HybridCPU ISE/Tools/CxlIommuFeasibility/VtdIo.cs>), [DmaBuffers](<../../../HybridCPU ISE/Tools/CxlIommuFeasibility/efi/DmaBuffers.c>): actual controlled grant/retire/invalidation/fault и native allocation owner. Host reference tokens не serialized boot authority.
- [CxlLifecycleLab](<../../../HybridCPU ISE/Tools/CxlLifecycleLab/README-RU.md>), [NativeIsolation](<../../../HybridCPU ISE/Tools/CxlLifecycleLab/NativeIsolation.cs>), [NativeMapping](<../../../HybridCPU ISE/Tools/CxlLifecycleLab/NativeMapping.cs>): A producer, reused source-linked VtdGrantOwner, original operation/generations, existing native consumer. V2 mapping deny; V3 actual CPU route witness → snapshot/RAM.
- [CallbackDebug](<../../../HybridCPU ISE/Tools/CxlLifecycleLab/CallbackDebug.cs>): actual EFI callback instruction-window observer, hardware breakpoints/private pipe, не producer authority.
- [PendingDma](<../../../HybridCPU ISE/Tools/CxlLifecycleLab/PendingDma.cs>): actual pending EDU command, original pins/terminal reset/loss, independent VM peer → completion → native RAM.
- [CxlDmaTarget](<../../../HybridCPU ISE/Tools/CxlLifecycleLab/CxlDmaTarget.cs>): original proved-retire/table reuse, low IOVA→high CXL scratch HPA, ABA/revoke faults. Native admission permanently withdrawn до diagnostic grant.

## Tests и запуск

[Adapter tests](../../tests/SingPlus.Tests/Runtime/HybridBootProductionAdapterTests.cs), [importer tests](../../tests/SingPlus.Tests/Runtime/HybridBootInfoImporterTests.cs), [authority tests](../../tests/SingPlus.Tests/Runtime/HybridBootAuthorityAdmissionTests.cs), [core semantic tests](../../tests/SingPlus.Tests/Boot/DirectBootCoreSemanticTests.cs) — tests existing consumers, не native caller.

[Scripts](../../tools/qemu-cxl/README-RU.md), [lab README](../../tools/SingNext.CxlLab/README.md), [legacy qualification script](../../tools/cxl-qemu-qualification.ps1). Только exact report/launch определяет tested path/profile; current runtime handoff нельзя вывести из наличия API или binary.
