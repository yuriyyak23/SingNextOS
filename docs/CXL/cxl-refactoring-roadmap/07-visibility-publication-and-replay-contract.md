# Visibility, publication и replay

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

Effect/replay policy и lifecycle уже представлены в ExternalOperations contracts.
Сохранять различие DeviceComplete, visibility evidence, publication action и release.
Replay evidence не разрешает повторный provider effect; staged output не становится
опубликованным только из-за completion или transport success.

HybridCpuExternalOperationProvider хранит original requests/bindings и порядок
receipts. Generation ABA, duplicate submit, callback reentry и late provider drift
не должны повторять publication или терять cleanup. Direct coherent output
остаётся закрытым; snapshot/idempotence/barrier requirements не обходят authority.

## Действующий код

* [ExternalOperations.cs](../../../contracts/SingPlus.Contracts/ExternalOperations.cs)
* [ExternalOperationAuthority.cs](../../../src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs)
* [HybridCpuExternalOperationProvider.cs](../../../src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [ExternalEffectPolicyTests.cs](../../../tests/SingPlus.Tests/Runtime/ExternalEffectPolicyTests.cs)
* [HybridCpuExternalOperationProviderTests.cs](../../../tests/SingPlus.Tests/Runtime/HybridCpuExternalOperationProviderTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
