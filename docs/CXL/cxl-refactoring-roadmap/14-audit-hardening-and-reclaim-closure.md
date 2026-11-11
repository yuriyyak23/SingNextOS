# Exact lifetime и reclaim closure

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

Existing CXL owners зарегистрированы в RuntimeKernel teardown barrier.
Closure относится к original invocation/binding/parent, а не только равному ID
или последнему provider query. Backing/use/charges удерживаются до actual closure;
неизвестный outcome остаётся quarantined. Type-3 backing lease и Type-2 use lifetime
не объединяются. Новое owner поле или helper не является adoption consumer.

При изменении проверять callback boundary, late route/parent loss, foreign/copy,
reentry и same-original retry без повторного provider effect. Старая декларация
«все программные gaps закрыты, остался только hardware» не является актуальным
вердиктом текущего дерева.

## Действующий код

* [CxlAuthorityBridge.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlAuthorityBridge.cs)
* [CxlType3MemoryAuthority.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlType3MemoryAuthority.cs)
* [CxlTeardownParticipant.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlTeardownParticipant.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [CxlAuthorityBridgeTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlAuthorityBridgeTests.cs)
* [CxlType3MemoryProviderTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlType3MemoryProviderTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
