# Fabric Manager, pooling и reconfiguration

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

CxlFabricManagerAuthority уже реализует Register, ExecuteAdmission,
TrackOperation, Begin/CompleteReconfiguration и pool assignment/release.
Provider callbacks выполняются с отдельной final validation existing source;
original invocation/ticket/record revision сохраняются через lost replies.

Draining закрывает admission и требует provider closure tracked operations.
Query failure/NotFound не доказывают drain. Полученный replacement receipt может
сохраниться для cleanup, не разрешая publication или повторный completion effect.
Exact pool closure и Region reclaim — независимые consequences. Нельзя удалять
correlation при живом work или принимать copied/equal-written history за original.
P2P evidence не предоставляет Region authority; модель FM не physical enforcement.

## Действующий код

* [CxlFabricManagerAuthority.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlFabricManagerAuthority.cs)
* [CxlFabricManagementContracts.cs](../../../src/Platform/SingPlus.Platform.Abstractions/CxlFabricManagementContracts.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [CxlFabricManagerAuthorityTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlFabricManagerAuthorityTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
