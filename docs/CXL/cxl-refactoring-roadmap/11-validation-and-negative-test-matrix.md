# Validation по actual consumer

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

Существующие suite files ниже задают места проверки, не current pass counts.
Для source changes выбирать relevant positive/negative/fault/race workload:
original/copy/lost owner, stale/ABA generation, callback reentry, ambiguous effect,
exact repair, Region/use/charge retention и independent foreign backing.

Не повторять full suites для документационной правки. Для runtime changes читать
[dev policy](../../SingNextOS-v6-modernize/03-DEV-VALIDATION.md), проверять actual
package/output adoption и записывать commands/exits/input scope. Старые model
passes, QEMU summaries и current source review — разные уровни доказательства.

## Действующий код

* [CxlTeardownParticipant.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlTeardownParticipant.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [CxlAuthorityBridgeTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlAuthorityBridgeTests.cs)
* [CxlType3MemoryProviderTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlType3MemoryProviderTests.cs)
* [CxlType2AcceleratorServiceTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlType2AcceleratorServiceTests.cs)
* [CxlFabricManagerAuthorityTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlFabricManagerAuthorityTests.cs)
* [CxlMultiHostLifetimeTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlMultiHostLifetimeTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
