# QCXL-05: source/caller audit

**26.10.2026.** Docs/source review, без нового build/VM или executable pass.

## Existing consumers и отсутствие startup caller

[HybridBootProductionAdapters](../../src/Runtime/SingPlus.Runtime/Boot/HybridBootProductionAdapters.cs) содержит KernelOwnedPhysicalBootInfoReader, HybridBootInfoEntryAdapter и HybridBootRuntimeHandoff.Create. В production src/tools actual native construction/call site этого handoff не найден. [HybridBootAuthorityAdmission](../../src/Runtime/SingPlus.Runtime/Boot/HybridBootAuthorityAdmission.cs) уже revalidates fresh provider и отвергает quarantined aperture.

[KernelEntryPoint](../../src/Kernel/SingPlus.Kernel/Boot/KernelEntryPoint.cs) вызывается [host-debug Program](../../src/Kernel/Boot/SingPlus.Boot/Program.cs); User/ManagedGc/Host HAL profile не native guest startup. [BootCapsuleEntry.Run](../../src/Kernel/Boot/SingNext.Boot.Capsule/BootCapsuleEntry.cs) возвращает Unsupported, не build failure. Tests individual adapters не actual native caller.

## Четыре real input owners

1. Actual loader HBI1 physical address + exact byte length.
2. Kernel-owned physical reader/copy с authoritative whole-range NormalRam classification, current epoch и image-overlap denial.
3. Fresh endpoint enumeration/discovery provider, независимый от firmware identities.
4. Original aperture owner с proven retirement либо quarantine и fresh revalidation перед publication.

Current source имеет contracts для kernel map/copy/epoch, но concrete native producers не найдены. CxlType3ModelProvider — host model. Existing managed hardware implementation — UnsupportedHybridCpuBootHardware. Lab RamStage после observer ACK clear/free исходную RAM; источник не передан kernel.

## N1 result / classification

[N1 разрешён как design и завершён](blockers-singnextos/SNO-01-N1-TARGET-ABI-RU.md). Receiver contract предполагает post-EBS x64 runtime/heap и retained original read loan до VM terminal. Supported actual guest runtime/PAL/bootstrap отсутствует, что подтвердил пользователь. Original aperture proof также Missing: decoder disable не CpuRouteClosed/DmaAndOtherEffectsClosed.

QCXL-05/06 **ExternalBlocked**, не helper/API gap. Нужны real substrate/owners и отдельное implementation/EBS/runtime execution authorization. После этого existing Create → Import → HybridBootAuthorityAdmission, без нового importer или success bypass. BootInfo HBI1 immutable evidence не live capability; HBV1 — volume magic.
