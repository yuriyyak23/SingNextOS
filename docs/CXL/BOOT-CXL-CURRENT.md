# CXL boot: текущие контракты SingNextOS

Сверено по локальному исходному дереву 24.10.2026. Это не спецификация
несуществующего Boot ROM/Stage-0 и не доказательство запуска RuntimeKernel.

## Что есть в коде

`SingNext.Boot.Core` содержит bounded boot primitives, candidate selection,
temporary mapping contract и `HybridBootInfoBoundedWriter`. `DirectBootCapsule`
готовит уже аутентифицированный image в normal RAM, проверяет destination SHA-384
и пишет BootInfo; reset-aware путь очищает оба destination при изменении reset
snapshot. `BootCapsuleEntry.Prepare` использует тот же bounded copy/write path.
`BootCapsuleEntry.Run()` по-прежнему возвращает `BootFailure.Unsupported`.

`HybridBootInfoCodec` определяет v1 envelope: 120-byte header, SHA-384, CRC32C,
8-byte aligned records и bounded parser. Runtime importer требует ровно один
Security record с `Required | EvidenceOnly`, 32-byte корректным
`BootSecurityEvidenceV1`, после чего выполняет **fresh** endpoint discovery.
Firmware evidence, BDF, addresses и temporary aperture не становятся runtime
authority. Kernel-owned reader принимает только normal-RAM range вне image и
очищает copy при stale epoch.

## QEMU / native EFI scope

Лаборатория QEMU имеет native EFI discovery, mailbox Identify, HDM, CXL.mem
read/write, development capsule verification и RAM preparation. Она полезна для
guest-level protocol evidence. Она не подключает `BootCapsuleEntry.Run` к
RuntimeKernel, не создаёт production protected state и не доказывает DMA/reset
isolation. Current Windows profile использует RAM-backed Type-3; cold persistence
после QEMU exit не является принятым milestone.

## Ближайшие integration boundaries

1. EFI producer должен формировать фактический Security record из реального
   result verification и пройти `HybridBootInfoCodec.Parse` плюс
   `HybridBootInfoImporter.ImportAndDiscover`. `NoFreshEndpoint`/`ProviderRefused`
   после корректного parsing — scoped next-stage refusal, не successful takeover.
2. Current boot mailbox contract принимает `byte`, а CXL Identify использует
   `0x4000`. Предпочтительное исправление — согласованная `ushort` migration через
   transport, hardware interface, DeadlineMailbox, implementations/callers/tests;
   временный token adapter не заменяет contract migration.
3. Native entry composition требует existing owners и real gates. Создание
   synthetic success при missing DMA isolation, reset authority или fresh provider
   запрещено. ExitBootServices/kernel entry остаются отдельным future stage.

## Код и проверки

* [BootCapsuleEntry.cs](../../src/Kernel/Boot/SingNext.Boot.Capsule/BootCapsuleEntry.cs)
* [DirectBootCapsule.cs](../../src/Kernel/Boot/SingNext.Boot.Capsule/DirectBootCapsule.cs)
* [HybridBootInfoBoundedWriter.cs](../../src/Kernel/Boot/SingNext.Boot.Core/HybridBootInfoBoundedWriter.cs)
* [HybridBootInfoImporter.cs](../../src/Runtime/SingPlus.Runtime/Boot/HybridBootInfoImporter.cs)
* [HybridBootProductionAdapters.cs](../../src/Runtime/SingPlus.Runtime/Boot/HybridBootProductionAdapters.cs)
* [Boot contracts](../../tools/HybridCpu_ExecutableAdapter/Boot.Contracts/BootContracts.cs)
* [Boot tests](../../tests/SingPlus.Tests/Boot/HybridCpuBootAdapterTests.cs)
* [Runtime importer tests](../../tests/SingPlus.Tests/Runtime/HybridBootInfoImporterTests.cs)
* [QEMU laboratory](../QEMU_CXL/README.md)

Новый executable change требует свежих commands/exits и hashes actual inputs;
исторический model pass или сохранённый QEMU summary этого не заменяет.
