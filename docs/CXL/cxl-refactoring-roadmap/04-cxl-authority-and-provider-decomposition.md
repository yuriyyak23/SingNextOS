# CXL provider decomposition и authority

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

Уже существуют отдельные ICxlDiscoveryProvider, ICxlIoProvider,
ICxlFabricProvider, ICxlMemoryProvider, ICxlCoherentAccessProvider и security roles.
CxlAuthorityBridge соединяет их с existing device lease, RegionUse/backing lease
и parent lifetime. Новый top-level CXL authority root не требуется.

Проверки должны происходить до effect и перед publication; equal IDs/record copies
не восстанавливают original tracked owner. Device, fabric, memory и security
versions — разные domains. Failed/malformed/late creation receipts сохраняются
для exact cleanup; потеря routing record не разрешает повторить unbind или
освободить original parent reservation.

## Действующий код

* [CxlProviderContracts.cs](../../../src/Platform/SingPlus.Platform.Abstractions/CxlProviderContracts.cs)
* [CxlAuthorityBridge.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlAuthorityBridge.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [CxlAuthorityBridgeTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlAuthorityBridgeTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
