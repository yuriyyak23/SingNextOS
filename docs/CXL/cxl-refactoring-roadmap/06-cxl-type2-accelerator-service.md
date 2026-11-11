# Type-2 staged accelerator service

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

CxlType2AcceleratorService и model provider уже реализованы. Request/submission
несут exact CxlFabricBindingRef (ID + generation), OperationBinding и Region uses;
protected sideband — descriptive metadata, не новая authority.

До submit и publication сохраняются device/fabric/Region/security checks.
Malformed receipt/ambiguous acceptance/failed compensation удерживают original
provider effect и local uses. Cancel success не равен Release/no-further-use.
FM drain и teardown должны закрывать actual provider work до local reclaim.
DirectCoherentWrite явно запрещён до CPU alias exclusion и symmetric coherent
binding release. Флаг capability или CXL.cache не снимает этот gate.

## Действующий код

* [CxlType2AcceleratorService.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlType2AcceleratorService.cs)
* [CxlAcceleratorContracts.cs](../../../src/Platform/SingPlus.Platform.Abstractions/CxlAcceleratorContracts.cs)
* [CxlType2ModelAccelerator.cs](../../../src/Platform/SingPlus.Platform.Host/CxlType2ModelAccelerator.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [CxlType2AcceleratorServiceTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlType2AcceleratorServiceTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
