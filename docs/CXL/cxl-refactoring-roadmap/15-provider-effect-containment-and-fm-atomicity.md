# Ambiguous provider effects и FM atomicity

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

NotAccepted применим только к proven no-effect отказу. Unavailable/exception
после effect boundary не разрешают local release. Сохранённый original receipt
нужен для cleanup, но сам не даёт fresh publication permission.

Type-2 service и FM admission/drain уже существуют; не реализовывать заново.
Изменения должны сохранить provider callbacks outside owner locks, final source
validation, exact closure и atomic local commit. Cancellation/request sent/timeout
не заменяют provider no-further-use; учитывать independent Region reclaim.

## Действующий код

* [CxlAcceleratorContracts.cs](../../../src/Platform/SingPlus.Platform.Abstractions/CxlAcceleratorContracts.cs)
* [CxlType2AcceleratorService.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlType2AcceleratorService.cs)
* [CxlFabricManagerAuthority.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlFabricManagerAuthority.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [CxlType2AcceleratorServiceTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlType2AcceleratorServiceTests.cs)
* [CxlFabricManagerAuthorityTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlFabricManagerAuthorityTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
