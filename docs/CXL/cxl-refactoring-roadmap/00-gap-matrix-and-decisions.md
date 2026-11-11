# Baseline и границы CXL

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

CXL runtime уже реализован через provider roles и existing Region authority.
Создавать заново MutationEpoch, RegionUse, external lifecycle, Type-3 placement,
Type-2 staged service или Fabric Manager не требуется. Наличие реализации не
закрывает все возможные bugs и не квалифицирует physical provider.

CXL.io использует обычные device/MMIO/IRQ/DMA contracts; CXL.mem — backing и
placement; coherent access — отдельный provider role. BDF/DPA/HDM/route остаются
внутри platform/provider. Logical endpoint/binding IDs не заменяют capability.

Следующую runtime работу выбирать по actual consumer и единственной v6 очереди.
QEMU EFI lab развивается отдельно от runtime provider adoption; его актуальные
команды и evidence — в [QEMU_CXL](../../QEMU_CXL/README.md).

## Действующий код

* [CxlAuthorityBridge.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlAuthorityBridge.cs)
* [CxlProviderContracts.cs](../../../src/Platform/SingPlus.Platform.Abstractions/CxlProviderContracts.cs)
* [RegionAuthority.cs](../../../src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [CxlAuthorityBridgeTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlAuthorityBridgeTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
