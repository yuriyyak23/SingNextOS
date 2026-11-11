# Type-3 backing, placement и health consequences

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

CxlType3PlacementPlanner, CxlType3MemoryAuthority и CxlType3ModelProvider уже
существуют. Place резервирует RegionBackingLease, создаёт fabric/memory bindings,
сохраняет original cleanup sources и публикует Active только после final checks.
Backing lease не равен постоянному writable DevicePrivate use.

Refresh может перевести placement в MigrationRequired; это не автоматическая
миграция данных. ObserveAndQuarantine принимает exact endpoint/fabric/memory tuple,
проверяет health evidence и передаёт consequence в RegionAuthority. Evidence само
не мутирует Region и не разрешает reclaim. Callback/reentry/revision drift после
receipt сохраняет quarantine/pins. Модель не является physical CXL.mem backend;
EFI lab доступ к HPA не доказывает runtime backing adoption.

## Действующий код

* [CxlType3PlacementPlanner.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlType3PlacementPlanner.cs)
* [CxlType3MemoryAuthority.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlType3MemoryAuthority.cs)
* [CxlProviderContracts.cs](../../../src/Platform/SingPlus.Platform.Abstractions/CxlProviderContracts.cs)
* [CxlType3ModelProvider.cs](../../../src/Platform/SingPlus.Platform.Host/CxlType3ModelProvider.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [CxlType3MemoryProviderTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlType3MemoryProviderTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
