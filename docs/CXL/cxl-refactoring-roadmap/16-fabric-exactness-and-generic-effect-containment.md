# Fabric original sources и effect containment

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

Runtime FM удерживает original begin/completion invocations, source revision,
ticket и known replacement. Pool release валидирует exact original assignment;
операции нельзя забыть до actual drain. Поздний equal-written/copy record не
восстанавливает authority, lost query не доказывает отсутствия effect.

Это existing source path с dedicated fault/race tests, не обещание универсальной
bug-free модели. Следующий fix требует actual failing consequence и проверки
original repair, independent peer/backing и quantitative closure; новые ledgers
или повторная широкая roadmap campaign без consumer не нужны.

## Действующий код

* [CxlFabricManagerAuthority.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlFabricManagerAuthority.cs)
* [CxlAuthorityBridge.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlAuthorityBridge.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [CxlFabricManagerAuthorityTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlFabricManagerAuthorityTests.cs)
* [CxlAuthorityBridgeTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlAuthorityBridgeTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
